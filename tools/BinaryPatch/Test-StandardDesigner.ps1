[CmdletBinding()]
param(
    [string]$VisualStudioIdePath = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE',
    [string[]]$WorkflowPath = @(),
    [string]$CompatibilityAssemblyPath
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$visualStudioRoot = [IO.Path]::GetFullPath((Join-Path $VisualStudioIdePath '..\..'))
$project = Join-Path $repository 'src\BuiltinDesigner.LegacyProbe\BuiltinDesigner.LegacyProbe.csproj'
& dotnet build $project -c Release '-p:NuGetAudit=false' "-p:VisualStudioInstallRoot=$visualStudioRoot" -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed. Build-StandardDesignerPatch.ps1 must create the signing key first.' }
$probe = Join-Path $repository 'src\BuiltinDesigner.LegacyProbe\bin\Release\net48\BuiltinDesigner.LegacyProbe.exe'
if ($CompatibilityAssemblyPath) { Copy-Item -LiteralPath $CompatibilityAssemblyPath -Destination (Join-Path ([IO.Path]::GetDirectoryName($probe)) 'CoreWF.LegacyAdapters.dll') -Force }
$config = [Xml.XmlDocument]::new()
$config.PreserveWhitespace = $true
$config.Load((Join-Path $VisualStudioIdePath 'devenv.exe.config'))
$names = [Xml.XmlNamespaceManager]::new($config.NameTable)
$names.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
foreach ($codeBase in $config.SelectNodes('//asm:codeBase', $names)) {
    if (-not [Uri]::IsWellFormedUriString($codeBase.href, [UriKind]::Absolute)) {
        $codeBase.href = ([Uri][IO.Path]::GetFullPath((Join-Path $VisualStudioIdePath $codeBase.href))).AbsoluteUri
    }
}
# Match Visual Studio's WPF bindings in the test process without changing the IDE.
$config.Save($probe + '.config')
$oldIde = $env:COREWF_PROBE_IDE
try {
    $env:COREWF_PROBE_IDE = $VisualStudioIdePath
    & $probe
    if ($LASTEXITCODE -ne 0) { throw 'Framework adapter/runtime probe failed.' }
    foreach ($workflow in $WorkflowPath) {
        & $probe ([IO.Path]::GetFullPath($workflow))
        if ($LASTEXITCODE -ne 0) { throw ('Stock designer model load failed: ' + $workflow) }
    }
} finally { $env:COREWF_PROBE_IDE = $oldIde }
