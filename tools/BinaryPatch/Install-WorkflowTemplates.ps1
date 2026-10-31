[CmdletBinding()]
param(
    [ValidateSet('Check', 'Apply', 'Restore')][string]$Mode = 'Check',
    [string]$VisualStudioIdePath = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE',
    [string]$ResultPath,
    [switch]$SkipCatalogRefresh
)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'item-template-manifest.json') -Raw | ConvertFrom-Json
$sourceRoot = Join-Path $PSScriptRoot 'NewItemTemplates'
$targetRoot = Join-Path (Resolve-Path -LiteralPath $VisualStudioIdePath).Path 'ItemTemplates'

function Get-ContainedPath([string]$Root, [string]$Relative) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A template path falls outside its intended directory.'
    }
    return $resolved
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "The template file has unexpected contents: $Path"
    }
}

try {
    if ($manifest.schemaVersion -ne 1 -or @($manifest.files).Count -ne 7) { throw 'Unexpected item-template manifest.' }
    $present = 0
    foreach ($file in $manifest.files) {
        Assert-Hash (Get-ContainedPath $sourceRoot $file.path) $file.sha256
        $target = Get-ContainedPath $targetRoot $file.path
        if (Test-Path -LiteralPath $target) {
            Assert-Hash $target $file.sha256
            $present++
        }
    }
    if ($Mode -ne 'Check') {
        $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Installing the built-in item catalog requires an administrator PowerShell window.'
        }
        if ($Mode -eq 'Apply') {
            $created = [Collections.Generic.List[string]]::new()
            try {
                # Publish the catalog only after all of its template files are ready.
                foreach ($file in ($manifest.files | Sort-Object { $_.path.EndsWith('.vstman') })) {
                    $target = Get-ContainedPath $targetRoot $file.path
                    if (Test-Path -LiteralPath $target) { continue }
                    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
                    Copy-Item -LiteralPath (Get-ContainedPath $sourceRoot $file.path) -Destination $target
                    $created.Add($target)
                    Assert-Hash $target $file.sha256
                }
            } catch {
                foreach ($target in $created) { Remove-Item -LiteralPath $target -Force }
                throw
            }
            $present = @($manifest.files).Count
        } else {
            # Only remove our hash-verified files; leave stock templates and directories alone.
            foreach ($file in ($manifest.files | Sort-Object { -not $_.path.EndsWith('.vstman') })) {
                $target = Get-ContainedPath $targetRoot $file.path
                if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
            }
            $present = 0
        }
        if (-not $SkipCatalogRefresh) {
            $refresh = Start-Process -FilePath (Join-Path $VisualStudioIdePath 'devenv.com') -ArgumentList '/InstallVSTemplates' -WindowStyle Hidden -PassThru -Wait
            if ($refresh.ExitCode -ne 0) { throw ('Visual Studio item-catalog refresh failed: ' + $refresh.ExitCode) }
        }
    }
    $result = [pscustomobject]@{
        success = $true; mode = $Mode; revision = $manifest.revision
        installedFiles = $present; totalFiles = @($manifest.files).Count
        itemTemplatesDirectory = $targetRoot; checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        catalogRefreshRequested = ($Mode -ne 'Check' -and -not $SkipCatalogRefresh)
    }
    if ($ResultPath) { $result | ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding UTF8 }
    Write-Output ('CoreWF item templates: ' + $present + '/' + @($manifest.files).Count + ' files installed and hash-verified.')
    if ($Mode -eq 'Apply') { Write-Output 'Reopen Visual Studio to refresh Add New Item, then search for CoreWF.' }
} catch {
    if ($ResultPath) {
        [pscustomobject]@{ success = $false; mode = $Mode; error = $_.Exception.Message } |
            ConvertTo-Json | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    }
    throw
}
