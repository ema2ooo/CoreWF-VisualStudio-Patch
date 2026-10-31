using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MAttributes = Mono.Cecil.MethodAttributes;
using TAttributes = Mono.Cecil.TypeAttributes;

namespace CoreWF.LegacyAdapters
{
    // Only public design-time metadata is projected. Business method bodies are never copied
    // or executed inside the CLR4 designer. The project's runtime output is never modified.
    public static class BuiltActivityMetadata
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> Identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CachedAssembly> Cache = new Dictionary<string, CachedAssembly>(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedAssembly
        {
            internal long Length;
            internal DateTime Modified;
            internal AssemblyName Identity;
        }

        public static void ReportDiscoveryError(Exception error)
        {
            Trace("Discovery failed: " + error);
        }

        private static void Trace(string message)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreWF.DesignerPatch");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "activity-discovery.log"), DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public static bool IsDesignTimeAssembly(string name) { lock (Gate) return name != null && Identities.Contains(name); }

        public static AssemblyName MapBuiltAssembly(AssemblyName input)
        {
            if (input == null || string.IsNullOrEmpty(input.CodeBase)) return input;
            string path = Uri.TryCreate(input.CodeBase, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : input.CodeBase;
            if (!File.Exists(path)) return input;
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (Cache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
                    return (AssemblyName)cached.Identity.Clone();
                byte[] bytes = File.ReadAllBytes(path);
                using (var resolver = new DefaultAssemblyResolver())
                {
                    resolver.AddSearchDirectory(Path.GetDirectoryName(path));
                    using (var source = ModuleDefinition.ReadModule(new MemoryStream(bytes), new ReaderParameters { AssemblyResolver = resolver }))
                    {
                        bool modern = source.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute" &&
                            a.ConstructorArguments.Count == 1 && (((string)a.ConstructorArguments[0].Value).StartsWith(".NETCoreApp,") ||
                            ((string)a.ConstructorArguments[0].Value).StartsWith(".NETStandard,")));
                        if (!modern) return input;
                        var activities = source.GetTypes().Where(IsActivity).ToArray();
                        if (activities.Length == 0) return input;
                        if (source.Assembly.Name.HasPublicKey)
                            throw new NotSupportedException("Signed modern activity assemblies require a Framework-compatible design-time build: " + source.Assembly.Name.Name);
                        string digest;
                        using (var hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
                        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreWF.DesignerPatch", "ActivityMetadata-v2", digest);
                        string destination = Path.Combine(directory, source.Assembly.Name.Name + ".dll");
                        if (!File.Exists(destination))
                        {
                            Directory.CreateDirectory(directory);
                            using (var generated = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(source.Assembly.Name.Name, source.Assembly.Name.Version), source.Assembly.Name.Name, ModuleKind.Dll))
                            {
                                var emitter = new Emitter(source, generated.MainModule);
                                foreach (var activity in activities) emitter.Emit(activity);
                                string temporary = destination + "." + Guid.NewGuid().ToString("N");
                                generated.Write(temporary);
                                try { File.Move(temporary, destination); }
                                catch (IOException) { if (!File.Exists(destination)) throw; File.Delete(temporary); }
                            }
                        }
                        var identity = AssemblyName.GetAssemblyName(destination);
                        identity.CodeBase = destination;
                        Identities.Add(identity.FullName);
                        Cache[path] = new CachedAssembly { Length = info.Length, Modified = info.LastWriteTimeUtc, Identity = identity };
                        Trace("Mapped " + path + " to " + destination + " (" + activities.Length + " activities)");
                        return identity;
                    }
                }
            }
        }

        private static bool IsActivity(TypeDefinition type)
        {
            if ((!type.IsPublic && !type.IsNestedPublic) || type.IsInterface) return false;
            var visited = new HashSet<string>();
            for (TypeReference parent = type.BaseType; parent != null && visited.Add(parent.FullName);)
            {
                if (parent.Namespace == "System.Activities" &&
                    new[] { "Activity", "Activity`1", "NativeActivity", "NativeActivity`1", "CodeActivity", "CodeActivity`1", "AsyncCodeActivity", "AsyncCodeActivity`1" }.Contains(parent.Name)) return true;
                try { parent = parent.Resolve()?.BaseType; } catch { return false; }
            }
            return false;
        }

        private sealed class Emitter
        {
            private readonly ModuleDefinition source;
            private readonly ModuleDefinition output;
            private readonly Dictionary<string, TypeDefinition> types = new Dictionary<string, TypeDefinition>();
            public Emitter(ModuleDefinition source, ModuleDefinition output) { this.source = source; this.output = output; }

            public TypeDefinition Emit(TypeDefinition original)
            {
                if (types.TryGetValue(original.FullName, out var existing)) return existing;
                if (original.IsValueType && !original.IsEnum)
                    throw new NotSupportedException("The Framework designer needs a compatible design-time build for this activity value type: " + original.FullName);
                var type = new TypeDefinition(original.Namespace, original.Name, TAttributes.Public | TAttributes.Class | TAttributes.BeforeFieldInit,
                    output.ImportReference(original.IsEnum ? typeof(Enum) : typeof(object)));
                if (original.IsAbstract) type.IsAbstract = true;
                if (original.IsNested) type.Attributes = (type.Attributes & ~TAttributes.VisibilityMask) | TAttributes.NestedPublic;
                types.Add(original.FullName, type);
                if (original.IsNested) Emit(original.DeclaringType).NestedTypes.Add(type); else output.Types.Add(type);
                foreach (var parameter in original.GenericParameters) type.GenericParameters.Add(new GenericParameter(parameter.Name, type));
                if (original.IsEnum)
                {
                    foreach (var field in original.Fields)
                    {
                        var copy = new FieldDefinition(field.Name, field.Attributes, field.Name == "value__" ? Map(field.FieldType, type) : type);
                        if (field.HasConstant) copy.Constant = field.Constant;
                        type.Fields.Add(copy);
                    }
                    return type;
                }
                bool activity = IsActivity(original);
                TypeReference parent = original.BaseType;
                if (activity)
                {
                    while (parent != null && parent.Namespace != "System.Activities") parent = parent.Resolve().BaseType;
                    // Declarative activities are opaque native activities in the design-only view.
                    if (parent.Name == "Activity" || parent.Name == "Activity`1")
                    {
                        var replacement = parent is GenericInstanceType generic ? (TypeReference)new GenericInstanceType(output.ImportReference(typeof(System.Activities.NativeActivity<>))) : output.ImportReference(typeof(System.Activities.NativeActivity));
                        if (replacement is GenericInstanceType target) target.GenericArguments.Add(Map(((GenericInstanceType)parent).GenericArguments[0], type));
                        type.BaseType = replacement;
                    }
                    else type.BaseType = Map(parent, type);
                }
                var constructor = new MethodDefinition(".ctor", MAttributes.Public | MAttributes.HideBySig | MAttributes.SpecialName | MAttributes.RTSpecialName, output.TypeSystem.Void);
                type.Methods.Add(constructor);
                var baseConstructor = new MethodReference(".ctor", output.TypeSystem.Void, type.BaseType) { HasThis = true };
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, baseConstructor));
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var properties = new List<PropertyDefinition>(original.Properties);
                if (activity)
                {
                    var ancestor = original.BaseType;
                    while (ancestor != null && ancestor.Namespace != "System.Activities")
                    {
                        var definition = ancestor.Resolve();
                        properties.AddRange(definition.Properties.Where(p => properties.All(own => own.Name != p.Name)));
                        ancestor = definition.BaseType;
                    }
                }
                foreach (var property in properties.Where(p => p.Parameters.Count == 0 && p.GetMethod != null && p.GetMethod.IsPublic && !p.GetMethod.IsStatic))
                {
                    var propertyType = Map(property.PropertyType, type);
                    var field = new FieldDefinition("_" + property.Name, Mono.Cecil.FieldAttributes.Private, propertyType);
                    type.Fields.Add(field);
                    CopyLiteralInitializer(property, field, constructor);
                    var copy = new PropertyDefinition(property.Name, Mono.Cecil.PropertyAttributes.None, propertyType);
                    type.Properties.Add(copy);
                    var get = new MethodDefinition("get_" + property.Name, MAttributes.Public | MAttributes.HideBySig | MAttributes.SpecialName, propertyType);
                    get.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); get.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, field)); get.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    type.Methods.Add(get); copy.GetMethod = get;
                    if (property.SetMethod != null && property.SetMethod.IsPublic)
                    {
                        var set = new MethodDefinition("set_" + property.Name, MAttributes.Public | MAttributes.HideBySig | MAttributes.SpecialName, output.TypeSystem.Void);
                        set.Parameters.Add(new ParameterDefinition("value", Mono.Cecil.ParameterAttributes.None, propertyType));
                        set.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); set.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); set.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, field)); set.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        type.Methods.Add(set); copy.SetMethod = set;
                    }
                    foreach (var attribute in property.CustomAttributes.Where(a => a.AttributeType.FullName == "System.Activities.RequiredArgumentAttribute" || a.AttributeType.Namespace == "System.ComponentModel"))
                    {
                        if (attribute.HasProperties || attribute.HasFields) continue;
                        try
                        {
                            var attributeType = RuntimeType(attribute.AttributeType.FullName);
                            var parameters = attribute.Constructor.Parameters.Select(p => RuntimeType(p.ParameterType.FullName)).ToArray();
                            var runtimeConstructor = attributeType.GetConstructor(parameters);
                            if (runtimeConstructor == null) continue;
                            var cloned = new CustomAttribute(output.ImportReference(runtimeConstructor));
                            foreach (var argument in attribute.ConstructorArguments) cloned.ConstructorArguments.Add(new CustomAttributeArgument(Map(argument.Type, type), argument.Value));
                            copy.CustomAttributes.Add(cloned);
                        }
                        catch (NotSupportedException) { }
                    }
                }
                if (activity)
                {
                    bool asynchronous = type.BaseType.Name.StartsWith("AsyncCodeActivity");
                    bool code = type.BaseType.Name.StartsWith("CodeActivity");
                    var result = type.BaseType is GenericInstanceType generic ? generic.GenericArguments[0] : output.TypeSystem.Void;
                    if (asynchronous)
                    {
                        AddThrow(type, "BeginExecute", typeof(IAsyncResult), typeof(System.Activities.AsyncCodeActivityContext), typeof(AsyncCallback), typeof(object));
                        AddThrow(type, "EndExecute", result, typeof(System.Activities.AsyncCodeActivityContext), typeof(IAsyncResult));
                    }
                    else AddThrow(type, "Execute", code ? result : output.TypeSystem.Void, code ? typeof(System.Activities.CodeActivityContext) : typeof(System.Activities.NativeActivityContext));
                }
                return type;
            }

            private void CopyLiteralInitializer(PropertyDefinition property, FieldDefinition field, MethodDefinition constructor)
            {
                var original = property.DeclaringType.Methods.FirstOrDefault(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
                if (original == null || !original.HasBody) return;
                var store = original.Body.Instructions.FirstOrDefault(instruction => instruction.OpCode.Code == Code.Stfld &&
                    instruction.Operand is FieldReference target && target.Name == "<" + property.Name + ">k__BackingField");
                if (store == null) return;
                Instruction value = store.Previous;
                var instructions = new List<Instruction> { Instruction.Create(OpCodes.Ldarg_0) };
                if (value.OpCode.Code == Code.Newobj && value.Operand is MethodReference creation &&
                    creation.DeclaringType.Namespace == "System.Activities" && creation.DeclaringType.Name == "InArgument`1" && creation.Parameters.Count <= 1)
                {
                    if (creation.Parameters.Count == 1)
                    {
                        Instruction literal = Literal(value.Previous);
                        if (literal == null || value.Previous.Previous?.OpCode.Code != Code.Ldarg_0) throw UnsupportedInitializer(property);
                        instructions.Add(literal);
                    }
                    else if (value.Previous?.OpCode.Code != Code.Ldarg_0) throw UnsupportedInitializer(property);
                    var target = new MethodReference(".ctor", output.TypeSystem.Void, field.FieldType) { HasThis = true };
                    if (creation.Parameters.Count == 1)
                    {
                        var parameter = creation.Parameters[0].ParameterType;
                        target.Parameters.Add(new ParameterDefinition(parameter is GenericParameter ?
                            ((GenericInstanceType)field.FieldType).ElementType.GenericParameters[0] : Map(parameter, field.DeclaringType)));
                    }
                    instructions.Add(Instruction.Create(OpCodes.Newobj, target));
                }
                else
                {
                    Instruction literal = Literal(value);
                    if (literal == null || value.Previous?.OpCode.Code != Code.Ldarg_0) throw UnsupportedInitializer(property);
                    instructions.Add(literal);
                }
                instructions.Add(Instruction.Create(OpCodes.Stfld, field));
                var il = constructor.Body.GetILProcessor();
                foreach (var instruction in instructions) il.InsertBefore(constructor.Body.Instructions.Last(), instruction);
            }

            private static NotSupportedException UnsupportedInitializer(PropertyDefinition property) => new NotSupportedException(
                "This activity property has a runtime-dependent initializer and needs a Framework-compatible design-time build: " + property.FullName);

            private static Instruction Literal(Instruction instruction)
            {
                if (instruction == null) return null;
                switch (instruction.OpCode.Code)
                {
                    case Code.Ldnull: case Code.Ldc_I4_M1: case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2:
                    case Code.Ldc_I4_3: case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8:
                        return Instruction.Create(instruction.OpCode);
                    case Code.Ldc_I4: return Instruction.Create(instruction.OpCode, (int)instruction.Operand);
                    case Code.Ldc_I4_S: return Instruction.Create(instruction.OpCode, (sbyte)instruction.Operand);
                    case Code.Ldc_I8: return Instruction.Create(instruction.OpCode, (long)instruction.Operand);
                    case Code.Ldc_R4: return Instruction.Create(instruction.OpCode, (float)instruction.Operand);
                    case Code.Ldc_R8: return Instruction.Create(instruction.OpCode, (double)instruction.Operand);
                    case Code.Ldstr: return Instruction.Create(instruction.OpCode, (string)instruction.Operand);
                    default: return null;
                }
            }

            private void AddThrow(TypeDefinition type, string name, object result, params Type[] parameters)
            {
                var method = new MethodDefinition(name, MAttributes.Family | MAttributes.Virtual | MAttributes.HideBySig,
                    result is Type runtime ? output.ImportReference(runtime) : (TypeReference)result);
                foreach (var parameter in parameters) method.Parameters.Add(new ParameterDefinition(output.ImportReference(parameter)));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "This is a design-time metadata view. Run the original activity in its modern .NET application."));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, output.ImportReference(typeof(NotSupportedException).GetConstructor(new[] { typeof(string) }))));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
                type.Methods.Add(method);
            }

            private TypeReference Map(TypeReference reference, TypeDefinition owner)
            {
                if (reference is GenericParameter parameter) return owner.GenericParameters[parameter.Position];
                if (reference is GenericInstanceType instance)
                {
                    var result = new GenericInstanceType(Map(instance.ElementType, owner));
                    foreach (var argument in instance.GenericArguments) result.GenericArguments.Add(Map(argument, owner));
                    return result;
                }
                if (reference is ArrayType array) return new ArrayType(Map(array.ElementType, owner), array.Rank);
                var local = source.GetTypes().FirstOrDefault(t => t.FullName == reference.FullName);
                if (local != null) return Emit(local);
                return output.ImportReference(RuntimeType(reference.FullName));
            }

            private static Type RuntimeType(string name)
            {
                foreach (var assembly in new[] { typeof(object).Assembly, typeof(System.Activities.Activity).Assembly, typeof(System.ComponentModel.DefaultValueAttribute).Assembly, typeof(Uri).Assembly, typeof(System.Linq.Enumerable).Assembly })
                {
                    var type = assembly.GetType(name.Replace('/', '+'));
                    if (type != null) return type;
                }
                throw new NotSupportedException("The Framework designer cannot represent this public activity property type: " + name);
            }
        }
    }
}
