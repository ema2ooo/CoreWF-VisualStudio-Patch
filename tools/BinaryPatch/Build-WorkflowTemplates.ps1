[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$VisualStudioIdePath = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE'
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$payload = Join-Path $output 'NewItemTemplates'
if (Test-Path -LiteralPath $payload) { throw 'The item-template output directory already exists.' }
$schema = [IO.Path]::GetFullPath((Join-Path $VisualStudioIdePath '..\..\Xml\Schemas\1033\vstemplate.xsd'))
$catalog = [Xml.XmlDocument]::new()
$manifestNamespace = 'http://schemas.microsoft.com/developer/vstemplatemanifest/2015'
$root = $catalog.CreateElement('VSTemplateManifest', $manifestNamespace)
$root.SetAttribute('Version', '1.0')
[void]$catalog.AppendChild($root)
foreach ($kind in @('Sequence', 'Flowchart', 'StateMachine')) {
    $source = Join-Path $PSScriptRoot ('Templates\' + $kind)
    [xml]$template = Get-Content -LiteralPath (Join-Path $source 'Workflow.vstemplate') -Raw
    [void]$template.Schemas.Add('http://schemas.microsoft.com/developer/vstemplate/2005', $schema)
    $template.Validate({ param($sender, $event) throw $event.Message })
    $relative = 'CSharp\Workflow\1033\CoreWF' + $kind
    $destination = Join-Path $payload $relative
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'Workflow.vstemplate'), (Join-Path $source 'Workflow.xaml') -Destination $destination
    $container = $catalog.CreateElement('VSTemplateContainer', $manifestNamespace)
    $container.SetAttribute('TemplateType', 'Item')
    $container.SetAttribute('Locale', '1033')
    foreach ($pair in @(@('RelativePathOnDisk', $relative), @('TemplateFileName', 'Workflow.vstemplate'))) {
        $element = $catalog.CreateElement($pair[0], $manifestNamespace)
        $element.InnerText = $pair[1]
        [void]$container.AppendChild($element)
    }
    $header = $catalog.CreateElement('VSTemplateHeader', $manifestNamespace)
    [void]$header.AppendChild($catalog.ImportNode($template.VSTemplate.TemplateData, $true))
    [void]$container.AppendChild($header)
    [void]$root.AppendChild($container)
}
$catalog.Save((Join-Path $payload 'CoreWF.BuiltinDesigner.Item.vstman'))
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
    [pscustomobject]@{
        path = $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
[pscustomobject]@{ schemaVersion = 1; revision = 'corewf-items-1'; files = $files } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'item-template-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-WorkflowTemplates.ps1') -Destination $output -Force
Write-Output ('Prepared 3 CoreWF item templates (' + $files.Count + ' files); Visual Studio schema validation passed.')
