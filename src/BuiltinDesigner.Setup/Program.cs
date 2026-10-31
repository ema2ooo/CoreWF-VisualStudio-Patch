using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BuiltinDesigner.Setup
{
    internal sealed class Plan
    {
        public string VisualStudioVersion { get; set; }
        public Entry[] Files { get; set; }
    }

    internal sealed class Entry
    {
        public string Target { get; set; }
        public string Payload { get; set; }
        public string Sha256 { get; set; }
        public string Original { get; set; }
        public string OriginalSha256 { get; set; }
        public string Backup { get; set; }
    }

    internal sealed class Snapshot
    {
        public string Path;
        public byte[] Bytes;
    }

    internal static class PatchEngine
    {
        internal static string Hash(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var input = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "");
        }

        internal static string Contained(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid package path.");
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package path leaves its directory.");
            return path;
        }

        internal static void Verify(string path, string expected)
        {
            if (!File.Exists(path) || Hash(path) != expected) throw new InvalidDataException("Unexpected file version: " + path);
        }

        internal static void Preflight(Plan plan, string payload, string ide, bool requireVisualStudio, bool uninstall = false)
        {
            LocalPatch.ValidatePlan(plan, !uninstall);
            if (requireVisualStudio) LocalPatch.ValidateInstallation(ide, true);
            Plan prior = LocalPatch.LoadReceipt(ide, false);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry file in plan.Files)
            {
                string target = Contained(ide, file.Target);
                if (!targets.Add(target)) throw new InvalidDataException("Duplicate package target.");
                if (!uninstall)
                {
                    Verify(Contained(payload, file.Payload), file.Sha256);
                    if (!string.IsNullOrEmpty(file.Original)) Verify(Contained(payload, file.Original), file.OriginalSha256);
                }
                if (!string.IsNullOrEmpty(file.Original))
                {
                    string backup = LocalPatch.BackupPath(ide, file);
                    if (uninstall || File.Exists(backup)) Verify(backup, file.OriginalSha256);
                }
                if (!File.Exists(target))
                {
                    if (!string.IsNullOrEmpty(file.Original)) throw new FileNotFoundException("The Workflow Designer component is missing.", target);
                    continue;
                }
                string current = Hash(target);
                Entry previous = prior == null ? null : prior.Files.SingleOrDefault(item => item.Target == file.Target);
                if (string.IsNullOrEmpty(file.Original) && previous == null)
                    throw new InvalidOperationException("A pre-existing patch/dependency file was found. Uninstall the older patch first: " + file.Target);
                if (current != file.Sha256 && current != file.OriginalSha256 && (previous == null || current != previous.Sha256))
                    throw new InvalidOperationException("An unsupported or modified file was found. No files were changed: " + target);
            }
        }

        // All files are checked before mutation. Every changed file has an in-memory rollback
        // snapshot; original Microsoft binaries also have persistent, hash-verified backups.
        internal static void Apply(Plan plan, string payload, string ide, bool uninstall, Action<string> log, int failAfter = -1)
        {
            Preflight(plan, payload, ide, false, uninstall);
            var snapshots = new List<Snapshot>();
            string receipt = Contained(ide, LocalPatch.Receipt);
            snapshots.Add(new Snapshot { Path = receipt, Bytes = File.Exists(receipt) ? File.ReadAllBytes(receipt) : null });
            try
            {
                foreach (Entry file in plan.Files)
                {
                    string target = Contained(ide, file.Target);
                    snapshots.Add(new Snapshot { Path = target, Bytes = File.Exists(target) ? File.ReadAllBytes(target) : null });
                    if (!string.IsNullOrEmpty(file.Original))
                    {
                        string backup = LocalPatch.BackupPath(ide, file);
                        if (!uninstall && !File.Exists(backup))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(backup));
                            File.Copy(Contained(payload, file.Original), backup, false);
                        }
                        Verify(backup, file.OriginalSha256);
                    }
                    if (uninstall && string.IsNullOrEmpty(file.Original))
                    {
                        if (File.Exists(target)) File.Delete(target);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string source = uninstall ? LocalPatch.BackupPath(ide, file) : Contained(payload, file.Payload);
                        File.Copy(source, target, true);
                        Verify(target, uninstall ? file.OriginalSha256 : file.Sha256);
                    }
                    log((uninstall ? "Restored " : "Installed ") + file.Target);
                    if (snapshots.Count == failAfter) throw new IOException("Simulated transaction failure.");
                }
                if (uninstall) File.Delete(receipt);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(receipt));
                    File.WriteAllText(receipt, new JavaScriptSerializer().Serialize(plan));
                }
            }
            catch
            {
                foreach (Snapshot snapshot in Enumerable.Reverse(snapshots))
                {
                    if (snapshot.Bytes == null) { if (File.Exists(snapshot.Path)) File.Delete(snapshot.Path); }
                    else File.WriteAllBytes(snapshot.Path, snapshot.Bytes);
                }
                log("The operation failed. Every changed designer file was rolled back.");
                throw;
            }
        }
    }

    internal static class Program
    {
        internal static PackageManifest Package;
        internal static string Payload;
        internal static string WorkRoot;

        [STAThread]
        private static int Main(string[] args)
        {
            string temporary = Path.Combine(Path.GetTempPath(), "CoreWF-Patch-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temporary);
                WorkRoot = temporary;
                Payload = Path.Combine(temporary, "payload");
                Directory.CreateDirectory(Payload);
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.zip"))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/")) continue;
                        string destination = PatchEngine.Contained(Payload, entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        using (Stream input = entry.Open())
                        using (Stream output = File.Create(destination)) input.CopyTo(output);
                    }
                }
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request)
                {
                    return new AssemblyName(request.Name).Name == "Mono.Cecil" ?
                        Assembly.Load(File.ReadAllBytes(Path.Combine(Payload, "tools", "BinaryPatch", "Mono.Cecil.dll"))) : null;
                };
                Package = LocalPatch.Read<PackageManifest>(Path.Combine(Payload, "package.json"));
                LocalPatch.ValidatePackage();
                if (args.Length == 2 && args[0] == "--self-test") return SelfTest(args[1]);
                if (args.Length == 2 && (args[0] == "--check" || args[0] == "--check-uninstall"))
                {
                    LocalPatch.ValidateInstallation(args[1], true);
                    if (args[0] == "--check-uninstall")
                        PatchEngine.Preflight(LocalPatch.LoadReceipt(args[1], true), Payload, args[1], false, true);
                    else LocalPatch.Prepare(args[1], Path.Combine(WorkRoot, "checked"), true, Console.WriteLine);
                    Console.WriteLine("Local designer components and original-file recovery verified. No installation files were changed.");
                    return 0;
                }
                if ((args.Length == 4 && args[0] == "--install") || (args.Length == 3 && args[0] == "--uninstall"))
                {
                    using (var log = new StreamWriter(args[2], false))
                    {
                        try
                        {
                            bool uninstall = args[0] == "--uninstall";
                            if (!uninstall) LocalPatch.RequireConsent(args[3]);
                            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                                throw new InvalidOperationException("Windows administrator approval is required.");
                            if (Process.GetProcessesByName("devenv").Length > 0)
                                throw new InvalidOperationException("Save your work and close all Visual Studio windows, then try again.");
                            string ide = Path.GetFullPath(args[1]);
                            string generated = Path.Combine(WorkRoot, "generated");
                            Plan plan;
                            if (uninstall)
                            {
                                LocalPatch.ValidateInstallation(ide, true);
                                plan = LocalPatch.LoadReceipt(ide, true);
                            }
                            else plan = LocalPatch.Prepare(ide, generated, true, log.WriteLine);
                            PatchEngine.Apply(plan, generated, ide, uninstall, log.WriteLine);
                            log.WriteLine("Completed. Reopen Visual Studio.");
                            return 0;
                        }
                        catch (Exception error) { log.WriteLine(error.Message); return 1; }
                    }
                }
                if (args.Length != 0) throw new ArgumentException("Unsupported setup arguments.");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupWindow());
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length == 0) MessageBox.Show(error.Message, "Workflow Designer Patch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\CoreWF-Patch-";
                if (temporary.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static int SelfTest(string fixture) { return InstallerTests.Run(fixture); }
    }

    internal sealed class SetupWindow : Form
    {
        private readonly ComboBox instances = new ComboBox { Name = "VisualStudioInstallation", DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox path = new TextBox { Name = "IdePath" };
        private readonly TextBox details = new TextBox();
        private readonly Button install = new Button { Text = "Install / Repair" };
        private readonly Button remove = new Button { Text = "Uninstall" };
        private readonly Button browse = new Button { Text = "Browse…" };
        private readonly CheckBox consent = new CheckBox();

        internal SetupWindow() : this(LocalPatch.FindInstalledIdes()) { }

        internal SetupWindow(IEnumerable<VisualStudioInstance> installations)
        {
            Text = "CoreWF Workflow Designer Patch";
            ClientSize = new Size(760, 586);
            MinimumSize = new Size(776, 625);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10);
            var heading = new Label { Text = "Use the standard Workflow Designer with modern .NET", AutoSize = true, Location = new Point(22, 22), Font = new Font("Segoe UI", 13, FontStyle.Bold) };
            var description = new Label { Text = "Built-in designer • Standard Toolbox • Activity discovery after builds\r\nUses your installed designer · Close Visual Studio before installing.", AutoSize = true, Location = new Point(22, 62) };
            var instanceLabel = new Label { Text = "Visual Studio installation (choose one)", AutoSize = true, Location = new Point(22, 125) };
            instances.SetBounds(22, 152, 716, 28);
            instances.DropDownWidth = 716;
            var label = new Label { Text = "Selected Visual Studio IDE folder", AutoSize = true, Location = new Point(22, 192) };
            path.SetBounds(22, 219, 615, 28);
            browse.SetBounds(646, 218, 92, 30);
            consent.Text = "I authorize patching my installed Visual Studio designer files and keeping local backups.";
            consent.SetBounds(22, 261, 716, 48);
            consent.Checked = false;
            install.Enabled = false;
            consent.CheckedChanged += delegate { UpdateActions(); };
            instances.SelectedIndexChanged += delegate
            {
                var selected = instances.SelectedItem as VisualStudioInstance;
                if (selected != null) path.Text = selected.IdePath;
            };
            path.TextChanged += delegate
            {
                var selected = instances.SelectedItem as VisualStudioInstance;
                if (selected != null && !string.Equals(path.Text, selected.IdePath, StringComparison.OrdinalIgnoreCase)) instances.SelectedIndex = -1;
                UpdateActions();
            };
            install.SetBounds(22, 323, 160, 36);
            remove.SetBounds(193, 323, 125, 36);
            details.SetBounds(22, 377, 716, 180);
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical;
            details.Text = "Choose the Visual Studio installation to patch above, or use Browse.\r\nNo Microsoft DLLs are included in this installer.\r\nCompatibility is checked from the installed designer, without an exact build requirement.\r\nRecognized older patches can be upgraded or uninstalled directly.\r\nOriginal files are backed up locally. This setup executable is unsigned.";
            Controls.AddRange(new Control[] { heading, description, instanceLabel, instances, label, path, browse, consent, install, remove, details });
            instances.Items.AddRange(installations.Cast<object>().ToArray());
            // With multiple IDEs installed, require a deliberate choice instead of patching the newest silently.
            if (instances.Items.Count == 1) instances.SelectedIndex = 0;
            UpdateActions();
            browse.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "Select Common7\\IDE", SelectedPath = path.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.SelectedPath; };
            install.Click += async delegate { await Run(false); };
            remove.Click += async delegate { await Run(true); };
        }

        private void UpdateActions()
        {
            bool selected = !string.IsNullOrWhiteSpace(path.Text);
            install.Enabled = selected && consent.Checked;
            remove.Enabled = selected;
        }

        private async Task Run(bool uninstall)
        {
            string result = Path.Combine(Path.GetTempPath(), "CoreWF-Setup-" + Guid.NewGuid().ToString("N") + ".log");
            install.Enabled = remove.Enabled = browse.Enabled = path.Enabled = consent.Enabled = instances.Enabled = false;
            try
            {
                if (!uninstall) LocalPatch.RequireConsent(consent.Checked ? LocalPatch.Consent : null);
                LocalPatch.ValidateInstallation(path.Text, true);
                if (uninstall) PatchEngine.Preflight(LocalPatch.LoadReceipt(path.Text, true), Program.Payload, path.Text, false, true);
                if (Process.GetProcessesByName("devenv").Length > 0) throw new InvalidOperationException("Save and close all Visual Studio windows, then try again.");
                details.Text = "Waiting for Windows administrator approval…";
                var start = new ProcessStartInfo(Application.ExecutablePath, (uninstall ? "--uninstall " : "--install ") + Quote(Path.GetFullPath(path.Text)) + " " + Quote(result) + (uninstall ? "" : " " + LocalPatch.Consent))
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                int exit = await Task.Run(delegate { using (Process process = Process.Start(start)) { process.WaitForExit(); return process.ExitCode; } });
                details.Text = File.Exists(result) ? File.ReadAllText(result) : "Windows did not start the installer.";
                if (exit != 0) details.Text += "\r\nInstallation did not complete.";
            }
            catch (Exception error) { details.Text = error.Message; }
            finally
            {
                if (File.Exists(result)) File.Delete(result);
                browse.Enabled = path.Enabled = consent.Enabled = instances.Enabled = true;
                UpdateActions();
            }
        }

        private static string Quote(string value)
        {
            if (value.Contains("\"")) throw new ArgumentException("Invalid path.");
            return "\"" + value.TrimEnd('\\') + "\"";
        }
    }
}
