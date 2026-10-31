using System;
using System.AddIn.Contract;
using CoreWF.LegacyAdapters;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain domain = null;
        try
        {
            string nativeDirectory = System.IO.Path.Combine(Environment.GetEnvironmentVariable("COREWF_PROBE_IDE") ?? "C:\\Program Files\\Microsoft Visual Studio\\18\\Enterprise\\Common7\\IDE", "AppLocalWPF");
            foreach (string name in new[] { "vcruntime140_cor3.dll", "wpfgfx_cor3.dll", "PresentationNative_cor3.dll" })
                if (System.IO.File.Exists(System.IO.Path.Combine(nativeDirectory, name)) && LoadLibraryEx(System.IO.Path.Combine(nativeDirectory, name), IntPtr.Zero, 8) == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to load Visual Studio WPF native dependency: " + name);
            if (args.Length == 2 && args[0] == "--partition-probe")
            {
                string ide = Environment.GetEnvironmentVariable("COREWF_PROBE_IDE") ?? "C:\\Program Files\\Microsoft Visual Studio\\18\\Enterprise\\Common7\\IDE";
                string msbuild = System.IO.Path.GetFullPath(System.IO.Path.Combine(ide, "..", "..", "MSBuild", "Current", "Bin", "amd64"));
                AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
                {
                    string name = new AssemblyName(request.Name).Name;
                    string path = System.IO.Path.Combine(msbuild, name + ".dll");
                    if (name.StartsWith("Microsoft.Build", StringComparison.Ordinal) && System.IO.File.Exists(path)) return Assembly.LoadFrom(path);
                    if (name.StartsWith("Microsoft.VisualStudio.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("EnvDTE", StringComparison.OrdinalIgnoreCase) || name == "stdole")
                        foreach (string directory in new[] { "PublicAssemblies", "PrivateAssemblies", "", "Contracts" })
                        {
                            path = System.IO.Path.Combine(ide, directory, name + ".dll");
                            if (System.IO.File.Exists(path)) return Assembly.LoadFrom(path);
                        }
                    return null;
                };
                var patched = Assembly.LoadFile(System.IO.Path.GetFullPath(args[1]));
                var helper = patched.GetType("Microsoft.VisualStudio.Activities.Helper", true);
                var frameworkCallback = helper.GetMethod("GetTargetFramework", BindingFlags.NonPublic | BindingFlags.Static);
                var paneType = patched.GetType("Microsoft.VisualStudio.Activities.EditorPane", true);
                var pane = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(paneType);
                var paneCallback = paneType.GetMethod("GetTargetFramework", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (string moniker in new[] { ".NETCoreApp,Version=v8.0", ".NETCoreApp,Version=v10.0", ".NETStandard,Version=v2.0", ".NETFramework,Version=v4.7.2" })
                {
                    string expected = moniker.StartsWith(".NETFramework,") ? moniker : ".NETFramework,Version=v4.8";
                    var resolver = new ProbeResolver(moniker);
                    object[] frameworkArguments = { resolver, null, null };
                    if ((int)frameworkCallback.Invoke(null, frameworkArguments) != 0 || (string)frameworkArguments[2] != expected)
                        throw new InvalidOperationException("The patched framework helper did not normalize " + moniker);
                    paneType.GetField("dtar", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pane, resolver);
                    object[] paneArguments = { null };
                    if ((int)paneCallback.Invoke(pane, paneArguments) != 0 || (string)paneArguments[0] != expected)
                        throw new InvalidOperationException("The patched editor callback did not normalize " + moniker);
                    var configuration = new System.Activities.Presentation.WorkflowDesigner().Context.Services
                        .GetService<System.Activities.Presentation.DesignerConfigurationService>();
                    configuration.TargetFrameworkName = new System.Runtime.Versioning.FrameworkName((string)paneArguments[0]);
                }
                paneType.GetField("dtar", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pane, new ProbeResolver(null, unchecked((int)0x80004005)));
                object[] failedArguments = { "must be cleared" };
                if ((int)paneCallback.Invoke(pane, failedArguments) != unchecked((int)0x80004005) || failedArguments[0] != null)
                    throw new InvalidOperationException("The framework callback changed the failed lookup result.");
                Console.WriteLine("PASS: actual patched framework callbacks accept .NET 8/10 and .NET Standard in the native designer; Framework targets and HRESULTs are preserved.");
                var method = patched.GetType("Microsoft.VisualStudio.Activities.Helper", true)
                    .GetMethod("TryGetPartitionedBuildAssemblyPath", BindingFlags.NonPublic | BindingFlags.Static);
                foreach (string moniker in new[] { ".NETCoreApp,Version=v10.0", ".NETStandard,Version=v2.0" })
                {
                    var hierarchy = new ProbeHierarchy(moniker);
                    object[] arguments = { hierarchy.GetTransparentProxy(), "must be cleared" };
                    if ((bool)method.Invoke(null, arguments) || arguments[1] != null || hierarchy.Calls != 1)
                        throw new InvalidOperationException("The patched partition-build method did not bypass project evaluation.");
                }
                Console.WriteLine("PASS: patched Microsoft designer DLL bypasses project evaluation for modern projects and clears the output path.");
                var createName = patched.GetType("Microsoft.VisualStudio.Activities.Helper", true)
                    .GetMethod("CreateAssemblyNameFromReference", BindingFlags.NonPublic | BindingFlags.Static);
                foreach (string culture in new[] { null, "", "en-US" })
                {
                    var reference = new ProbeReference(createName.GetParameters()[0].ParameterType, culture);
                    var name = (AssemblyName)createName.Invoke(null, new[] { reference.GetTransparentProxy() });
                    if (name.CultureInfo.Name != (culture ?? "") || name.Name != "ProbeActivity" || name.Version.ToString() != "1.2.3.4")
                        throw new InvalidOperationException("Reference assembly culture or identity was changed incorrectly.");
                }
                Console.WriteLine("PASS: patched reference conversion accepts null/empty neutral cultures and preserves named cultures and assembly versions.");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--metadata")
            {
                var identity = AssemblyName.GetAssemblyName(args[1]); identity.CodeBase = args[1];
                var mapped = BuiltActivityMetadata.MapBuiltAssembly(identity);
                var again = BuiltActivityMetadata.MapBuiltAssembly(identity);
                if (mapped.CodeBase != again.CodeBase) throw new InvalidOperationException("Unchanged build did not reuse its metadata view.");
                if (mapped.CodeBase != identity.CodeBase && !ModernFrameworkCompatibility.IsStandardWorkflowAssembly(" " + mapped.FullName))
                    throw new InvalidOperationException("The native Toolbox assembly identity was not recognized.");
                var projected = Assembly.LoadFrom(mapped.CodeBase);
                foreach (var type in projected.GetExportedTypes().Where(type => typeof(System.Activities.Activity).IsAssignableFrom(type)))
                {
                    if (!type.IsAbstract && !type.ContainsGenericParameters) Activator.CreateInstance(type);
                    Console.WriteLine("ACTIVITY " + type.FullName);
                }
                Console.WriteLine("METADATA " + mapped.CodeBase);
                Console.WriteLine("PASS: repeat discovery and native Toolbox identity mapping.");
                return 0;
            }
            Console.WriteLine("Process bitness: " + IntPtr.Size * 8);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Console.WriteLine("CLR: " + Environment.Version);
            Console.WriteLine("WPF: " + typeof(FrameworkElement).Assembly.FullName);
            Console.WriteLine("Add-in adapters: " + typeof(FrameworkElementAdapters).Assembly.FullName);
            foreach (string moniker in new[] { ".NETCoreApp,Version=v10.0", ".NETStandard,Version=v2.0", ".NETFramework,Version=v4.7.2" })
            {
                string normalized = moniker;
                ModernFrameworkCompatibility.NormalizeDesignerFramework(ref normalized);
                if (normalized != (moniker.StartsWith(".NETFramework,") ? moniker : ".NETFramework,Version=v4.8"))
                    throw new InvalidOperationException("Designer framework normalization failed.");
            }
            var modernReferences = ModernFrameworkCompatibility.GetModernDesignerReferences(new ProbeResolver(".NETCoreApp,Version=v10.0"));
            if (!modernReferences.Any(name => name.Name == "System.Activities" && name.Version.Major == 4) ||
                modernReferences.Any(name => name.Name == "System.Private.CoreLib" || name.Version.Major > 10))
                throw new InvalidOperationException("Modern design-time references did not resolve to CLR4.");
            if (ModernFrameworkCompatibility.GetModernDesignerReferences(new ProbeResolver(".NETFramework,Version=v4.7.2")) != null)
                throw new InvalidOperationException("Legacy project references were intercepted.");
            Console.WriteLine("PASS: modern designer framework/reference mapping and legacy project preservation.");
            foreach (string moniker in new[] { ".NETCoreApp,Version=v10.0", ".NETStandard,Version=v2.0", ".NETFramework,Version=v4.7.2", "", null })
            {
                var hierarchy = new ProbeHierarchy(moniker);
                bool skip = ModernFrameworkCompatibility.ShouldSkipPartitionedBuildProbe(
                    (Microsoft.VisualStudio.Shell.Interop.IVsHierarchy)hierarchy.GetTransparentProxy());
                if (skip != (moniker != null && (moniker.StartsWith(".NETCoreApp,") || moniker.StartsWith(".NETStandard,"))) || hierarchy.Calls != 1)
                    throw new InvalidOperationException("Partitioned-build probing did not preserve project framework behavior.");
            }
            foreach (bool throws in new[] { false, true })
            {
                var hierarchy = new ProbeHierarchy(".NETCoreApp,Version=v10.0", true, throws);
                if (ModernFrameworkCompatibility.ShouldSkipPartitionedBuildProbe(
                    (Microsoft.VisualStudio.Shell.Interop.IVsHierarchy)hierarchy.GetTransparentProxy()) || hierarchy.Calls != 1)
                    throw new InvalidOperationException("Unavailable project properties changed the legacy fallback.");
            }
            if (ModernFrameworkCompatibility.ShouldSkipPartitionedBuildProbe(null))
                throw new InvalidOperationException("A missing hierarchy must preserve the legacy fallback.");
            Console.WriteLine("PASS: SDK partition-build checks read only the cached framework property; Framework and failed lookups retain the original path.");
            var registeredPane = new RegisteredPane("already-registered.xaml");
            for (int reload = 0; reload < 3; reload++)
                if (DeferredDesignerLoad.TryDefer(registeredPane, registeredPane.Moniker, 0, 0) ||
                    DeferredDesignerLoad.IsPending(registeredPane) || registeredPane.Moniker != "already-registered.xaml")
                    throw new InvalidOperationException("A registered document was delayed or its moniker changed during reload.");
            Console.WriteLine("PASS: repeated reloads preserve the registered document moniker and stay synchronous.");
            if (args.Length > 0)
            {
                if (args.Length >= 4 && args[3] != "-")
                {
                    var identity = AssemblyName.GetAssemblyName(args[3]); identity.CodeBase = args[3];
                    var metadata = BuiltActivityMetadata.MapBuiltAssembly(identity);
                    Assembly.LoadFrom(metadata.CodeBase);
                }
                new System.Activities.Core.Presentation.DesignerMetadata().Register();
                var designer = new System.Activities.Presentation.WorkflowDesigner();
                CompatibleWorkflowDocument.Load(designer, args[0]);
                if (designer.IsInErrorState())
                {
                    var errorType = typeof(System.Activities.Presentation.WorkflowDesigner).Assembly.GetType("System.Activities.Presentation.View.ErrorItem", true);
                    var error = designer.Context.Items.GetValue(errorType);
                    throw new InvalidOperationException((string)errorType.GetProperty("Details").GetValue(error));
                }
                var model = designer.Context.Services.GetService<System.Activities.Presentation.Services.ModelService>();
                if (model == null) throw new InvalidOperationException("No workflow model was loaded.");
                if (args.Length >= 3)
                {
                    int states = model.Find(model.Root, typeof(System.Activities.Statements.State)).Count();
                    int transitions = model.Find(model.Root, typeof(System.Activities.Statements.Transition)).Count();
                    if (states != int.Parse(args[1]) || transitions != int.Parse(args[2]))
                        throw new InvalidOperationException("Workflow model did not preserve its states/transitions: " + states + "/" + transitions);
                    Console.WriteLine("PASS: loaded actual workflow model with " + states + " states and " + transitions + " transitions.");
                }
                Console.WriteLine("PASS: stock .NET Framework WorkflowDesigner loaded " + args[0]);
                if (args.Length >= 5)
                {
                    designer.Save(args[4]);
                    Console.WriteLine("PASS: stock designer saved compatible workflow to " + args[4]);
                }
            }
            var current = AppDomain.CurrentDomain.SetupInformation;
            domain = AppDomain.CreateDomain("LegacyWpfProbe.AddIn", null, new AppDomainSetup
            { ApplicationBase = current.ApplicationBase, ConfigurationFile = current.ConfigurationFile });
            var remote = (RemoteViewFactory)domain.CreateInstanceAndUnwrap(
                Assembly.GetExecutingAssembly().FullName, typeof(RemoteViewFactory).FullName);
            INativeHandleContract contract = remote.Create();
            FrameworkElement host = FrameworkElementAdapters.ContractToViewAdapter(contract);
            if (host == null) throw new InvalidOperationException("No framework element was returned.");
            Exception failure = null;
            using (var timer = new DispatcherTimerScope())
            {
                var window = new Window
                {
                    Content = host, ShowInTaskbar = false, Opacity = 0, Width = 400, Height = 300,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
                };
                window.Loaded += (_, __) => application.Dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        IntPtr child = contract.GetHandle();
                        IntPtr parent = new WindowInteropHelper(window).Handle;
                        if (child == IntPtr.Zero || !IsWindow(child) || !IsChild(parent, child))
                            throw new InvalidOperationException("The cross-domain designer HWND was not embedded.");
                        Console.WriteLine("PASS: FrameworkElementAdapters round-tripped a WPF view across AppDomains and embedded its HWND.");
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { window.Close(); application.Shutdown(); }
                }));
                timer.Timer.Tick += (_, __) =>
                {
                    failure = new TimeoutException("The legacy WPF adapter test did not finish.");
                    window.Close(); application.Shutdown();
                };
                timer.Timer.Start();
                application.Run(window);
            }
            remote.Close();
            if (failure != null) throw failure;
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { if (domain != null) AppDomain.Unload(domain); }
    }

    private sealed class DispatcherTimerScope : IDisposable
    {
        internal readonly DispatcherTimer Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        public void Dispose() => Timer.Stop();
    }

    private sealed class RegisteredPane
    {
        private readonly string fileName;
        public string Moniker => fileName;
        public RegisteredPane(string path) { fileName = path; }
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
}

internal sealed class ProbeResolver : Microsoft.VisualStudio.Shell.Interop.IVsDesignTimeAssemblyResolution
{
    private readonly string moniker;
    private readonly int result;
    public ProbeResolver(string moniker, int result = 0) { this.moniker = moniker; this.result = result; }
    public int GetTargetFramework(out string framework) { framework = moniker; return result; }
    public int ResolveAssemblyPathInTargetFx(string[] specs, uint count, Microsoft.VisualStudio.Shell.Interop.VsResolvedAssemblyPath[] paths, out uint resolved)
    { resolved = 0; throw new InvalidOperationException("A modern CLR reference resolver must not be used by the CLR4 designer."); }
}

internal sealed class ProbeHierarchy : System.Runtime.Remoting.Proxies.RealProxy
{
    private readonly string moniker;
    private readonly bool fails;
    private readonly bool throws;
    public int Calls { get; private set; }
    public ProbeHierarchy(string moniker, bool fails = false, bool throws = false)
        : base(typeof(Microsoft.VisualStudio.Shell.Interop.IVsHierarchy))
    { this.moniker = moniker; this.fails = fails; this.throws = throws; }
    public override System.Runtime.Remoting.Messaging.IMessage Invoke(System.Runtime.Remoting.Messaging.IMessage message)
    {
        var call = (System.Runtime.Remoting.Messaging.IMethodCallMessage)message;
        Calls++;
        if (call.MethodName != "GetProperty" || (uint)call.Args[0] != 0xFFFFFFFEu ||
            (int)call.Args[1] != (int)Microsoft.VisualStudio.Shell.Interop.__VSHPROPID4.VSHPROPID_TargetFrameworkMoniker)
            throw new InvalidOperationException("The SDK guard requested something other than the cached target framework property.");
        if (throws)
            return new System.Runtime.Remoting.Messaging.ReturnMessage(new COMException("Property unavailable."), call);
        object[] arguments = (object[])call.Args.Clone();
        arguments[2] = moniker;
        return new System.Runtime.Remoting.Messaging.ReturnMessage(fails ? unchecked((int)0x80004001) : 0,
            arguments, arguments.Length, call.LogicalCallContext, call);
    }
}

internal sealed class ProbeReference : System.Runtime.Remoting.Proxies.RealProxy
{
    private readonly string culture;
    public ProbeReference(Type referenceType, string culture) : base(referenceType) { this.culture = culture; }
    public override System.Runtime.Remoting.Messaging.IMessage Invoke(System.Runtime.Remoting.Messaging.IMessage message)
    {
        var call = (System.Runtime.Remoting.Messaging.IMethodCallMessage)message;
        object value;
        switch (call.MethodName)
        {
            case "get_Identity": value = "ProbeActivity"; break;
            case "get_Culture": value = culture; break;
            case "get_MajorVersion": value = 1; break;
            case "get_MinorVersion": value = 2; break;
            case "get_BuildNumber": value = 3; break;
            case "get_RevisionNumber": value = 4; break;
            case "get_PublicKeyToken": value = null; break;
            case "get_Path": value = @"C:\ProbeActivity.dll"; break;
            default: throw new InvalidOperationException("Unexpected reference property: " + call.MethodName);
        }
        return new System.Runtime.Remoting.Messaging.ReturnMessage(value, null, 0, call.LogicalCallContext, call);
    }
}

public sealed class RemoteViewFactory : MarshalByRefObject
{
    private Dispatcher dispatcher;
    public INativeHandleContract Create()
    {
        var ready = new TaskCompletionSource<INativeHandleContract>();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.SetResult(FrameworkElementAdapters.ViewToContractAdapter(
                    new Border { Child = new TextBlock { Text = "Legacy Workflow Designer adapter probe" } }));
                application.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The add-in view did not initialize.");
        return ready.Task.GetAwaiter().GetResult();
    }

    public void Close() => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    public override object InitializeLifetimeService() => null;
}
