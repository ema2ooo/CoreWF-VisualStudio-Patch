[CmdletBinding()]
param([Parameter(Mandatory)][string]$VisualStudioIdePath, [Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$OriginalDirectory)
$ErrorActionPreference = 'Stop'
$payload = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$compatibleBridge = $OutputDirectory + '.bridge.dll'
& (Join-Path $PSScriptRoot 'Build-CompatibleBridge.ps1') -VisualStudioIdePath $VisualStudioIdePath -Source (Join-Path $payload 'CoreWF.LegacyAdapters.dll') -Destination $compatibleBridge
& (Join-Path $PSScriptRoot 'Build-StandardDesignerPatch.ps1') -OutputDirectory $OutputDirectory -VisualStudioIdePath $VisualStudioIdePath -CompatibilityAssemblyPath $compatibleBridge -OriginalDirectory $OriginalDirectory
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
& (Join-Path $PSScriptRoot 'Build-WorkflowRuntimeFilter.ps1') -OutputDirectory $output -OriginalDirectory $OriginalDirectory
$files = [Collections.Generic.List[object]]::new()
function Add-LocalFile([string]$Target,[string]$Source,[string]$Original,[string]$OriginalHash) {
    $backup = if ($Original) { 'CoreWF.BuiltinPatchBackup/originals/' + $OriginalHash + '/' + [IO.Path]::GetFileName($Target) } else { $null }
    $files.Add([ordered]@{ Target=$Target; Payload=$Source; Sha256=(Get-FileHash -LiteralPath (Join-Path $output $Source)).Hash; Original=$Original; OriginalSha256=$OriginalHash; Backup=$backup })
}
$standard = Get-Content -LiteralPath (Join-Path $output 'standard-designer-manifest.json') -Raw | ConvertFrom-Json
$legacy = Get-Content -LiteralPath (Join-Path $output 'legacy-adapter-manifest.json') -Raw | ConvertFrom-Json
$dynamic = Get-Content -LiteralPath (Join-Path $output 'dynamic-activities-manifest.json') -Raw | ConvertFrom-Json
$templates = Get-Content -LiteralPath (Join-Path $output 'item-template-manifest.json') -Raw | ConvertFrom-Json
Add-LocalFile 'CoreWF.LegacyAdapters.dll' 'CoreWF.LegacyAdapters.dll' $null $null
Add-LocalFile 'Mono.Cecil.dll' 'Mono.Cecil.dll' $null $null
foreach ($entry in $legacy.adapters) { Add-LocalFile $entry.path ('LegacyAdapters/'+$entry.path) ('LegacyAdapters/'+$entry.path+'.original') $entry.originalSha256 }
Add-LocalFile $dynamic.addinPath ('DynamicActivities/'+$dynamic.addinPath) ('DynamicActivities/'+$dynamic.addinPath+'.original') $dynamic.originalSha256
Add-LocalFile 'Microsoft.VisualStudio.Activities.dll' 'Microsoft.VisualStudio.Activities.dll' 'Microsoft.VisualStudio.Activities.original.dll' $standard.originalSha256
foreach ($entry in $templates.files | Sort-Object { $_.path.EndsWith('.vstman') }) { Add-LocalFile ('ItemTemplates/'+$entry.path) ('NewItemTemplates/'+$entry.path) $null $null }
$filter = Get-Content -LiteralPath (Join-Path $output 'workflow-runtime-filter.json') -Raw | ConvertFrom-Json
foreach ($entry in $filter.files) { Add-LocalFile $entry.target $entry.payload $entry.original $entry.originalSha256 }
foreach ($notice in @(@('LICENSE','CoreWF.DesignerPatch.LICENSE.txt'),@('THIRD-PARTY-NOTICES.md','CoreWF.DesignerPatch.NOTICES.md'))) {
    Copy-Item -LiteralPath (Join-Path $payload $notice[0]) -Destination (Join-Path $output $notice[1])
    Add-LocalFile $notice[1] $notice[1] $null $null
}
[ordered]@{ VisualStudioVersion=$standard.visualStudioVersion; Files=@($files.ToArray()) } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'install-plan.json') -Encoding UTF8
Write-Output 'Local patch prepared from verified files on this computer. The Visual Studio installation has not yet been modified.'
