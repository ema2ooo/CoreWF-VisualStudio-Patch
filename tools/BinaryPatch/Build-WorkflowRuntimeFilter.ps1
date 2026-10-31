[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [Parameter(Mandatory)][string]$OriginalDirectory)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$inputs = [IO.Path]::GetFullPath($OriginalDirectory)
$templateNamespace = 'http://schemas.microsoft.com/developer/vstemplate/2005'
$manifestNamespace = 'http://schemas.microsoft.com/developer/vstemplatemanifest/2015'
$msbuildNamespace = 'http://schemas.microsoft.com/developer/msbuild/2003'
$files = [Collections.Generic.List[object]]::new()

function Read-Xml([string]$Path) {
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.PreserveWhitespace = $true
    $document.Load($Path)
    return ,$document
}
function Set-TemplateValue($Data, [string]$Name, [string]$Value) {
    $node = $Data[$Name]
    if (-not $node) { $node = $Data.OwnerDocument.CreateElement($Name, $templateNamespace); [void]$Data.AppendChild($node) }
    $node.InnerText = $Value
}
function Save-Change([string]$Relative, $Document) {
    $source = Join-Path $inputs $Relative
    $original = 'RuntimeFilter/originals/' + $Relative
    $patched = 'RuntimeFilter/patched/' + $Relative
    foreach ($path in @($original, $patched)) { New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName((Join-Path $output $path))) -Force | Out-Null }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $original)
    $Document.Save((Join-Path $output $patched))
    $files.Add([ordered]@{ target=$Relative; payload=$patched; original=$original; originalSha256=(Get-FileHash -LiteralPath $source).Hash })
}

$managed = 'Installation/MSBuild/Microsoft/VisualStudio/Managed/Microsoft.Managed.DesignTime.targets'
$targets = Read-Xml (Join-Path $inputs $managed)
if ($targets.DocumentElement.LocalName -ne 'Project' -or $targets.DocumentElement.NamespaceURI -ne $msbuildNamespace -or
    $targets.OuterXml.Contains('CoreWFModernRuntime')) { throw 'Unexpected managed design-time targets layout or an unrecognized existing runtime filter.' }
foreach ($runtime in @(@('CoreWFModernRuntime', "'`$(TargetFrameworkIdentifier)' == '.NETCoreApp' Or '`$(TargetFrameworkIdentifier)' == '.NETStandard'"),
                       @('CoreWFFrameworkRuntime', "'`$(TargetFrameworkIdentifier)' == '.NETFramework'"))) {
    $group = $targets.CreateElement('ItemGroup', $msbuildNamespace)
    $group.SetAttribute('Condition', $runtime[1])
    $capability = $targets.CreateElement('ProjectCapability', $msbuildNamespace)
    $capability.SetAttribute('Include', $runtime[0])
    [void]$group.AppendChild($capability)
    [void]$targets.DocumentElement.AppendChild($group)
}
Save-Change $managed $targets

$core = Read-Xml (Join-Path $output 'NewItemTemplates/CoreWF.BuiltinDesigner.Item.vstman')
$catalogs = @(Get-ChildItem -LiteralPath (Join-Path $inputs 'ItemTemplates') -Filter 'Microsoft.VisualStudio.WorkflowV2.Resources.*.Item..vstman' -File)
if ($catalogs.Count -eq 0) { throw 'No registered workflow item catalog was found.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $catalogs) {
    $catalog = Read-Xml $file.FullName
    $namespaces = [Xml.XmlNamespaceManager]::new($catalog.NameTable)
    $namespaces.AddNamespace('m', $manifestNamespace)
    foreach ($container in @($catalog.SelectNodes('/m:VSTemplateManifest/m:VSTemplateContainer', $namespaces))) {
        $data = $container['VSTemplateHeader']['TemplateData']
        if ($data.TemplateID -notmatch '^Microsoft\.(CSharp|VisualBasic)\.(Activity|ActivityDesigner|CodeActivity|WorkflowService)$') {
            throw ('Unrecognized stock workflow template: ' + $data.TemplateID)
        }
        # AppliesTo is an opt-in; disabling the generic fallback is essential.
        $language = if ($data.ProjectType -eq 'CSharp') { 'CSharp' } elseif ($data.ProjectType -eq 'VisualBasic') { '(VB | VisualBasic)' } else { throw 'Unexpected workflow template language.' }
        $expression = $language + ' + (!CPS | CoreWFFrameworkRuntime)'
        Set-TemplateValue $data 'ShowByDefault' 'false'
        Set-TemplateValue $data 'AppliesTo' $expression
        $relative = 'ItemTemplates/' + $container['RelativePathOnDisk'].InnerText.Replace('\','/') + '/' + $container['TemplateFileName'].InnerText
        if ($seen.Add($relative)) {
            $template = Read-Xml (Join-Path $inputs $relative)
            if ($template.VSTemplate.TemplateData.TemplateID -ne $data.TemplateID) { throw 'Workflow catalog and template identities disagree.' }
            Set-TemplateValue $template.VSTemplate.TemplateData 'ShowByDefault' 'false'
            Set-TemplateValue $template.VSTemplate.TemplateData 'AppliesTo' $expression
            Save-Change $relative $template
        }
    }
    # VS enumerates the catalogs registered with its installed components. Add
    # our headers to that existing catalog instead of relying on an unregistered
    # loose .vstman file being discovered.
    foreach ($container in $core.DocumentElement.ChildNodes) {
        if ($container.NodeType -eq [Xml.XmlNodeType]::Element) { [void]$catalog.DocumentElement.AppendChild($catalog.ImportNode($container, $true)) }
    }
    Save-Change ('ItemTemplates/' + $file.Name) $catalog
}
# Keep the previously installed owned catalog as an empty compatibility marker;
# the registered stock catalog above owns discovery, without duplicate entries.
foreach ($node in @($core.DocumentElement.ChildNodes)) { [void]$core.DocumentElement.RemoveChild($node) }
$core.Save((Join-Path $output 'NewItemTemplates/CoreWF.BuiltinDesigner.Item.vstman'))
$itemsPath = Join-Path $output 'item-template-manifest.json'
$items = Get-Content -LiteralPath $itemsPath -Raw | ConvertFrom-Json
($items.files | Where-Object path -eq 'CoreWF.BuiltinDesigner.Item.vstman').sha256 = (Get-FileHash -LiteralPath (Join-Path $output 'NewItemTemplates/CoreWF.BuiltinDesigner.Item.vstman')).Hash
$items.revision = 'corewf-runtime-filtered-items-2'
$items | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $itemsPath -Encoding UTF8
@{ files=@($files.ToArray()) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'workflow-runtime-filter.json') -Encoding UTF8
Write-Output ('Prepared runtime-filtered workflow catalogs and design-time capabilities (' + $files.Count + ' local files).')
