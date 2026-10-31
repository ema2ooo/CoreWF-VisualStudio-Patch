using System;
using System.AddIn.Contract;
using System.AddIn.Pipeline;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CoreWF.LegacyAdapters
{
    // VS 18's app-local WPF omits the MAF UI adapters. This replacement uses
    // public WPF APIs; it does not change the CLR or the legacy WF designer.
    public static class FrameworkElementAdapters
    {
        public static INativeHandleContract ViewToContractAdapter(FrameworkElement root)
        {
            if (root == null) return null;
            root.VerifyAccess();
            var source = PresentationSource.FromVisual(root) as HwndSource;
            if (source == null)
            {
                source = new HwndSource(new HwndSourceParameters("Workflow designer add-in")
                {
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE until attached to a host.
                    WindowStyle = 0x40000000 | 0x02000000 | 0x04000000
                });
                source.RootVisual = root;
                source.CompositionTarget.BackgroundColor = Colors.White;
                source.SizeToContent = SizeToContent.Manual;
            }
            else if (source.RootVisual != root)
                throw new InvalidOperationException("The adapted element must be the root visual.");
            return new ViewContract(source);
        }

        public static FrameworkElement ContractToViewAdapter(INativeHandleContract contract) =>
            contract == null ? null : new ViewHost(contract);

        public sealed class ViewContract : ContractBase, INativeHandleContract
        {
            private readonly HwndSource source;
            private readonly IntPtr handle;
            private ContractHandle siteLifetime;

            internal ViewContract(HwndSource source) { this.source = source; handle = source.Handle; }
            public IntPtr GetHandle() => handle;

            public bool TabInto(FocusNavigationDirection direction) => source.CheckAccess() &&
                ((IKeyboardInputSink)source).TabInto(new TraversalRequest(direction));

            public bool Translate(int message, IntPtr wParam, IntPtr lParam, ModifierKeys modifiers)
            {
                if (!source.CheckAccess()) return false;
                var msg = new MSG { hwnd = handle, message = message, wParam = wParam, lParam = lParam };
                return ((IKeyboardInputSink)source).TranslateAccelerator(ref msg, modifiers);
            }

            public void RegisterSite(HostSite site)
            {
                if (!source.CheckAccess()) return;
                siteLifetime?.Dispose();
                siteLifetime = new ContractHandle(site);
                ((IKeyboardInputSink)source).KeyboardInputSite = new InputSite(source, site);
            }

            protected override void OnFinalRevoke()
            {
                Action release = () =>
                {
                    ((IKeyboardInputSink)source).KeyboardInputSite = null;
                    source.Dispose();
                    siteLifetime?.Dispose();
                    siteLifetime = null;
                };
                if (source.CheckAccess()) release();
                else if (!source.Dispatcher.HasShutdownStarted) source.Dispatcher.BeginInvoke(release);
            }
        }

        public sealed class HostSite : ContractBase
        {
            private readonly ViewHost host;
            internal HostSite(ViewHost host) { this.host = host; }
            public bool MoveFocus(FocusNavigationDirection direction) => host.CheckAccess() &&
                host.MoveFocus(new TraversalRequest(direction));
        }

        private sealed class InputSite : IKeyboardInputSite
        {
            private readonly HostSite site;
            public InputSite(IKeyboardInputSink sink, HostSite site) { Sink = sink; this.site = site; }
            public IKeyboardInputSink Sink { get; }
            public void Unregister() { Sink.KeyboardInputSite = null; }
            public bool OnNoMoreTabStops(TraversalRequest request) => site.MoveFocus(request.FocusNavigationDirection);
        }

        internal sealed class ViewHost : HwndHost
        {
            private ContractHandle lifetime;
            private readonly INativeHandleContract contract;
            private readonly ViewContract view;

            static ViewHost()
            {
                Control.IsTabStopProperty.OverrideMetadata(typeof(ViewHost), new FrameworkPropertyMetadata(true));
                FocusableProperty.OverrideMetadata(typeof(ViewHost), new FrameworkPropertyMetadata(true));
            }

            public ViewHost(INativeHandleContract contract)
            {
                this.contract = contract;
                lifetime = new ContractHandle(contract);
                view = contract as ViewContract;
                view?.RegisterSite(new HostSite(this));
            }

            protected override HandleRef BuildWindowCore(HandleRef parent)
            {
                IntPtr child = contract.GetHandle();
                uint process;
                GetWindowThreadProcessId(child, out process);
                if (child == IntPtr.Zero || !IsWindow(child) || process != GetCurrentProcessId())
                    throw new InvalidOperationException("The legacy designer returned an invalid window.");
                SetLastError(0);
                IntPtr previous = SetParent(child, parent.Handle);
                int error = Marshal.GetLastWin32Error();
                if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
                return new HandleRef(this, child);
            }

            protected override void DestroyWindowCore(HandleRef window) { }
            protected override bool TabIntoCore(TraversalRequest request) => view?.TabInto(request.FocusNavigationDirection) ?? false;
            protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers) =>
                view?.Translate(msg.message, msg.wParam, msg.lParam, modifiers) ?? false;

            protected override void Dispose(bool disposing)
            {
                try { base.Dispose(disposing); }
                finally { if (disposing) { lifetime?.Dispose(); lifetime = null; } }
            }
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    }
}
