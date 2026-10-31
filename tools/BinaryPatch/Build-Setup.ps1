[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$build = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) ('setup-build-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $build 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$sources = @{
    'CoreWF.LegacyAdapters.dll' = Join-Path $repository 'src\CoreWF.LegacyAdapters\bin\Release\net48\CoreWF.LegacyAdapters.dll'
    'tools/BinaryPatch/Mono.Cecil.dll' = Join-Path $PSScriptRoot 'Mono.Cecil.dll'
    # These are Mono.Cecil's upstream terms, generated outside source control.
    # Keep the payload names compatible with earlier installation records.
    'LICENSE' = Join-Path $repository '.packages\mono.cecil\0.11.6\LICENSE.txt'
    'THIRD-PARTY-NOTICES.md' = Join-Path $repository '.packages\mono.cecil\0.11.6\LICENSE.txt'
    'tools/BinaryPatch/LegacyPatchProfiles.json' = Join-Path $PSScriptRoot 'LegacyPatchProfiles.json'
}
foreach ($script in @('Build-StandardDesignerPatch.ps1','Build-LegacyAdapters.ps1','Build-DynamicActivities.ps1','Build-WorkflowTemplates.ps1','Build-WorkflowRuntimeFilter.ps1','Install-StandardDesignerPatch.ps1','Install-LegacyAdapters.ps1','Install-DynamicActivities.ps1','Install-WorkflowTemplates.ps1','Prepare-LocalPatch.ps1','Build-CompatibleBridge.ps1','Patch-Support.ps1')) {
    $sources['tools/BinaryPatch/' + $script] = Join-Path $PSScriptRoot $script
}
foreach ($template in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Templates') -File -Recurse) {
    $relative = $template.FullName.Substring($PSScriptRoot.Length + 1).Replace('\','/')
    $sources['tools/BinaryPatch/' + $relative] = $template.FullName
}
$files = @()
foreach ($relative in $sources.Keys | Sort-Object) {
    if ($relative.EndsWith('.dll') -and $relative -notin @('CoreWF.LegacyAdapters.dll','tools/BinaryPatch/Mono.Cecil.dll')) { throw 'Microsoft or unexpected DLL cannot be packaged.' }
    $destination = Join-Path $payload $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $sources[$relative] -Destination $destination
    $files += [ordered]@{ Path=$relative; Sha256=(Get-FileHash -LiteralPath $destination).Hash }
}
[ordered]@{
    SchemaVersion=3
    Files=$files
    Originals=@(
        @{ Target='Microsoft.VisualStudio.Activities.dll' },
        @{ Target='HostSideAdapters/Microsoft.VisualStudio.Activities.HostAdapter.dll' },
        @{ Target='AddinSideAdapters/Microsoft.VisualStudio.Activities.AddinAdapter.dll' },
        @{ Target='Addins/WorkflowDesigner/Microsoft.VisualStudio.Activities.Addin.dll' }
    )
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $payload 'package.json') -Encoding UTF8
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath (Join-Path $build 'Payload.zip')
@'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><assemblyIdentity version="2.0.0.0" name="CoreWF.WorkflowDesignerPatch.Setup"/><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo><compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility></assembly>
'@ | Set-Content -LiteralPath (Join-Path $build 'app.manifest') -Encoding UTF8
$source = Join-Path $repository 'src\BuiltinDesigner.Setup\Program.cs'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$OutputPath" "/win32manifest:$build\app.manifest" "/resource:$build\Payload.zip,Payload.zip" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$PSScriptRoot\Mono.Cecil.dll" $source (Join-Path $repository 'src\BuiltinDesigner.Setup\LocalPatch.cs') (Join-Path $repository 'src\BuiltinDesigner.Setup\InstallerTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
Write-Output ('Built local-patching installer: ' + $OutputPath)
Write-Output ('Build workspace: ' + $build)
