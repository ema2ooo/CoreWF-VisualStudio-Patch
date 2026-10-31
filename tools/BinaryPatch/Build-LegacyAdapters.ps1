[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$VisualStudioIdePath = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE',
    [string]$CompatibilityAssemblyPath,
    [string]$OriginalDirectory
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$payload = Join-Path $output 'LegacyAdapters'
if (Test-Path -LiteralPath $payload) { throw 'The legacy-adapter output already exists.' }
Add-Type -Path (Join-Path $PSScriptRoot 'Mono.Cecil.dll')
. (Join-Path $PSScriptRoot 'Patch-Support.ps1')
$compatibility = if ($CompatibilityAssemblyPath) { (Resolve-Path -LiteralPath $CompatibilityAssemblyPath).Path } else { Join-Path $repository 'src\CoreWF.LegacyAdapters\bin\Release\net48\CoreWF.LegacyAdapters.dll' }
$bridgeModule = [Mono.Cecil.ModuleDefinition]::ReadModule($compatibility)
try {
    $entryType = $bridgeModule.Types | Where-Object FullName -eq 'CoreWF.LegacyAdapters.FrameworkElementAdapters'
    if (-not $entryType) { throw 'Compatibility adapter entry point not found.' }
    $targets = @(
        @{ path = 'HostSideAdapters\Microsoft.VisualStudio.Activities.HostAdapter.dll' },
        @{ path = 'AddinSideAdapters\Microsoft.VisualStudio.Activities.AddinAdapter.dll' }
    )
    $evidence = @()
    foreach ($target in $targets) {
        $inputPath = Get-DesignerInput $VisualStudioIdePath $target.path $OriginalDirectory
        $originalHash = (Get-FileHash -LiteralPath $inputPath).Hash
        $module = Open-OriginalDesigner $inputPath ([IO.Path]::GetFileNameWithoutExtension($inputPath))
        try {
            $count = 0
            foreach ($type in $module.GetTypes()) {
                foreach ($method in $type.Methods) {
                    if (-not $method.HasBody) { continue }
                    foreach ($instruction in $method.Body.Instructions) {
                        $reference = $instruction.Operand -as [Mono.Cecil.MethodReference]
                        if ($reference -and $reference.DeclaringType.FullName -eq 'System.AddIn.Pipeline.FrameworkElementAdapters') {
                            $replacement = $entryType.Methods | Where-Object Name -eq $reference.Name
                            if (@($replacement).Count -ne 1) { throw 'Unexpected legacy adapter method.' }
                            $instruction.Operand = $module.ImportReference([Mono.Cecil.MethodDefinition]$replacement)
                            $count++
                        }
                    }
                }
            }
            if ($count -ne 5) { throw ('Expected exactly five UI adapter calls, found ' + $count + ' in ' + $target.path) }
            $destination = Join-Path $payload $target.path
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            $module.Mvid = [Guid]::NewGuid()
            $module.Write($destination)
            $original = $destination + '.original'
            Copy-Item -LiteralPath $inputPath -Destination $original
            $evidence += [pscustomobject]@{ path = $target.path.Replace('\', '/'); originalSha256 = $originalHash; patchedSha256 = (Get-FileHash -LiteralPath $destination).Hash; changedCalls = $count }
        } finally { $module.Dispose() }
    }
    Copy-Item -LiteralPath $compatibility -Destination (Join-Path $output 'CoreWF.LegacyAdapters.dll')
    [pscustomobject]@{ schemaVersion = 1; revision = 'legacy-wpf10-1'; compatibilitySha256 = (Get-FileHash -LiteralPath $compatibility).Hash; adapters = $evidence } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'legacy-adapter-manifest.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-LegacyAdapters.ps1') -Destination $output -Force
    Write-Output 'Prepared two legacy WF pipeline adapters using public WPF APIs.'
} finally { $bridgeModule.Dispose() }
