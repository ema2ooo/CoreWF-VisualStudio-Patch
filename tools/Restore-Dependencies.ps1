[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packages = Join-Path $repository '.packages'
$project = Join-Path $PSScriptRoot 'BinaryPatch\Dependencies\Dependencies.csproj'
& dotnet restore $project --packages $packages --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Mono.Cecil dependency restore failed.' }
$source = Join-Path $packages 'mono.cecil\0.11.6\lib\net40\Mono.Cecil.dll'
$identity = [Reflection.AssemblyName]::GetAssemblyName($source)
if ($identity.FullName -ne 'Mono.Cecil, Version=0.11.6.0, Culture=neutral, PublicKeyToken=50cebf1cceb9d05e') {
    throw 'Unexpected Mono.Cecil package assembly.'
}
Copy-Item -LiteralPath $source -Destination (Join-Path $PSScriptRoot 'BinaryPatch\Mono.Cecil.dll') -Force
$notice = Join-Path $packages 'mono.cecil\0.11.6\LICENSE.txt'
if (-not (Test-Path -LiteralPath $notice)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/jbevain/cecil/0.11.6/LICENSE.txt' -OutFile $notice
}
if ((Get-FileHash -LiteralPath $notice -Algorithm SHA256).Hash -ne '13D58ECBED676511C0C4C4009BFF107025E50007FF5E7901DC562700EC62509E') {
    throw 'Unexpected Mono.Cecil dependency notice.'
}
Write-Output 'Restored Mono.Cecil 0.11.6 from NuGet.'
