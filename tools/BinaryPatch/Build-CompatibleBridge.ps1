[CmdletBinding()]
param([Parameter(Mandatory)][string]$VisualStudioIdePath, [Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'Mono.Cecil.dll')
$module = [Mono.Cecil.ModuleDefinition]::ReadModule($Source)
try {
    $changed = $false
    foreach ($reference in @($module.AssemblyReferences | Where-Object { $_.Name.StartsWith('Microsoft.VisualStudio.') })) {
        $name = $reference.Name + '.dll'
        $installed = @('PublicAssemblies','PrivateAssemblies','') | ForEach-Object { Join-Path (Join-Path $VisualStudioIdePath $_) $name } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $installed) { throw ('The installed designer API is missing: ' + $name) }
        $identity = [Reflection.AssemblyName]::GetAssemblyName($installed)
        if ([BitConverter]::ToString($identity.GetPublicKeyToken()) -ne [BitConverter]::ToString($reference.PublicKeyToken)) { throw ('Unexpected designer API identity: ' + $name) }
        if ($reference.Version -ne $identity.Version) {
            $reference.Version = $identity.Version
            $changed = $true
        }
    }
    if ($changed) {
        $module.Mvid = [Guid]::NewGuid()
        $module.Write($Destination)
    } else { Copy-Item -LiteralPath $Source -Destination $Destination }
} finally { $module.Dispose() }
Write-Output 'Compatibility library bound to the installed Visual Studio API versions.'
