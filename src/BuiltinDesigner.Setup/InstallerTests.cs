using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BuiltinDesigner.Setup
{
    internal static class InstallerTests
    {
        private static void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("Expected rejection: " + typeof(T).Name);
        }

        private static void Restored(Plan plan, string ide)
        {
            foreach (Entry file in plan.Files)
            {
                string target = LocalPatch.TargetPath(ide, file.Target);
                if (string.IsNullOrEmpty(file.Original))
                {
                    if (File.Exists(target)) throw new Exception("An owned file was left behind: " + file.Target);
                }
                else PatchEngine.Verify(target, file.OriginalSha256);
            }
            if (File.Exists(PatchEngine.Contained(ide, LocalPatch.Receipt))) throw new Exception("Installation record was left behind.");
        }

        private static void Legacy(string fixture, Action<string> log)
        {
            string root = Directory.GetParent(Directory.GetParent(fixture).FullName).FullName;
            string ide = Path.Combine(root, "Legacy", "Common7", "IDE");
            if (!Directory.Exists(ide)) { log("SKIP: no matching installed legacy patch for the optional migration fixture."); return; }
            if (File.Exists(PatchEngine.Contained(ide, LocalPatch.Receipt))) throw new Exception("Legacy test must start without a receipt.");
            LocalPatch.ValidateInstallation(ide, false);
            Plan legacy = LocalPatch.LoadReceipt(ide, true);
            var snapshots = new List<Snapshot>();
            foreach (Entry file in legacy.Files) snapshots.Add(new Snapshot { Path = LocalPatch.TargetPath(ide, file.Target), Bytes = File.ReadAllBytes(LocalPatch.TargetPath(ide, file.Target)) });
            PatchEngine.Apply(legacy, "unused", ide, true, log);
            Restored(legacy, ide);
            log("PASS: the actual earlier patch uninstalls without a receipt or embedded originals.");
            foreach (Snapshot snapshot in snapshots) File.WriteAllBytes(snapshot.Path, snapshot.Bytes);
            string directory = Path.Combine(Program.WorkRoot, "migrated");
            Plan migrated = LocalPatch.Prepare(ide, directory, false, log);
            Reject<IOException>(delegate { PatchEngine.Apply(migrated, directory, ide, false, log, 7); });
            foreach (Entry file in legacy.Files) PatchEngine.Verify(LocalPatch.TargetPath(ide, file.Target), file.Sha256);
            if (File.Exists(PatchEngine.Contained(ide, LocalPatch.Receipt))) throw new Exception("Failed migration left a receipt.");
            PatchEngine.Apply(migrated, directory, ide, false, log);
            foreach (Entry file in migrated.Files) PatchEngine.Verify(LocalPatch.TargetPath(ide, file.Target), file.Sha256);
            PatchEngine.Apply(LocalPatch.LoadReceipt(ide, true), "unused", ide, true, log);
            Restored(migrated, ide);
            foreach (Entry file in legacy.Files.Where(file => !string.IsNullOrEmpty(file.Original))) PatchEngine.Verify(LocalPatch.BackupPath(ide, file), file.OriginalSha256);
            log("PASS: legacy migration, failed-migration rollback, and subsequent uninstall preserve the original backups.");
        }

        internal static int Run(string fixture)
        {
            fixture = Path.GetFullPath(fixture);
            string marker = Path.Combine(fixture, ".corewf-test-fixture");
            if (!fixture.Contains("CoreWF-LocalPatch-Test-") || !File.Exists(marker) ||
                File.ReadAllText(marker).Trim() != "isolated-local-patch-test" || File.Exists(Path.Combine(fixture, "devenv.exe")))
                throw new InvalidOperationException("Self-test requires an isolated, marked fixture; a real IDE cannot be used.");
            using (var report = new StreamWriter(Path.Combine(fixture, "test-results.txt")))
            {
                try
                {
                    Action<string> log = report.WriteLine;
                    LocalPatch.ValidatePackage();
                    if (Program.Package.Files.Count(file => file.Path.EndsWith(".dll")) != 2)
                        throw new Exception("The package must contain only our bridge and Mono.Cecil DLLs.");
                    Reject<InvalidOperationException>(delegate { LocalPatch.RequireConsent(null); });
                    using (var window = new SetupWindow())
                    {
                        if (window.Controls.OfType<CheckBox>().Single().Checked ||
                            window.Controls.OfType<Button>().Single(button => button.Text == "Install / Repair").Enabled)
                            throw new Exception("Consent must be unchecked and installation disabled by default.");
                    }
                    var installations = new[]
                    {
                        new VisualStudioInstance { DisplayName = "Visual Studio 2026", IdePath = @"C:\Test\VS2026\Common7\IDE" },
                        new VisualStudioInstance { DisplayName = "Visual Studio 2022", IdePath = @"C:\Test\VS2022\Common7\IDE" }
                    };
                    using (var window = new SetupWindow(installations))
                    {
                        var selector = window.Controls.OfType<ComboBox>().Single();
                        var path = window.Controls.OfType<TextBox>().Single(control => control.Name == "IdePath");
                        var consent = window.Controls.OfType<CheckBox>().Single();
                        var install = window.Controls.OfType<Button>().Single(button => button.Text == "Install / Repair");
                        var uninstall = window.Controls.OfType<Button>().Single(button => button.Text == "Uninstall");
                        consent.Checked = true;
                        if (selector.Items.Count != 2 || selector.SelectedIndex != -1 || path.Text.Length != 0 || install.Enabled || uninstall.Enabled)
                            throw new Exception("Multiple Visual Studio installations require an explicit selection.");
                        foreach (int index in new[] { 1, 0, 1 })
                        {
                            selector.SelectedIndex = index;
                            if (path.Text != installations[index].IdePath || !install.Enabled || !uninstall.Enabled)
                                throw new Exception("The selected Visual Studio did not become the operation target.");
                        }
                        path.Text = @"D:\CustomVisualStudio\Common7\IDE";
                        if (selector.SelectedIndex != -1 || !install.Enabled)
                            throw new Exception("A manually selected IDE must clear the previous installation label.");
                        consent.Checked = false;
                        if (install.Enabled || !uninstall.Enabled) throw new Exception("Selection changed the consent requirement.");
                    }
                    using (var window = new SetupWindow(new[] { installations[1] }))
                        if (window.Controls.OfType<ComboBox>().Single().SelectedIndex != 0)
                            throw new Exception("A single installation should be selected automatically.");
                    log("PASS: package inventory and explicit consent by default.");
                    log("PASS: Visual Studio selection controls the exact target, requires a choice for multiple IDEs, and preserves manual browsing and consent.");
                    Legacy(fixture, log);

                    string generated = Path.Combine(Program.WorkRoot, "generated-first");
                    Plan first = LocalPatch.Prepare(fixture, generated, false, log);
                    if (first.VisualStudioVersion == "18.9.12128.139") throw new Exception("The structural fixture must have a different identity from the former version pin.");
                    Restored(first, fixture);
                    PatchEngine.Apply(first, generated, fixture, false, log);
                    foreach (Entry file in first.Files) PatchEngine.Verify(LocalPatch.TargetPath(fixture, file.Target), file.Sha256);
                    log("PASS: preparation leaves inputs untouched; local installation and receipt verified.");

                    string repairDirectory = Path.Combine(Program.WorkRoot, "generated-repair");
                    Plan repair = LocalPatch.Prepare(fixture, repairDirectory, false, log);
                    if (first.Files.Single(file => file.Target == "Microsoft.VisualStudio.Activities.dll").Sha256 ==
                        repair.Files.Single(file => file.Target == "Microsoft.VisualStudio.Activities.dll").Sha256)
                        throw new Exception("Repair should test a newly generated patch identity.");
                    PatchEngine.Apply(repair, repairDirectory, fixture, false, log);
                    foreach (Entry file in repair.Files) PatchEngine.Verify(LocalPatch.TargetPath(fixture, file.Target), file.Sha256);
                    log("PASS: repair accepts only the prior recorded patch and freshly generated outputs.");

                    string receiptPath = PatchEngine.Contained(fixture, LocalPatch.Receipt);
                    string receiptHash = PatchEngine.Hash(receiptPath);
                    Reject<IOException>(delegate { PatchEngine.Apply(first, generated, fixture, false, log, 7); });
                    foreach (Entry file in repair.Files) PatchEngine.Verify(LocalPatch.TargetPath(fixture, file.Target), file.Sha256);
                    PatchEngine.Verify(receiptPath, receiptHash);
                    Reject<IOException>(delegate { PatchEngine.Apply(repair, repairDirectory, fixture, true, log, 7); });
                    foreach (Entry file in repair.Files) PatchEngine.Verify(LocalPatch.TargetPath(fixture, file.Target), file.Sha256);
                    PatchEngine.Verify(receiptPath, receiptHash);
                    log("PASS: failed repair and uninstall preserve the previous installation and its receipt.");
                    foreach (bool uninstall in new[] { false, true })
                    {
                        Reject<IOException>(delegate { PatchEngine.Apply(repair, repairDirectory, fixture, uninstall, log, repair.Files.Length); });
                        foreach (Entry file in repair.Files) PatchEngine.Verify(LocalPatch.TargetPath(fixture, file.Target), file.Sha256);
                        PatchEngine.Verify(receiptPath, receiptHash);
                    }
                    log("PASS: failures after modifying template catalogs and MSBuild capabilities restore every metadata file and receipt.");

                    // Keep this locally generated DLL only in the ignored fixture for the callback probe.
                    string probe = Path.Combine(fixture, "test-generated");
                    Directory.CreateDirectory(probe);
                    File.Copy(Path.Combine(repairDirectory, "Microsoft.VisualStudio.Activities.dll"), Path.Combine(probe, "Microsoft.VisualStudio.Activities.dll"));
                    File.Copy(Path.Combine(repairDirectory, "CoreWF.LegacyAdapters.dll"), Path.Combine(probe, "CoreWF.LegacyAdapters.dll"));
                    foreach (Entry file in repair.Files.Where(file => LocalPatch.IsTemplateOriginal(file.Target)))
                    {
                        string destination = PatchEngine.Contained(probe, file.Target);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        File.Copy(PatchEngine.Contained(repairDirectory, file.Payload), destination);
                    }
                    Plan receipt = LocalPatch.LoadReceipt(fixture, true);
                    PatchEngine.Apply(receipt, Path.Combine(Program.WorkRoot, "no-payload-here"), fixture, true, log);
                    Restored(repair, fixture);
                    log("PASS: uninstall restores verified local backups without an embedded or generated Microsoft payload.");

                    // Earlier receipt-based installers copied CoreWF templates but
                    // did not patch catalogs or design-time capabilities.
                    var old = new Plan { VisualStudioVersion = first.VisualStudioVersion,
                        Files = first.Files.Where(file => !LocalPatch.IsTemplateOriginal(file.Target)).ToArray() };
                    foreach (Entry file in old.Files)
                    {
                        string destination = LocalPatch.TargetPath(fixture, file.Target);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        File.Copy(PatchEngine.Contained(generated, file.Payload), destination, true);
                    }
                    File.WriteAllText(receiptPath, new JavaScriptSerializer().Serialize(old));
                    string upgradeDirectory = Path.Combine(Program.WorkRoot, "generated-upgrade");
                    Plan upgrade = LocalPatch.Prepare(fixture, upgradeDirectory, false, log);
                    PatchEngine.Apply(upgrade, upgradeDirectory, fixture, false, log);
                    PatchEngine.Apply(LocalPatch.LoadReceipt(fixture, true), "unused", fixture, true, log);
                    Restored(upgrade, fixture);
                    log("PASS: existing receipt-based installations upgrade to runtime-filtered templates and uninstall cleanly.");

                    Reject<IOException>(delegate { PatchEngine.Apply(first, generated, fixture, false, log, 5); });
                    Restored(first, fixture);
                    log("PASS: injected installation failure rolls back all changed files and the receipt.");

                    string owned = Path.Combine(fixture, "CoreWF.LegacyAdapters.dll");
                    File.WriteAllText(owned, "pre-existing unrelated dependency");
                    Reject<InvalidOperationException>(delegate { PatchEngine.Preflight(first, generated, fixture, false); });
                    File.Delete(owned);
                    Entry original = first.Files.First(file => !string.IsNullOrEmpty(file.Original));
                    string target = LocalPatch.TargetPath(fixture, original.Target);
                    byte[] bytes = File.ReadAllBytes(target);
                    File.WriteAllText(target, "not a managed designer assembly");
                    Reject<BadImageFormatException>(delegate { LocalPatch.ValidateInstallation(fixture, false); });
                    File.WriteAllBytes(target, bytes);
                    string backup = LocalPatch.BackupPath(fixture, original);
                    File.AppendAllText(backup, "unsupported");
                    Reject<InvalidDataException>(delegate { PatchEngine.Preflight(first, generated, fixture, false); });
                    File.WriteAllBytes(backup, bytes);
                    log("PASS: unknown installed files, unrelated dependencies, and damaged backups are refused.");

                    string script = PatchEngine.Contained(Program.Payload, "tools/BinaryPatch/Prepare-LocalPatch.ps1");
                    bytes = File.ReadAllBytes(script);
                    File.AppendAllText(script, "tampered");
                    Reject<InvalidDataException>(LocalPatch.ValidatePackage);
                    File.WriteAllBytes(script, bytes);
                    Reject<InvalidDataException>(delegate { PatchEngine.Contained(fixture, "../escape"); });
                    var serializer = new JavaScriptSerializer();
                    Plan invalid = serializer.Deserialize<Plan>(serializer.Serialize(first));
                    invalid.Files[0].Target = "../escape";
                    Reject<InvalidDataException>(delegate { LocalPatch.ValidatePlan(invalid, false); });
                    Restored(first, fixture);
                    log("PASS: payload tampering and escaping installation records are refused.");
                    log("PASS: all local-patching installer checks.");
                    return 0;
                }
                catch (Exception error) { report.WriteLine(error); throw; }
            }
        }
    }
}
