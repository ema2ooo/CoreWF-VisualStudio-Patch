function Resolve-VisualStudioIde([string]$Requested) {
    if ($Requested) { return (Resolve-Path -LiteralPath $Requested).Path }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        foreach ($root in @(& $vswhere -products '*' -prerelease -sort -property installationPath)) {
            $ide = Join-Path $root 'Common7\IDE'
            if (Test-Path -LiteralPath (Join-Path $ide 'Microsoft.VisualStudio.Activities.dll')) { return $ide }
        }
    }
    throw 'A Visual Studio installation with Windows Workflow Foundation was not found. Supply -VisualStudioIdePath.'
}
