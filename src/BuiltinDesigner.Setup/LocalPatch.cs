using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BuiltinDesigner.Setup
{
    internal sealed class PackageFile
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class OriginalFile
    {
        public string Target { get; set; }
    }

    internal sealed class PackageManifest
    {
        public int SchemaVersion { get; set; }
        public PackageFile[] Files { get; set; }
        public OriginalFile[] Originals { get; set; }
    }

    internal sealed class LegacyProfiles
    {
        public Plan[] Plans { get; set; }
    }

    internal static class LocalPatch
    {
        internal const string Receipt = "CoreWF.BuiltinPatchBackup/local-install-plan.json";
        internal const string Consent = "--consent-local-patch";

        internal static T Read<T>(string path)
        {
            return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path));
        }

        internal static void RequireConsent(string value)
        {
            if (value != Consent) throw new InvalidOperationException("Explicit consent to patch this local Visual Studio installation is required.");
        }

        internal static void ValidatePackage()
        {
            PackageManifest package = Program.Package;
            if (package == null || package.SchemaVersion != 3 || package.Files == null || package.Originals == null || package.Originals.Length != 4)
                throw new InvalidDataException("Unsupported local-patch package.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "package.json" };
            foreach (PackageFile file in package.Files)
            {
                if (!paths.Add(file.Path)) throw new InvalidDataException("Duplicate package file.");
                string path = PatchEngine.Contained(Program.Payload, file.Path);
                PatchEngine.Verify(path, file.Sha256);
                bool permittedDll = file.Path == "CoreWF.LegacyAdapters.dll" || file.Path == "tools/BinaryPatch/Mono.Cecil.dll";
                using (var input = File.OpenRead(path))
                {
                    bool executable = input.ReadByte() == 'M' && input.ReadByte() == 'Z';
                    if (executable != permittedDll) throw new InvalidDataException("An unexpected executable is present in the package: " + file.Path);
                }
                if (file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !permittedDll)
                    throw new InvalidDataException("Microsoft or unexpected DLLs cannot be distributed in this installer.");
                if (permittedDll)
                {
                    string name = AssemblyName.GetAssemblyName(path).Name;
                    if (name != (file.Path == "CoreWF.LegacyAdapters.dll" ? "CoreWF.LegacyAdapters" : "Mono.Cecil"))
                        throw new InvalidDataException("Unexpected packaged assembly.");
                }
            }
            foreach (string path in Directory.GetFiles(Program.Payload, "*", SearchOption.AllDirectories))
            {
                string relative = path.Substring(Program.Payload.Length + 1).Replace('\\', '/');
                if (!paths.Contains(relative)) throw new InvalidDataException("An unlisted file is present in the package.");
            }
        }

        private static HashSet<string> AllowedTargets()
        {
            var targets = new HashSet<string>(Program.Package.Originals.Select(file => file.Target), StringComparer.OrdinalIgnoreCase)
            {
                "CoreWF.LegacyAdapters.dll", "Mono.Cecil.dll", "CoreWF.DesignerPatch.LICENSE.txt", "CoreWF.DesignerPatch.NOTICES.md",
                "ItemTemplates/CoreWF.BuiltinDesigner.Item.vstman"
            };
            foreach (string kind in new[] { "Sequence", "Flowchart", "StateMachine" })
                foreach (string name in new[] { "Workflow.xaml", "Workflow.vstemplate" })
                    targets.Add("ItemTemplates/CSharp/Workflow/1033/CoreWF" + kind + "/" + name);
            return targets;
        }

        internal static void ValidatePlan(Plan plan, bool fromPackage)
        {
            if (plan == null || string.IsNullOrEmpty(plan.VisualStudioVersion) || plan.Files == null)
                throw new InvalidDataException("Unsupported local installation record.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Entry file in plan.Files)
            {
                PatchEngine.Contained(Program.Payload, file.Target);
                PatchEngine.Contained(Program.Payload, file.Payload);
                if (!seen.Add(file.Target) || !IsHash(file.Sha256))
                    throw new InvalidDataException("Invalid or duplicate installation target.");
                OriginalFile original = Program.Package.Originals.SingleOrDefault(item => string.Equals(item.Target, file.Target, StringComparison.OrdinalIgnoreCase));
                if (original != null)
                {
                    if (string.IsNullOrEmpty(file.Original) || !IsHash(file.OriginalSha256))
                        throw new InvalidDataException("Unexpected original designer identity.");
                    PatchEngine.Contained(Program.Payload, file.Original);
                    BackupPath(Program.Payload, file);
                }
                else if (!string.IsNullOrEmpty(file.Original) || !string.IsNullOrEmpty(file.OriginalSha256) || !string.IsNullOrEmpty(file.Backup))
                    throw new InvalidDataException("Unexpected original-file record.");
                if (fromPackage)
                {
                    string source = file.Target == "Mono.Cecil.dll" ? "tools/BinaryPatch/Mono.Cecil.dll" :
                        file.Target == "CoreWF.DesignerPatch.LICENSE.txt" ? "LICENSE" :
                        file.Target == "CoreWF.DesignerPatch.NOTICES.md" ? "THIRD-PARTY-NOTICES.md" : null;
                    if (source != null && file.Sha256 != Program.Package.Files.Single(item => item.Path == source).Sha256)
                        throw new InvalidDataException("Generated patch changed a packaged dependency or notice.");
                }
            }
            var allowed = AllowedTargets();
            if (!fromPackage && plan.Files.Length == allowed.Count - 2)
            {
                allowed.Remove("CoreWF.DesignerPatch.LICENSE.txt");
                allowed.Remove("CoreWF.DesignerPatch.NOTICES.md");
            }
            if (!seen.SetEquals(allowed)) throw new InvalidDataException("Unexpected or incomplete installation target list.");
        }

        private static bool IsHash(string value)
        {
            return value != null && value.Length == 64 && value.All(character => "0123456789ABCDEF".IndexOf(character) >= 0);
        }

        internal static string BackupPath(string ide, Entry file)
        {
            string expected = "CoreWF.BuiltinPatchBackup/originals/" + file.OriginalSha256 + "/" + Path.GetFileName(file.Target);
            if (!string.IsNullOrEmpty(file.Backup) && file.Backup != expected)
                throw new InvalidDataException("Unexpected original backup location.");
            return PatchEngine.Contained(ide, string.IsNullOrEmpty(file.Backup) ? "CoreWF.BuiltinPatchBackup/" + file.Target : expected);
        }

        internal static Plan LoadReceipt(string ide, bool required)
        {
            string path = PatchEngine.Contained(ide, Receipt);
            if (!File.Exists(path))
            {
                var profiles = Read<LegacyProfiles>(PatchEngine.Contained(Program.Payload, "tools/BinaryPatch/LegacyPatchProfiles.json"));
                foreach (Plan legacy in profiles.Plans)
                {
                    ValidatePlan(legacy, false);
                    if (!legacy.Files.All(file => File.Exists(PatchEngine.Contained(ide, file.Target)) &&
                        PatchEngine.Hash(PatchEngine.Contained(ide, file.Target)) == file.Sha256)) continue;
                    foreach (Entry file in legacy.Files.Where(file => !string.IsNullOrEmpty(file.Original)))
                        PatchEngine.Verify(BackupPath(ide, file), file.OriginalSha256);
                    return legacy;
                }
                if (required) throw new InvalidOperationException("No recognized patch or installation record was found. Keep the original backups and use the installer that applied an unrecognized older patch.");
                return null;
            }
            Plan plan = Read<Plan>(path);
            ValidatePlan(plan, false);
            return plan;
        }

        internal static void ValidateInstallation(string ide, bool requireVisualStudio)
        {
            if (requireVisualStudio)
            {
                if (!File.Exists(Path.Combine(ide, "devenv.exe"))) throw new FileNotFoundException("Select a Visual Studio Common7\\IDE folder containing devenv.exe.");
            }
            Plan prior = LoadReceipt(ide, false);
            foreach (OriginalFile original in Program.Package.Originals)
            {
                string target = PatchEngine.Contained(ide, original.Target);
                string current = PatchEngine.Hash(target);
                Entry previous = prior == null ? null : prior.Files.Single(file => file.Target == original.Target);
                if (previous != null)
                {
                    if (current != previous.Sha256 && current != previous.OriginalSha256)
                        throw new InvalidOperationException("The installed designer changed since patching. No older files will be restored over a Visual Studio update: " + original.Target);
                    PatchEngine.Verify(BackupPath(ide, previous), previous.OriginalSha256);
                }
                else
                {
                    using (var module = Mono.Cecil.ModuleDefinition.ReadModule(target))
                    {
                        var identity = module.Assembly.Name;
                        if (!string.Equals(identity.Name, Path.GetFileNameWithoutExtension(target), StringComparison.OrdinalIgnoreCase) ||
                            BitConverter.ToString(identity.PublicKeyToken).Replace("-", "") != "31BF3856AD364E35")
                            throw new InvalidDataException("Unexpected Workflow Designer assembly identity: " + original.Target);
                        if (module.AssemblyReferences.Any(name => name.Name == "CoreWF.LegacyAdapters"))
                            throw new InvalidOperationException("An unrecognized older CoreWF patch is installed. Preserve its original backups and uninstall it with its original installer: " + original.Target);
                    }
                }
            }
        }

        internal static string FindInstalledIde()
        {
            string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere)) return null;
            var start = new ProcessStartInfo(vswhere, "-products * -prerelease -sort -property installationPath")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (Process process = Process.Start(start))
            {
                string roots = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) return null;
                foreach (string root in roots.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string ide = Path.Combine(root.Trim(), "Common7", "IDE");
                    if (File.Exists(Path.Combine(ide, "devenv.exe")) && File.Exists(Path.Combine(ide, "Microsoft.VisualStudio.Activities.dll"))) return ide;
                }
                return null;
            }
        }

        internal static Plan Prepare(string ide, string output, bool requireVisualStudio, Action<string> log)
        {
            ValidatePackage();
            ValidateInstallation(ide, requireVisualStudio);
            Plan prior = LoadReceipt(ide, false);
            string inputs = output + "-inputs";
            Directory.CreateDirectory(inputs);
            foreach (OriginalFile original in Program.Package.Originals)
            {
                Entry previous = prior == null ? null : prior.Files.Single(file => file.Target == original.Target);
                string source = previous == null ? PatchEngine.Contained(ide, original.Target) : BackupPath(ide, previous);
                string destination = PatchEngine.Contained(inputs, original.Target);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, destination);
            }
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string script = PatchEngine.Contained(Program.Payload, "tools/BinaryPatch/Prepare-LocalPatch.ps1");
            var start = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(script) +
                " -VisualStudioIdePath " + Quote(ide) + " -OutputDirectory " + Quote(output) + " -OriginalDirectory " + Quote(inputs))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            // A launcher running under PowerShell 7 can pass incompatible module paths.
            // Preparation uses only the Windows PowerShell modules shipped with Windows.
            start.EnvironmentVariables["PSModulePath"] = Path.Combine(Path.GetDirectoryName(powershell), "Modules");
            log("Preparing changes from the verified designer files on this computer...");
            using (Process process = Process.Start(start))
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(180000)) { process.Kill(); throw new TimeoutException("Local patch preparation timed out; Visual Studio files were not changed."); }
                Task.WaitAll(stdout, stderr);
                if (process.ExitCode != 0) throw new InvalidOperationException("Local patch preparation failed. No designer files were installed.\r\n" + stderr.Result + stdout.Result);
            }
            Plan plan = Read<Plan>(Path.Combine(output, "install-plan.json"));
            ValidatePlan(plan, true);
            if (AssemblyName.GetAssemblyName(Path.Combine(output, "CoreWF.LegacyAdapters.dll")).FullName !=
                AssemblyName.GetAssemblyName(Path.Combine(Program.Payload, "CoreWF.LegacyAdapters.dll")).FullName)
                throw new InvalidDataException("Local API binding changed the compatibility library identity.");
            PatchEngine.Preflight(plan, output, ide, false);
            return plan;
        }

        internal static string Quote(string value)
        {
            if (value.Contains('"')) throw new ArgumentException("Invalid path.");
            return "\"" + value.TrimEnd('\\') + "\"";
        }
    }
}
