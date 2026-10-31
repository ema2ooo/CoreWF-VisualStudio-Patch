using System;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CoreWF.LegacyAdapters
{
    public static class DeferredDesignerLoad
    {
        private static readonly ConditionalWeakTable<object, PendingLoad> Pending = new ConditionalWeakTable<object, PendingLoad>();
        private static readonly ConditionalWeakTable<object, object> Loading = new ConditionalWeakTable<object, object>();

        public static bool TryDefer(object pane, string path, uint format, int readOnly)
        {
            if (path == null || Loading.TryGetValue(pane, out _)) return false;
            if (Pending.TryGetValue(pane, out _)) return true;
            // A loaded pane is already registered with EditorPanesManager. Its normal
            // build reload must stay synchronous and retain the existing moniker.
            // Clearing that moniker for another delayed load registers it a second time.
            var moniker = pane.GetType().GetField("fileName", BindingFlags.Instance | BindingFlags.NonPublic);
            if (!string.IsNullOrEmpty((string)moniker.GetValue(pane))) return false;
            var pending = new PendingLoad(pane, path, format, readOnly);
            Pending.Add(pane, pending);
            pending.Start();
            return true;
        }

        public static bool IsPending(object pane) => Pending.TryGetValue(pane, out _);

        private sealed class PendingLoad
        {
            private readonly WeakReference owner;
            private readonly string path;
            private readonly uint format;
            private readonly int readOnly;
            private readonly DispatcherTimer timer;
            private readonly DateTime started = DateTime.UtcNow;
            private bool firstTick = true;
            private readonly TextBlock status = new TextBlock { Text = "Waiting for the project to finish loading…", Foreground = SystemColors.ControlTextBrush, Background = SystemColors.ControlBrush, Margin = new Thickness(16) };

            public PendingLoad(object pane, string path, uint format, int readOnly)
            {
                owner = new WeakReference(pane); this.path = path; this.format = format; this.readOnly = readOnly;
                var content = pane.GetType().GetProperty("Content")?.GetValue(pane) as DispatcherObject;
                timer = new DispatcherTimer(DispatcherPriority.Background, content?.Dispatcher ?? Application.Current.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                timer.Tick += OnTick;
            }

            public void Start()
            {
                object pane = owner.Target;
                // The shell asks for the document moniker before the delayed load runs.
                pane.GetType().GetField("fileName", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(pane, path);
                var content = pane.GetType().GetProperty("Content")?.GetValue(pane) as Border;
                if (content != null) content.Child = status;
                Log("Deferred workflow load scheduled.");
                timer.Start();
            }

            private void OnTick(object sender, EventArgs args)
            {
                try { LoadWhenReady(); }
                catch (Exception exception)
                {
                    timer.Stop();
                    status.Text = "The workflow could not be initialized. " + exception.GetBaseException().Message;
                    Log(exception.ToString());
                }
            }

            private void LoadWhenReady()
            {
                object pane = owner.Target;
                if (pane == null) { timer.Stop(); return; }
                if (firstTick) Log("Deferred workflow callback entered.");
                var frameService = pane.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(method => method.Name == "GetService" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                    .MakeGenericMethod(typeof(IVsWindowFrame)).Invoke(pane, null);
                if (firstTick) { Log("Window frame available: " + (frameService != null)); firstTick = false; }
                if (frameService == null || (!KnownUIContexts.SolutionExistsAndFullyLoadedContext.IsActive && !KnownUIContexts.NoSolutionContext.IsActive))
                {
                    if (DateTime.UtcNow - started < TimeSpan.FromMinutes(2)) return;
                    timer.Stop();
                    status.Text = "The project has not finished loading. Reopen the workflow after project loading completes.";
                    return;
                }
                timer.Stop(); Pending.Remove(pane);
                Loading.Add(pane, new object());
                try
                {
                    pane.GetType().GetField("fileName", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(pane, null);
                    ((IPersistFileFormat)pane).Load(path, format, readOnly);
                    Log("Standard designer loaded.");
                    StandardToolbox.Ensure(pane);
                    Log("Standard Toolbox initialized.");
                }
                catch (Exception exception) { status.Text = "The workflow could not be loaded. " + exception.Message; Log(exception.ToString()); }
                finally { Loading.Remove(pane); }
            }

            private static void Log(string message)
            {
                try
                {
                    string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreWF.DesignerPatch");
                    System.IO.Directory.CreateDirectory(directory);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "standard-designer.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
                }
                catch { /* Diagnostics must never interrupt editing or saving. */ }
            }
        }
    }
}
