[CmdletBinding()]
param(
    [ValidateSet('Apply', 'Check', 'Restore')]
    [string] $Mode = 'Check',
    [string] $VisualStudioRoot = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise',
    [string] $BackupDirectory = (Join-Path $PSScriptRoot 'backup')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$relativeTarget = 'Common7\ServiceHub\Hosts\ServiceHub.Host.Extensibility.amd64\net10.0-windows10.0.19041.0\DevHub.runtimeconfig.json'
$target = [IO.Path]::GetFullPath((Join-Path $VisualStudioRoot $relativeTarget))
$backup = Join-Path $BackupDirectory 'DevHub.runtimeconfig.original.json'
$statePath = Join-Path $BackupDirectory 'state.json'
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-BytesHash([byte[]] $Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Write-Atomic([byte[]] $Bytes) {
    $temporary = $target + '.devhub-fix-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllBytes($temporary, $Bytes)
        [IO.File]::Replace($temporary, $target, [System.Management.Automation.Language.NullString]::Value)
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

$currentBytes = [IO.File]::ReadAllBytes($target)
$currentHash = Get-BytesHash $currentBytes
$text = [IO.File]::ReadAllText($target)
$config = $text | ConvertFrom-Json
if ($config.runtimeOptions.tfm -ne 'net10.0') { throw 'Unexpected DevHub target framework; no changes made.' }
$properties = $config.runtimeOptions.configProperties
$setting = $properties.PSObject.Properties['System.Runtime.TieredCompilation']
$disabled = $null -ne $setting -and $setting.Value -is [bool] -and $setting.Value -eq $false

if ($Mode -eq 'Check') {
    [pscustomobject]@{ Target = $target; TieredCompilationDisabled = $disabled; Sha256 = $currentHash }
    return
}

if ($Mode -eq 'Restore') {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.Target -ne $target) { throw 'Backup belongs to a different Visual Studio installation.' }
    $originalBytes = [IO.File]::ReadAllBytes($backup)
    if ((Get-BytesHash $originalBytes) -ne $state.OriginalSha256) { throw 'Original backup hash does not match.' }
    if ($currentHash -eq $state.OriginalSha256) { Write-Output 'Original DevHub configuration is already restored.'; return }
    if ($currentHash -ne $state.PatchedSha256) { throw 'DevHub configuration changed since this fix; refusing to overwrite it.' }
    Write-Atomic $originalBytes
    Write-Output 'Restored the exact original DevHub configuration. Restart Visual Studio to use it.'
    return
}

if ($disabled) { Write-Output 'DevHub tiered compilation is already disabled; no changes made.'; return }
if ($null -ne $setting) { throw 'DevHub already defines tiered compilation; inspect the existing setting before changing it.' }
$marker = '(?m)^([ \t]*)"System.Runtime.TieredPGO"'
if ([regex]::Matches($text, $marker).Count -ne 1) { throw 'Unexpected DevHub configuration layout; no changes made.' }
$lineEnding = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
$updated = [regex]::Replace($text, $marker, {
    param($match)
    $match.Groups[1].Value + '"System.Runtime.TieredCompilation": false,' + $lineEnding + $match.Value
})
$updatedConfig = $updated | ConvertFrom-Json
$updatedConfig.runtimeOptions.configProperties.PSObject.Properties.Remove('System.Runtime.TieredCompilation')
if (($updatedConfig | ConvertTo-Json -Depth 20 -Compress) -ne ($config | ConvertTo-Json -Depth 20 -Compress)) {
    throw 'Configuration validation failed: unrelated settings changed.'
}
$updatedBytes = $utf8.GetBytes($updated)
New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
if (Test-Path -LiteralPath $statePath) {
    $existing = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($existing.Target -ne $target -or $existing.OriginalSha256 -ne $currentHash) {
        throw 'A backup for another configuration already exists; refusing to overwrite it.'
    }
}
if (Test-Path -LiteralPath $backup) {
    if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $currentHash) { throw 'An unrelated backup already exists.' }
} else {
    [IO.File]::WriteAllBytes($backup, $currentBytes)
}
[ordered]@{
    Target = $target
    OriginalSha256 = $currentHash
    PatchedSha256 = Get-BytesHash $updatedBytes
    Created = [DateTimeOffset]::Now.ToString('o')
    Change = 'System.Runtime.TieredCompilation=false for DevHub only'
} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
Write-Atomic $updatedBytes
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-BytesHash $updatedBytes)) { throw 'Installed configuration verification failed.' }
Write-Output 'Applied the DevHub-only tiered compilation workaround. Original configuration is backed up. Restart Visual Studio to use it.'
