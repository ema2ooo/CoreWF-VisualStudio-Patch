[CmdletBinding()]
param([ValidateSet('Check','Apply','Restore')][string]$Mode = 'Check', [Parameter(Mandatory)][string]$VisualStudioIdePath)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dynamic-activities-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.addinPath -ne 'Addins/WorkflowDesigner/Microsoft.VisualStudio.Activities.Addin.dll') { throw 'Unexpected add-in path.' }
function Assert-Hash([string]$Path, [string]$Hash) { if ((Get-FileHash -LiteralPath $Path).Hash -ne $Hash) { throw ('Unexpected file revision: ' + $Path) } }
$destination = Join-Path $VisualStudioIdePath $manifest.addinPath
$patched = Join-Path $PSScriptRoot ('DynamicActivities\' + $manifest.addinPath)
$original = $patched + '.original'
Assert-Hash $patched $manifest.patchedSha256
Assert-Hash $original $manifest.originalSha256
$current = (Get-FileHash -LiteralPath $destination).Hash
if ($current -notin (@($manifest.originalSha256,$manifest.patchedSha256) + @($manifest.previousPatchedSha256))) { throw 'Unsupported installed workflow add-in.' }
$library = Join-Path $VisualStudioIdePath 'Mono.Cecil.dll'
Assert-Hash (Join-Path $PSScriptRoot 'Mono.Cecil.dll') $manifest.metadataLibrarySha256
if (Test-Path -LiteralPath $library) { Assert-Hash $library $manifest.metadataLibrarySha256 }
if ($Mode -eq 'Check') { Write-Output 'Dynamic activity metadata payload and installed add-in verified.'; return }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator privileges are required.' }
if (Get-Process -Name devenv -ErrorAction SilentlyContinue) { throw 'Save and close Visual Studio first.' }
$backup = Join-Path $VisualStudioIdePath ('CoreWF.BuiltinPatchBackup\' + $manifest.addinPath)
if (-not (Test-Path -LiteralPath $backup)) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force | Out-Null
    Copy-Item -LiteralPath $original -Destination $backup
}
Assert-Hash $backup $manifest.originalSha256
try {
    $rollback = Join-Path $VisualStudioIdePath ('CoreWF.BuiltinPatchBackup\Addin.previous.' + $current + '.dll')
    if (-not (Test-Path -LiteralPath $rollback)) { Copy-Item -LiteralPath $destination -Destination $rollback }
    Assert-Hash $rollback $current
    if ($Mode -eq 'Apply') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Mono.Cecil.dll') -Destination $library -Force }
    Copy-Item -LiteralPath $(if ($Mode -eq 'Apply') { $patched } else { $backup }) -Destination $destination -Force
    Assert-Hash $destination $(if ($Mode -eq 'Apply') { $manifest.patchedSha256 } else { $manifest.originalSha256 })
} catch {
    if ($rollback) { Copy-Item -LiteralPath $rollback -Destination $destination -Force }
    throw
}
