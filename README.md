# CoreWF Visual Studio Patch

Edit workflows in modern .NET/CoreWF projects with Visual Studio's **built-in
Windows Workflow Foundation designer** and **standard Toolbox**.

The designer runs on .NET Framework. Workflow execution stays in your
application's modern .NET runtime.

## Features

- Open workflow XAML in the original Visual Studio designer and document tabs.
- Keep the standard Toolbox and property grid.
- Discover public custom activities after a successful build and drag them onto
  workflows from the Toolbox.
- Add Sequence, Flowchart, and State Machine items to SDK-style projects.
- Avoid repeated legacy MSBuild evaluation when opening modern projects.
- Install, repair, or restore the patch with verified local backups and rollback.
- Upgrade or uninstall recognized earlier patches without an installation record.

## How it works

The installer reads the Workflow Designer components already installed on your
computer. It checks their identities, method signatures, fields, and call
patterns, then generates the changes locally after your consent.

**There is no exact Visual Studio build requirement.** The compatibility library
is adapted to the installed Visual Studio APIs. Incompatible component layouts
are reported before installation files are changed.

**No original or modified Microsoft DLLs are included in the installer or this
repository.** The installer carries this project's compatibility library,
Mono.Cecil, patch scripts, item templates, and recovery metadata.

## Requirements

- Windows x64 and .NET Framework 4.8.
- Visual Studio with the Windows Workflow Foundation components installed.
- Windows PowerShell 5.1.

Installation does not require a .NET SDK, compiler, or NuGet download. The
installer lists local Visual Studio installations by name and version. When
several are installed, choose the one to patch; none is selected automatically.
Use **Browse** for an installation outside the list.

## Install

1. Save your work and close all Visual Studio windows.
2. Run `CoreWF-WorkflowDesigner-Setup.exe`.
3. Choose the Visual Studio installation from the list, or use **Browse** to
   select its `Common7\IDE` folder. Check the displayed path, especially when
   Visual Studio 2022 and a newer version are both installed.
4. Check the authorization box and choose **Install / Repair**.
5. Approve the Windows administrator prompt.
6. Reopen Visual Studio and build your project before opening a workflow.

After adding or changing a custom activity, build the project again to refresh
its design-time metadata and Toolbox entry. The setup executable is unsigned.

For new workflow files, choose **Activity (CoreWF)**, **Flowchart Workflow
(CoreWF)**, or **State Machine Workflow (CoreWF)** in **Add New Item**. The
original **Activity** template uses the .NET Framework reference wizard and
XAML compiler, which cannot build modern .NET workflows.

CoreWF workflow XAML is loaded by the application's workflow runtime. These
templates use build action **None** and the standard designer; they do not
generate an `Activity1` C# class during the build. Add a compatible workflow
runtime package to your application when you want to execute the workflow.

## Repair and uninstall

Run the installer again and select the same Visual Studio installation. Choose
**Install / Repair** to regenerate the changes, or **Uninstall** to restore the
original designer components.

Original files and the installation record are kept in
`Common7\IDE\CoreWF.BuiltinPatchBackup`. Keep this directory. Backups are stored
by their full SHA-256, so different component builds do not overwrite each other.

Recognized earlier packages are described in
`tools/BinaryPatch/LegacyPatchProfiles.json`. That file contains paths and hashes
only. Recovery requires all installed patch files and original backups to match
the known package. Unrecognized older patches need their original installer.

Uninstall before updating Visual Studio. If an update has changed a patched
component, recovery refuses to copy an older component over the updated file.

## Build from source

Build prerequisites:

- .NET SDK 10.
- .NET Framework 4.8 developer/reference assemblies.
- A Visual Studio installation with Workflow Foundation.
- Windows PowerShell 5.1 or PowerShell 7.
- Access to NuGet and GitHub for dependency restoration.

```powershell
.\Build.ps1 -OutputDirectory .\artifacts\setup
.\Test.ps1 -BundleDirectory .\artifacts\setup
```

The installer is written to:

```text
artifacts/setup/CoreWF-WorkflowDesigner-Setup.exe
```

Build and test prefer the newest local Visual Studio installation containing the
Workflow Designer. Pass `-VisualStudioIdePath "<Common7\IDE>"` to either script to
select a different installation.

