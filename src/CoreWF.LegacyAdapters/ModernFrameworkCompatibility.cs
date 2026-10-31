using System;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.Shell.Interop;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Globalization;

namespace CoreWF.LegacyAdapters
{
    public static class ModernFrameworkCompatibility
    {
        public static CultureInfo CreateReferenceCulture(string name)
        {
            // SDK reference automation can report null for a neutral assembly.
            return new CultureInfo(name ?? string.Empty);
        }

        public static bool ShouldSkipPartitionedBuildProbe(IVsHierarchy hierarchy)
        {
            if (hierarchy == null) return false;
            try
            {
                object value;
                // The project system already knows the target framework. Evaluating a new
                // MSBuild project here blocks the UI during every SDK reference notification.
                if (hierarchy.GetProperty(0xFFFFFFFEu,
                    (int)__VSHPROPID4.VSHPROPID_TargetFrameworkMoniker, out value) < 0) return false;
                string framework = value as string;
                return framework != null && (framework.StartsWith(".NETCoreApp,", StringComparison.Ordinal) ||
                    framework.StartsWith(".NETStandard,", StringComparison.Ordinal));
            }
            catch (COMException) { return false; }
        }

        // This callback configures the CLR4 designer, never the project's build/runtime.
        public static void NormalizeDesignerFramework(ref string targetFramework)
        {
            if (string.IsNullOrWhiteSpace(targetFramework)) return;
            FrameworkName framework;
            try { framework = new FrameworkName(targetFramework); }
            catch (ArgumentException) { return; }
            if (framework.Identifier == ".NETCoreApp" || framework.Identifier == ".NETStandard")
                targetFramework = ".NETFramework,Version=v4.8";
        }

        public static IVsDesignTimeAssemblyResolution SelectDesignerResolver(IVsDesignTimeAssemblyResolution resolver)
        {
            if (resolver == null) return null;
            string framework = null;
            if (resolver.GetTargetFramework(out framework) < 0) return resolver;
            return framework != null && (framework.StartsWith(".NETCoreApp,", StringComparison.Ordinal) ||
                framework.StartsWith(".NETStandard,", StringComparison.Ordinal)) ? null : resolver;
        }

        public static bool IsStandardWorkflowAssembly(string assemblyName)
        {
            // The stock wrapper takes the substring after the comma in an assembly-qualified
            // type name, which includes a leading space for reflection-discovered activities.
            assemblyName = assemblyName?.Trim();
            return BuiltActivityMetadata.IsDesignTimeAssembly(assemblyName) || assemblyName != null && (assemblyName.StartsWith("System.Activities,", StringComparison.Ordinal) ||
            assemblyName.StartsWith("System.Activities.Core.Presentation,", StringComparison.Ordinal) ||
            assemblyName.StartsWith("System.Activities.Presentation,", StringComparison.Ordinal));
        }

        public static IList<AssemblyName> GetModernDesignerReferences(IVsDesignTimeAssemblyResolution resolver)
        {
            if (resolver == null || SelectDesignerResolver(resolver) != null) return null;
            var names = new List<AssemblyName>
            {
                typeof(object).Assembly.GetName(), typeof(System.Activities.Activity).Assembly.GetName(),
                typeof(System.Linq.Enumerable).Assembly.GetName(),
                typeof(System.Xml.XmlDocument).Assembly.GetName()
            };
            foreach (string name in new[] { "System", "System.Data", "System.ServiceModel", "System.Runtime.Serialization", "Microsoft.VisualBasic" })
            {
                var identity = new AssemblyName(name + ", Version=4.0.0.0, Culture=neutral, PublicKeyToken=" +
                    (name == "Microsoft.VisualBasic" ? "b03f5f7f11d50a3a" : "b77a5c561934e089"));
                if (name == "Microsoft.VisualBasic") identity.Version = new Version(10, 0, 0, 0);
                names.Add(Assembly.Load(identity).GetName());
            }
            names.Add(Assembly.Load("System.ServiceModel.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35").GetName());
            return names;
        }

    }
}
