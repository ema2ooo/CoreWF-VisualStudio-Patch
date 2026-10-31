[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BundleDirectory,
    [string] $VisualStudioIdePath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tools\Find-VisualStudio.ps1')
$VisualStudioIdePath = Resolve-VisualStudioIde $VisualStudioIdePath
$bundle = (Resolve-Path -LiteralPath $BundleDirectory).Path
$setup = Join-Path $bundle 'CoreWF-WorkflowDesigner-Setup.exe'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('CoreWF-LocalPatch-Test-' + [Guid]::NewGuid().ToString('N').Substring(0,12))
$fixture = Join-Path $fixtureRoot 'Common7\IDE'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Set-Content -LiteralPath (Join-Path $fixture '.corewf-test-fixture') -Value 'isolated-local-patch-test'
foreach ($relative in @('Microsoft.VisualStudio.Activities.dll','HostSideAdapters\Microsoft.VisualStudio.Activities.HostAdapter.dll','AddinSideAdapters\Microsoft.VisualStudio.Activities.AddinAdapter.dll','Addins\WorkflowDesigner\Microsoft.VisualStudio.Activities.Addin.dll')) {
    $source = Join-Path $VisualStudioIdePath ('CoreWF.BuiltinPatchBackup\' + $relative)
    if (-not (Test-Path -LiteralPath $source)) { $source = Join-Path $VisualStudioIdePath $relative }
    $target = Join-Path $fixture $relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$schema = [IO.Path]::GetFullPath((Join-Path $VisualStudioIdePath '..\..\Xml\Schemas\1033\vstemplate.xsd'))
$schemaTarget = Join-Path $fixtureRoot 'Xml\Schemas\1033\vstemplate.xsd'
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($schemaTarget)) -Force | Out-Null
Copy-Item -LiteralPath $schema -Destination $schemaTarget
foreach ($api in @('Microsoft.VisualStudio.Interop.dll','Microsoft.VisualStudio.Shell.15.0.dll','Microsoft.VisualStudio.Shell.Framework.dll')) {
    $destination = Join-Path $fixture ('PublicAssemblies\' + $api)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $VisualStudioIdePath ('PublicAssemblies\' + $api)) -Destination $destination
}
# Use a different local assembly identity and harmless IL padding to exercise
# structural matching without a build/hash allowlist. These copies stay local.
Add-Type -Path (Join-Path $PSScriptRoot 'tools\BinaryPatch\Mono.Cecil.dll')
$designerPath = Join-Path $fixture 'Microsoft.VisualStudio.Activities.dll'
$designer = [Mono.Cecil.ModuleDefinition]::ReadModule($designerPath)
try {
    $designer.Assembly.Name.Version = [Version]::new($designer.Assembly.Name.Version.Major + 1, 1, 0, 0)
    $designer.Mvid = [Guid]::NewGuid()
    $pane = $designer.Types | Where-Object Name -eq 'EditorPane'
    foreach ($method in @($pane.Methods | Where-Object { $_.Name -in @('GetTargetFramework','SetReadOnly') })) {
        $il = $method.Body.GetILProcessor()
        foreach ($instruction in @($method.Body.Instructions)) { $il.InsertBefore($instruction, $il.Create([Mono.Cecil.Cil.OpCodes]::Nop)) }
    }
    $designer.Write($designerPath + '.variant')
} finally { $designer.Dispose() }
Copy-Item -LiteralPath ($designerPath + '.variant') -Destination $designerPath -Force
$profiles = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'tools\BinaryPatch\LegacyPatchProfiles.json') -Raw | ConvertFrom-Json
foreach ($profile in $profiles.Plans) {
    $matches = @($profile.Files | Where-Object { $target=Join-Path $VisualStudioIdePath $_.Target; -not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne $_.Sha256 }).Count -eq 0
    if (-not $matches) { continue }
    $legacyFixture = Join-Path $fixtureRoot 'Legacy\Common7\IDE'
    foreach ($file in $profile.Files) {
        $destination = Join-Path $legacyFixture $file.Target
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $VisualStudioIdePath $file.Target) -Destination $destination
        if ($file.Original) {
            $relativeBackup = 'CoreWF.BuiltinPatchBackup\' + $file.Target
            $destination = Join-Path $legacyFixture $relativeBackup
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $VisualStudioIdePath $relativeBackup) -Destination $destination
        }
    }
    foreach ($api in Get-ChildItem -LiteralPath (Join-Path $fixture 'PublicAssemblies') -File) {
        New-Item -ItemType Directory -Path (Join-Path $legacyFixture 'PublicAssemblies') -Force | Out-Null
        Copy-Item -LiteralPath $api.FullName -Destination (Join-Path $legacyFixture 'PublicAssemblies')
    }
    $legacySchema = Join-Path $fixtureRoot 'Legacy\Xml\Schemas\1033\vstemplate.xsd'
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($legacySchema)) -Force | Out-Null
    Copy-Item -LiteralPath $schema -Destination $legacySchema
    break
}
$setupTest = Start-Process -FilePath $setup -ArgumentList @('--self-test', ('"' + $fixture + '"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $fixtureRoot 'stdout.log') -RedirectStandardError (Join-Path $fixtureRoot 'stderr.log')
if (Test-Path -LiteralPath (Join-Path $fixture 'test-results.txt')) { Get-Content -LiteralPath (Join-Path $fixture 'test-results.txt') }
if ($setupTest.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $fixtureRoot 'stderr.log')
    throw ('Local-patching installer fixture failed. See ' + $fixtureRoot)
}
& (Join-Path $PSScriptRoot 'tools\BinaryPatch\Test-StandardDesigner.ps1') -VisualStudioIdePath $VisualStudioIdePath -WorkflowPath (Join-Path $PSScriptRoot 'samples\Minimal.xaml') -CompatibilityAssemblyPath (Join-Path $fixture 'test-generated\CoreWF.LegacyAdapters.dll')
$probe = Join-Path $PSScriptRoot 'src\BuiltinDesigner.LegacyProbe\bin\Release\net48\BuiltinDesigner.LegacyProbe.exe'
$previousIde = $env:COREWF_PROBE_IDE
try {
    $env:COREWF_PROBE_IDE = $VisualStudioIdePath
    & $probe --partition-probe (Join-Path $fixture 'test-generated\Microsoft.VisualStudio.Activities.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Patched designer partition/reference regression failed.' }
} finally { $env:COREWF_PROBE_IDE = $previousIde }
Write-Output 'PASS: Microsoft-free package, local install/repair/uninstall/rollback fixture, native Framework designer, WPF adapters, source load, and patched callback regressions.'
Write-Output ('Local-only fixture (never distribute): ' + $fixtureRoot)
