[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$VisualStudioIdePath, [string]$OriginalDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'Mono.Cecil.dll')
$relative = 'Addins/WorkflowDesigner/Microsoft.VisualStudio.Activities.Addin.dll'
. (Join-Path $PSScriptRoot 'Patch-Support.ps1')
$original = Get-DesignerInput $VisualStudioIdePath $relative $OriginalDirectory
$originalHash = (Get-FileHash -LiteralPath $original).Hash
$bridge = [Mono.Cecil.ModuleDefinition]::ReadModule((Join-Path $OutputDirectory 'CoreWF.LegacyAdapters.dll'))
$module = Open-OriginalDesigner $original 'Microsoft.VisualStudio.Activities.AddIn'
try {
    $load = ($module.Types | Where-Object Name -eq 'WorkflowDesignerAddIn').Methods | Where-Object { $_.Name -eq 'Load' -and $_.Parameters.Count -eq 1 }
    $calls = @($load.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.DeclaringType.FullName -eq 'System.Activities.Presentation.WorkflowDesigner' -and $_.Operand.Name -eq 'Load' })
    if ($calls.Count -ne 1) { throw 'Unexpected workflow add-in load method.' }
    $entry = ($bridge.Types | Where-Object Name -eq 'CompatibleWorkflowDocument').Methods | Where-Object Name -eq 'Load'
    $calls[0].OpCode = [Mono.Cecil.Cil.OpCodes]::Call
    $calls[0].Operand = $module.ImportReference([Mono.Cecil.MethodDefinition]$entry)
    $destination = Join-Path $OutputDirectory ('DynamicActivities\' + $relative)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    $module.Mvid = [Guid]::NewGuid()
    $module.Write($destination)
    Copy-Item -LiteralPath $original -Destination ($destination + '.original')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Mono.Cecil.dll') -Destination $OutputDirectory
    [pscustomobject]@{
        schemaVersion = 1
        addinPath = $relative; originalSha256 = $originalHash; patchedSha256 = (Get-FileHash -LiteralPath $destination).Hash
        previousPatchedSha256 = @('41FD1E20E64DA7BE8421B0A72C095FBCE64AACBC494CE2AA3E4EEBA47CE0D261','5356E42FDA67F2ADBF7D19E6461E433981AB8D900D40882A2A7DA8FAA864BC58','E4350527B05F8F80441F007404D60C169E254B51B9BD428097A682265A2892E8','2F50EA4DF09E17BD2C0A57B481B8B76F3A168B0A3E24AF45E6D834ED00099AA0','1EBE9EC38CD79BFB678D5135E7DCA1E89932DF292EE853506B1EF34F848027DB','B6470FE958024B6B055F03D402FFD1F09F0B15EDF6A39E99B7DE2140E9655CCE')
        metadataLibrarySha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'Mono.Cecil.dll')).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dynamic-activities-manifest.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-DynamicActivities.ps1') -Destination $OutputDirectory
} finally { $module.Dispose(); $bridge.Dispose() }
