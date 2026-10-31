function Open-OriginalDesigner([string]$Path, [string]$Name) {
    $module = [Mono.Cecil.ModuleDefinition]::ReadModule($Path)
    $token = [BitConverter]::ToString($module.Assembly.Name.PublicKeyToken).Replace('-','')
    if ($module.Assembly.Name.Name -ine $Name -or $token -ne '31BF3856AD364E35' -or
        @($module.AssemblyReferences | Where-Object Name -eq 'CoreWF.LegacyAdapters').Count) {
        $module.Dispose()
        throw ('Expected an unpatched Microsoft Workflow Designer component: ' + $Path)
    }
    return $module
}

function Get-DesignerInput([string]$Ide, [string]$Relative, [string]$OriginalDirectory) {
    if ($OriginalDirectory) { return Join-Path $OriginalDirectory $Relative }
    return Join-Path $Ide $Relative
}
