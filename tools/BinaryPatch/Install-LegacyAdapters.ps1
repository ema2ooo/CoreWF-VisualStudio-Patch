[CmdletBinding()]
param(
    [ValidateSet('Check', 'Apply', 'Restore')][string]$Mode = 'Check',
    [string]$VisualStudioIdePath = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE'
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'legacy-adapter-manifest.json') -Raw | ConvertFrom-Json
$ide = (Resolve-Path -LiteralPath $VisualStudioIdePath).Path
function Assert-Hash([string]$Path, [string]$Hash) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) { throw ('Unexpected file revision: ' + $Path) }
}
$compatibilityName = 'CoreWF.LegacyAdapters.dll'
Assert-Hash (Join-Path $PSScriptRoot $compatibilityName) $manifest.compatibilitySha256
foreach ($entry in $manifest.adapters) {
    if ($entry.path -notin @('HostSideAdapters/Microsoft.VisualStudio.Activities.HostAdapter.dll','AddinSideAdapters/Microsoft.VisualStudio.Activities.AddinAdapter.dll')) { throw 'Unexpected adapter path.' }
    Assert-Hash (Join-Path $PSScriptRoot ('LegacyAdapters\' + $entry.path)) $entry.patchedSha256
    Assert-Hash (Join-Path $PSScriptRoot ('LegacyAdapters\' + $entry.path + '.original')) $entry.originalSha256
    $current = (Get-FileHash -LiteralPath (Join-Path $ide $entry.path)).Hash
    if ($current -notin @($entry.originalSha256, $entry.patchedSha256)) { throw ('Unrecognized installed adapter: ' + $entry.path) }
}
if (Test-Path -LiteralPath (Join-Path $ide $compatibilityName)) {
    $installedCompatibilityHash = (Get-FileHash -LiteralPath (Join-Path $ide $compatibilityName)).Hash
    if ($installedCompatibilityHash -notin (@($manifest.compatibilitySha256) + @($manifest.previousCompatibilitySha256))) { throw 'Unrecognized compatibility library revision.' }
}
if ($Mode -eq 'Check') { Write-Output 'Legacy adapter payload and installed versions verified.'; return }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator privileges are required.' }
if (Get-Process -Name devenv -ErrorAction SilentlyContinue) { throw 'Save and close Visual Studio first.' }
$rollback = @()
$compatibilityDestination = Join-Path $ide $compatibilityName
$compatibilityBackup = $null
if ($Mode -eq 'Apply' -and (Test-Path -LiteralPath $compatibilityDestination)) {
    $compatibilityBackup = Join-Path $ide ('CoreWF.BuiltinPatchBackup\Compatibility.previous.' + $installedCompatibilityHash + '.dll')
    if (-not (Test-Path -LiteralPath $compatibilityBackup)) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($compatibilityBackup)) -Force | Out-Null
        Copy-Item -LiteralPath $compatibilityDestination -Destination $compatibilityBackup
    }
    Assert-Hash $compatibilityBackup $installedCompatibilityHash
}
try {
    if ($Mode -eq 'Apply') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $compatibilityName) -Destination $ide -Force; Assert-Hash (Join-Path $ide $compatibilityName) $manifest.compatibilitySha256 }
    foreach ($entry in $manifest.adapters) {
        $destination = Join-Path $ide $entry.path
        $backup = Join-Path $ide ('CoreWF.BuiltinPatchBackup\' + $entry.path)
        if (-not (Test-Path -LiteralPath $backup)) {
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
            Assert-Hash $destination $entry.originalSha256
            Copy-Item -LiteralPath $destination -Destination $backup
        }
        Assert-Hash $backup $entry.originalSha256
        $beforeHash = (Get-FileHash -LiteralPath $destination).Hash
        $rollback += @{ destination = $destination; source = $(if ($beforeHash -eq $entry.originalSha256) { $backup } else { Join-Path $PSScriptRoot ('LegacyAdapters\' + $entry.path) }) }
        $source = if ($Mode -eq 'Apply') { Join-Path $PSScriptRoot ('LegacyAdapters\' + $entry.path) } else { $backup }
        Copy-Item -LiteralPath $source -Destination $destination -Force
        Assert-Hash $destination $(if ($Mode -eq 'Apply') { $entry.patchedSha256 } else { $entry.originalSha256 })
    }
} catch {
    foreach ($entry in $rollback) { Copy-Item -LiteralPath $entry.source -Destination $entry.destination -Force }
    if ($compatibilityBackup) { Copy-Item -LiteralPath $compatibilityBackup -Destination $compatibilityDestination -Force }
    throw
}
Write-Output ('Legacy WF adapter repair: ' + $Mode + ' completed. Visual Studio global bindings were preserved.')
