[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot ('artifacts\installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string] $VisualStudioIdePath
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new output directory.' }
& (Join-Path $PSScriptRoot 'tools\Restore-Dependencies.ps1')
. (Join-Path $PSScriptRoot 'tools\Find-VisualStudio.ps1')
$VisualStudioIdePath = Resolve-VisualStudioIde $VisualStudioIdePath
$signingKey = Join-Path $PSScriptRoot 'tools\BinaryPatch\Bridge.snk'
if (-not (Test-Path -LiteralPath $signingKey)) {
    $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(2048)
    $rsa.PersistKeyInCsp = $false
    try { [IO.File]::WriteAllBytes($signingKey, $rsa.ExportCspBlob($true)) }
    finally { $rsa.Dispose() }
}
$visualStudioRoot = [IO.Path]::GetFullPath((Join-Path $VisualStudioIdePath '..\..'))
& dotnet build (Join-Path $PSScriptRoot 'src\CoreWF.LegacyAdapters\CoreWF.LegacyAdapters.csproj') -c Release '-p:NuGetAudit=false' "-p:VisualStudioInstallRoot=$visualStudioRoot" -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Compatibility library build failed.' }
New-Item -ItemType Directory -Path $output | Out-Null
& (Join-Path $PSScriptRoot 'tools\BinaryPatch\Build-Setup.ps1') -OutputPath (Join-Path $output 'CoreWF-WorkflowDesigner-Setup.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '.packages\mono.cecil\0.11.6\LICENSE.txt') -Destination (Join-Path $output 'Mono.Cecil.LICENSE.txt')
Write-Output ('Installer: ' + (Join-Path $output 'CoreWF-WorkflowDesigner-Setup.exe'))
Write-Output 'No Microsoft binaries are packaged. Supported installed designer files are patched locally after consent.'
