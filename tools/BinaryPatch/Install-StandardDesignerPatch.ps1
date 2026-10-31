[CmdletBinding()]
param(
    [ValidateSet('Check','Apply','Restore')][string]$Mode = 'Check',
    [string]$VisualStudioIdePath = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE',
    [switch]$SkipCatalogRefresh
)
$ErrorActionPreference = 'Stop'
$ide = (Resolve-Path -LiteralPath $VisualStudioIdePath).Path
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'standard-designer-manifest.json') -Raw | ConvertFrom-Json
function Assert-Hash([string]$Path, [string]$Hash) {
    if ((Get-FileHash -LiteralPath $Path).Hash -ne $Hash) { throw ('Unexpected file revision: ' + $Path) }
}
$target = Join-Path $ide 'Microsoft.VisualStudio.Activities.dll'
$patched = Join-Path $PSScriptRoot 'Microsoft.VisualStudio.Activities.dll'
$original = Join-Path $PSScriptRoot 'Microsoft.VisualStudio.Activities.original.dll'
Assert-Hash $patched $manifest.patchedSha256
Assert-Hash $original $manifest.originalSha256
$current = (Get-FileHash -LiteralPath $target).Hash
if ($current -notin (@($manifest.originalSha256,$manifest.patchedSha256) + @($manifest.previousPatchedSha256))) { throw 'Unsupported installed designer revision.' }
& (Join-Path $PSScriptRoot 'Install-LegacyAdapters.ps1') -Mode Check -VisualStudioIdePath $ide
& (Join-Path $PSScriptRoot 'Install-WorkflowTemplates.ps1') -Mode Check -VisualStudioIdePath $ide
& (Join-Path $PSScriptRoot 'Install-DynamicActivities.ps1') -Mode Check -VisualStudioIdePath $ide
if ($Mode -eq 'Check') { Write-Output ('Standard designer patch installed: ' + ($current -eq $manifest.patchedSha256)); return }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator privileges are required.' }
if (Get-Process -Name devenv -ErrorAction SilentlyContinue) { throw 'Save and close Visual Studio first.' }
$backupDirectory = Join-Path $ide 'CoreWF.BuiltinPatchBackup'
$backup = Join-Path $backupDirectory 'Microsoft.VisualStudio.Activities.dll'
if (Test-Path -LiteralPath $backup) { Assert-Hash $backup $manifest.originalSha256 }
else {
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Copy-Item -LiteralPath $original -Destination $backup
    Assert-Hash $backup $manifest.originalSha256
}
$rollback = Join-Path $backupDirectory ('Activities.previous.' + $current.Substring(0,16) + '.dll')
if (Test-Path -LiteralPath $rollback) { Assert-Hash $rollback $current }
else { Copy-Item -LiteralPath $target -Destination $rollback }
if ($Mode -eq 'Apply') {
    & (Join-Path $PSScriptRoot 'Install-LegacyAdapters.ps1') -Mode Apply -VisualStudioIdePath $ide
    try { Copy-Item -LiteralPath $patched -Destination $target -Force; Assert-Hash $target $manifest.patchedSha256 }
    catch { Copy-Item -LiteralPath $rollback -Destination $target -Force; throw }
} else {
    Copy-Item -LiteralPath $backup -Destination $target -Force
    Assert-Hash $target $manifest.originalSha256
    & (Join-Path $PSScriptRoot 'Install-LegacyAdapters.ps1') -Mode Restore -VisualStudioIdePath $ide
}
& (Join-Path $PSScriptRoot 'Install-WorkflowTemplates.ps1') -Mode $Mode -VisualStudioIdePath $ide -SkipCatalogRefresh:$SkipCatalogRefresh
& (Join-Path $PSScriptRoot 'Install-DynamicActivities.ps1') -Mode $Mode -VisualStudioIdePath $ide
Write-Output ('Standard built-in designer patch: ' + $Mode + ' completed. Reopen Visual Studio.')