The build restores Mono.Cecil and retrieves its upstream dependency notice for
the installer. Restored packages, generated notices, signing keys, binaries,
and local test files are excluded from source control. Build and test do not
install the patch or change Visual Studio configuration.

## Verification

Automated checks use components from:

| Installation | Component build |
| --- | --- |
| Visual Studio 2022 | 17.13.35931.197 |
| Visual Studio 2026 | 18.9.12128.139 |

Checks cover installation, repair, uninstall, rollback, native designer loading,
WPF hosting, and patched callbacks. Runtime probes use the compatibility library
generated by the installer. Tests also change assembly versions and add harmless
instruction padding to exercise structural matching.

Framework callback checks invoke the generated Visual Studio DLL with .NET 8,
.NET 10, .NET Standard, and .NET Framework targets, then pass the result to the
native Workflow Designer configuration service. Installer checks also verify
that choosing Visual Studio 2022 targets that instance when newer IDEs exist.

Migration and recovery of an earlier patch are tested in an isolated copy of
the installation. Local test fixtures contain Microsoft components and must not
be distributed. These checks do not establish compatibility with every past or
future Workflow Designer implementation.

For a check that leaves the Visual Studio installation unchanged:

```powershell
.\CoreWF-WorkflowDesigner-Setup.exe --check "<Common7\IDE>"
.\CoreWF-WorkflowDesigner-Setup.exe --check-uninstall "<Common7\IDE>"
```

The first command prepares and verifies a patch without installing it. The
second verifies recovery of the current patch. Neither needs administrator
approval. Command-line operations return a nonzero exit code on failure.

## Limitations

Public activity metadata is projected into cached .NET Framework design-time
assemblies. Business method bodies are not copied or executed by the designer.

Common activity arguments, Framework-compatible public property types, and
simple literal initializers are supported. Signed activity assemblies, custom
modern designer implementations, runtime-dependent initializers, and types
unavailable in .NET Framework need a compatible design-time assembly.

**Graphical XAML breakpoint binding for CoreWF remains unresolved.** C# activity
breakpoints and graphical workflow breakpoints are separate capabilities.

## Source layout

| Path | Purpose |
| --- | --- |
| `src/CoreWF.LegacyAdapters` | Designer compatibility, WPF hosting, activity metadata, and Toolbox integration |
| `src/BuiltinDesigner.Setup` | Installer, recovery, validation, and transaction tests |
| `src/BuiltinDesigner.LegacyProbe` | Native designer and callback checks |
| `tools/BinaryPatch` | Component transformations, templates, and recovery metadata |
| `tools/Workarounds/Fix-DevHub.ps1` | Optional, separate DevHub stability workaround |

The DevHub workaround is not applied by the installer. Its script provides
`Check`, `Apply`, and `Restore` modes and keeps a backup. Long-term effectiveness
of that workaround remains unconfirmed.

## Diagnostics

### Legacy Activity template in a modern project

Reference-wizard errors for `System.Activities` or `Microsoft.CSharp`, and
`XC1000` / `XC1020` errors mentioning `System.Runtime` or `System.Object`, can
occur when a modern project contains legacy `XamlAppDef` workflow items.

Replace those items and their `None Remove` entries with the following settings
for each affected workflow. Clear the file's **Custom Tool** property as well.

```xml
<ItemGroup>
  <None Update="Activity1.xaml">
    <SubType>Designer</SubType>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

Do not retain `<Generator>MSBuild:Compile</Generator>` on these workflow files.
The copy setting makes the XAML available beside the application for runtime
loading. For new items, choose one of the **(CoreWF)** templates above.

### Designer initialization

If the designer reports that `.NETCoreApp` is unsupported, check that you
patched the Visual Studio instance currently running. Installing the patch
for a newer Visual Studio does not change Visual Studio 2022. Close all IDEs,
select **Visual Studio 2022** in setup, and choose **Install / Repair**.

```text
%LOCALAPPDATA%\CoreWF.DesignerPatch\standard-designer.log
%LOCALAPPDATA%\CoreWF.DesignerPatch\activity-discovery.log
```

For bug reports, include the Visual Studio version, a relevant log excerpt,
and a minimal workflow. Remove credentials, application data, and private paths
before posting.
