[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$VisualStudioIdePath = 'C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\IDE',
    [string]$LegacyBundleDirectory,
    [string]$CompatibilityAssemblyPath,
    [string]$OriginalDirectory
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new output directory.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($CompatibilityAssemblyPath) {
    $compatibility = (Resolve-Path -LiteralPath $CompatibilityAssemblyPath).Path
} else {
    $signingKey = Join-Path $PSScriptRoot 'Bridge.snk'
    if (-not (Test-Path -LiteralPath $signingKey)) {
        $rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(2048)
        $rsa.PersistKeyInCsp = $false
        try { [IO.File]::WriteAllBytes($signingKey, $rsa.ExportCspBlob($true)) }
        finally { $rsa.Dispose() }
    }
    $visualStudioRoot = [IO.Path]::GetFullPath((Join-Path $VisualStudioIdePath '..\..'))
    $project = Join-Path $repository 'src\CoreWF.LegacyAdapters\CoreWF.LegacyAdapters.csproj'
    & dotnet build $project -c Release '-p:NuGetAudit=false' "-p:VisualStudioInstallRoot=$visualStudioRoot" -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'Compatibility library build failed.' }
    $compatibility = Join-Path $repository 'src\CoreWF.LegacyAdapters\bin\Release\net48\CoreWF.LegacyAdapters.dll'
}
Add-Type -Path (Join-Path $PSScriptRoot 'Mono.Cecil.dll')
. (Join-Path $PSScriptRoot 'Patch-Support.ps1')
$original = Get-DesignerInput $VisualStudioIdePath 'Microsoft.VisualStudio.Activities.dll' $OriginalDirectory
$originalHash = (Get-FileHash -LiteralPath $original).Hash
$bridge = [Mono.Cecil.ModuleDefinition]::ReadModule($compatibility)
$module = Open-OriginalDesigner $original 'Microsoft.VisualStudio.Activities'
$ideVersion = $module.Assembly.Name.Version.ToString()
if (Test-Path -LiteralPath (Join-Path $VisualStudioIdePath 'devenv.exe')) { $ideVersion = (Get-Item -LiteralPath (Join-Path $VisualStudioIdePath 'devenv.exe')).VersionInfo.FileVersion }
try {
    $entry = ($bridge.Types | Where-Object FullName -eq 'CoreWF.LegacyAdapters.ModernFrameworkCompatibility').Methods | Where-Object Name -eq 'NormalizeDesignerFramework'
    $pane = $module.Types | Where-Object FullName -eq 'Microsoft.VisualStudio.Activities.EditorPane'
    $method = $pane.Methods | Where-Object Name -eq 'GetTargetFramework'
    if (@($method).Count -ne 1 -or $method.Parameters.Count -ne 1 -or $method.Parameters[0].ParameterType.FullName -ne 'System.String&' -or $method.ReturnType.FullName -ne 'System.Int32') { throw 'Incompatible designer framework callback signature.' }
    $ret = $method.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret }
    if (@($ret).Count -ne 1) { throw 'Unexpected framework callback return.' }
    $il = $method.Body.GetILProcessor()
    $il.InsertBefore($ret, $il.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1))
    $il.InsertBefore($ret, $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$entry)))
    $method.Body.MaxStackSize = [Math]::Max(2, $method.Body.MaxStackSize)
    $helper = $module.Types | Where-Object FullName -eq 'Microsoft.VisualStudio.Activities.Helper'
    $createAssemblyName = $helper.Methods | Where-Object Name -eq 'CreateAssemblyNameFromReference'
    $cultureConstructors = @($createAssemblyName.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Newobj -and
        $_.Operand.DeclaringType.FullName -eq 'System.Globalization.CultureInfo' -and $_.Operand.Parameters.Count -eq 1
    })
    if ($cultureConstructors.Count -ne 1) { throw 'Unexpected reference culture construction.' }
    $cultureEntry = ($bridge.Types | Where-Object Name -eq 'ModernFrameworkCompatibility').Methods | Where-Object Name -eq 'CreateReferenceCulture'
    $cultureConstructors[0].OpCode = [Mono.Cecil.Cil.OpCodes]::Call
    $cultureConstructors[0].Operand = $module.ImportReference([Mono.Cecil.MethodDefinition]$cultureEntry)
    $partitionProbe = $helper.Methods | Where-Object Name -eq 'TryGetPartitionedBuildAssemblyPath'
    if (@($partitionProbe).Count -ne 1 -or $partitionProbe.Parameters.Count -ne 2 -or
        $partitionProbe.Parameters[1].ParameterType.FullName -ne 'System.String&') { throw 'Unexpected partitioned-build probe layout.' }
    $skipPartition = ($bridge.Types | Where-Object Name -eq 'ModernFrameworkCompatibility').Methods | Where-Object Name -eq 'ShouldSkipPartitionedBuildProbe'
    $partitionIl = $partitionProbe.Body.GetILProcessor()
    $partitionFirst = $partitionProbe.Body.Instructions[0]
    # SDK projects use the existing active configuration/output group fallback. Keep
    # the original partitioned workflow build evaluation for .NET Framework projects.
    foreach ($instruction in @(
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0),
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$skipPartition)),
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $partitionFirst),
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1),
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Ldnull),
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Stind_Ref),
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0),
        $partitionIl.Create([Mono.Cecil.Cil.OpCodes]::Ret)
    )) { $partitionIl.InsertBefore($partitionFirst, $instruction) }
    $localAssembly = $helper.Methods | Where-Object { $_.Name -eq 'GetLocalAssemblyName' -and $_.Parameters.Count -eq 3 }
    if (@($localAssembly).Count -ne 1) { throw 'Unexpected local assembly discovery method.' }
    $mapEntry = ($bridge.Types | Where-Object Name -eq 'BuiltActivityMetadata').Methods | Where-Object Name -eq 'MapBuiltAssembly'
    $localIl = $localAssembly.Body.GetILProcessor()
    foreach ($return in @($localAssembly.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret })) {
        $localIl.InsertBefore($return, $localIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$mapEntry)))
    }
    $parseTypes = ($module.GetTypes() | Where-Object Name -eq 'ToolBoxWorkItem').Methods | Where-Object Name -eq 'ParseTypesList'
    $catch = @($parseTypes.Body.ExceptionHandlers | Where-Object { $_.CatchType.FullName -eq 'System.Exception' })
    if ($catch.Count -ne 1 -or $catch[0].HandlerStart.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Pop) { throw 'Unexpected activity discovery error handler.' }
    $report = ($bridge.Types | Where-Object Name -eq 'BuiltActivityMetadata').Methods | Where-Object Name -eq 'ReportDiscoveryError'
    $catch[0].HandlerStart.OpCode = [Mono.Cecil.Cil.OpCodes]::Call
    $catch[0].HandlerStart.Operand = $module.ImportReference([Mono.Cecil.MethodDefinition]$report)
    $frameworkHelper = $helper.Methods | Where-Object { $_.Name -eq 'GetTargetFramework' -and $_.Parameters.Count -eq 3 }
    $frameworkIl = $frameworkHelper.Body.GetILProcessor()
    foreach ($return in @($frameworkHelper.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret })) {
        $frameworkIl.InsertBefore($return, $frameworkIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_2))
        $frameworkIl.InsertBefore($return, $frameworkIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$entry)))
    }
    $resolver = $helper.Methods | Where-Object { $_.Name -eq 'ResolveAssemblyPathInTargetFx' -and $_.Parameters.Count -eq 7 }
    $resolverEntry = ($bridge.Types | Where-Object Name -eq 'ModernFrameworkCompatibility').Methods | Where-Object Name -eq 'SelectDesignerResolver'
    $resolverIl = $resolver.Body.GetILProcessor()
    $resolverFirst = $resolver.Body.Instructions[0]
    $resolverIl.InsertBefore($resolverFirst, $resolverIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $resolverIl.InsertBefore($resolverFirst, $resolverIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$resolverEntry)))
    $resolverIl.InsertBefore($resolverFirst, $resolverIl.Create([Mono.Cecil.Cil.OpCodes]::Starg, $resolver.Parameters[0]))
    $toolData = $module.GetTypes() | Where-Object { $_.FullName -eq 'Microsoft.VisualStudio.Activities.WorkflowToolboxItemWrapper/ToolDataHelper' }
    $toolMethod = $toolData.Methods | Where-Object Name -eq 'AddMultiTargetingInfo'
    $toolIl = $toolMethod.Body.GetILProcessor()
    $toolFirst = $toolMethod.Body.Instructions[0]
    $fallback = @($toolMethod.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_ToolData' })[-1].Previous
    $assemblyGetter = ($module.Types | Where-Object Name -eq 'WorkflowToolboxItemWrapper').Methods | Where-Object Name -eq 'get_StrongAssemblyName'
    $standardEntry = ($bridge.Types | Where-Object Name -eq 'ModernFrameworkCompatibility').Methods | Where-Object Name -eq 'IsStandardWorkflowAssembly'
    if (-not $fallback -or $fallback.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldarg_0) { throw 'Unexpected toolbox fallback layout.' }
    $toolIl.InsertBefore($toolFirst, $toolIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $toolIl.InsertBefore($toolFirst, $toolIl.Create([Mono.Cecil.Cil.OpCodes]::Callvirt, [Mono.Cecil.MethodDefinition]$assemblyGetter))
    $toolIl.InsertBefore($toolFirst, $toolIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$standardEntry)))
    $toolIl.InsertBefore($toolFirst, $toolIl.Create([Mono.Cecil.Cil.OpCodes]::Brtrue, $fallback))
    # A document can be restored before Visual Studio supplies its frame service.
    $readOnly = $pane.Methods | Where-Object Name -eq 'SetReadOnly'
    $readOnlyIl = $readOnly.Body.GetILProcessor()
    # Bind the guard to the frame-service result and backup field, not byte offsets.
    $frameCalls = @($readOnly.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.GenericInstanceMethod] -and $_.Operand.Name -eq 'GetService' -and $_.Operand.GenericArguments[0].FullName -eq 'Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame' })
    $backups = @($readOnly.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stfld -and $_.Operand.Name -eq 'backupObsolete' })
    if ($frameCalls.Count -ne 1 -or $backups.Count -ne 1) { throw 'Incompatible frame-service or backup-state layout.' }
    $store = $frameCalls[0].Next
    while ($store.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Nop) { $store = $store.Next }
    $localIndex = switch ($store.OpCode.Code.ToString()) { 'Stloc_0' {0}; 'Stloc_1' {1}; 'Stloc_2' {2}; 'Stloc_3' {3}; 'Stloc' {$store.Operand.Index}; 'Stloc_S' {$store.Operand.Index}; default {throw 'Unexpected frame-service local.'} }
    $frameUse = $store.Next
    $backupValue = $backups[0].Previous
    while ($backupValue.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Nop) { $backupValue = $backupValue.Previous }
    $backupUpdate = $backupValue.Previous
    while ($backupUpdate.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Nop) { $backupUpdate = $backupUpdate.Previous }
    if ($backupUpdate.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldarg_0 -or $backupValue.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Ldc_I4_1) { throw 'Incompatible backup assignment.' }
    $readOnlyIl.InsertBefore($frameUse, $readOnlyIl.Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $readOnly.Body.Variables[$localIndex]))
    $readOnlyIl.InsertBefore($frameUse, $readOnlyIl.Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $backupUpdate))
    $references = $pane.Methods | Where-Object Name -eq 'get_ReferencedAssemblies'
    $referencesIl = $references.Body.GetILProcessor()
    $referencesFirst = $references.Body.Instructions[0]
    $referencesFallback = $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Pop)
    $referenceEntry = ($bridge.Types | Where-Object Name -eq 'ModernFrameworkCompatibility').Methods | Where-Object Name -eq 'GetModernDesignerReferences'
    $dtarField = $pane.Fields | Where-Object Name -eq 'dtar'
    # The stock build-completion handler consults this field before checking the local
    # assembly timestamp. Preserve it so a rebuilt metadata view reloads the add-in domain.
    $referenceField = $pane.Fields | Where-Object Name -eq 'referencedAssemblies'
    $referenceLocal = [Mono.Cecil.Cil.VariableDefinition]::new($referenceField.FieldType)
    $references.Body.Variables.Add($referenceLocal)
    foreach ($instruction in @(
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Ldfld, [Mono.Cecil.FieldDefinition]$dtarField),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$referenceEntry)),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Dup),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $referencesFallback),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Dup),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Stloc, $referenceLocal),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $referenceLocal),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Stfld, [Mono.Cecil.FieldDefinition]$referenceField),
        $referencesIl.Create([Mono.Cecil.Cil.OpCodes]::Ret), $referencesFallback
    )) { $referencesIl.InsertBefore($referencesFirst, $instruction) }
    $deferredType = $bridge.Types | Where-Object Name -eq 'DeferredDesignerLoad'
    $loadMethod = $pane.Methods | Where-Object { $_.Name.EndsWith('IPersistFileFormat.Load') }
    $loadIl = $loadMethod.Body.GetILProcessor()
    $loadFirst = $loadMethod.Body.Instructions[0]
    $deferEntry = $deferredType.Methods | Where-Object Name -eq 'TryDefer'
    foreach ($instruction in @(
        $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0), $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1),
        $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_2), $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_3),
        $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$deferEntry)),
        $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $loadFirst),
        $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0), $loadIl.Create([Mono.Cecil.Cil.OpCodes]::Ret)
    )) { $loadIl.InsertBefore($loadFirst, $instruction) }
    $saveMethod = $pane.Methods | Where-Object { $_.Name.EndsWith('IPersistFileFormat.Save') }
    $saveIl = $saveMethod.Body.GetILProcessor()
    $saveFirst = $saveMethod.Body.Instructions[0]
    $pendingEntry = $deferredType.Methods | Where-Object Name -eq 'IsPending'
    foreach ($instruction in @(
        $saveIl.Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0),
        $saveIl.Create([Mono.Cecil.Cil.OpCodes]::Call, $module.ImportReference([Mono.Cecil.MethodDefinition]$pendingEntry)),
        $saveIl.Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $saveFirst),
        $saveIl.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4, [int]-2147483638), $saveIl.Create([Mono.Cecil.Cil.OpCodes]::Ret)
    )) { $saveIl.InsertBefore($saveFirst, $instruction) }
    $module.Mvid = [Guid]::NewGuid()
    New-Item -ItemType Directory -Path $output | Out-Null
    $module.Write((Join-Path $output 'Microsoft.VisualStudio.Activities.dll'))
    Copy-Item -LiteralPath $original -Destination (Join-Path $output 'Microsoft.VisualStudio.Activities.original.dll')
} finally { $module.Dispose(); $bridge.Dispose() }
if ($LegacyBundleDirectory) {
    foreach ($name in @('LegacyAdapters','legacy-adapter-manifest.json')) { Copy-Item -LiteralPath (Join-Path $LegacyBundleDirectory $name) -Destination $output -Recurse }
    Copy-Item -LiteralPath $compatibility -Destination (Join-Path $output 'CoreWF.LegacyAdapters.dll')
} else {
    & (Join-Path $PSScriptRoot 'Build-LegacyAdapters.ps1') -OutputDirectory $output -VisualStudioIdePath $VisualStudioIdePath -CompatibilityAssemblyPath $compatibility -OriginalDirectory $OriginalDirectory
}
$legacyFile = Join-Path $output 'legacy-adapter-manifest.json'
$legacy = Get-Content -LiteralPath $legacyFile -Raw | ConvertFrom-Json
$oldCompatibilityHash = $legacy.compatibilitySha256
$legacy.compatibilitySha256 = (Get-FileHash -LiteralPath $compatibility).Hash
$legacy | Add-Member -NotePropertyName previousCompatibilitySha256 -NotePropertyValue (@($oldCompatibilityHash) + @($legacy.previousCompatibilitySha256) | Select-Object -Unique) -Force
$legacy | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $legacyFile -Encoding UTF8
foreach ($name in @('Install-LegacyAdapters.ps1','Install-StandardDesignerPatch.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output }
[pscustomobject]@{
    schemaVersion = 1; revision = 'standard-designer-1'; visualStudioVersion = $ideVersion
    originalSha256 = $originalHash; patchedSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'Microsoft.VisualStudio.Activities.dll')).Hash
    previousPatchedSha256 = @('70364703F4C093D395EE6DE2E5BDBAFEBB866B3941FCB2E4C4A726085299F3EC','7E209DE290B501EB4B686C82CCAC0F8644FE29C8120241D5F08F55DEC0154E7F','39BE0A177F58486CAB9953B67354FC0A8ADEEBD64FF5B756E355CA43AF204845','DC3CE81B0FC853E27CC3B5B65B877C6AFF72DFA191CC041610086170551D5709','37B8C0E0961F9D8A2C8D91D7FBC31850D64208E802F82569EE6CB985965D225D','E4106340730A4226D7448F32D32D0EEA7DD577C3ACEEFED14C3811502B4E7FAD','A2AD33BAE5DAA2289B4460F822BE16887B95602FF82A91902DCFCA722A63EEA7','796A78E492A044F29D9F61842B055B605DE388FAD907CA7FA71745B4C3841889','737930BD508DF8A083D2C5C09DAE7F48B7359FEC7F2DB0AFAC6DA9A58AD2DF90','6E4FBF95DBAC1B4F632E1E38198C8EF79EA119C4D8258D942A20945DEB341D3E','E7FF29D8030CDABA55D257C34A2EADC6AE652E9218DCB1DCEE17FCE8A2DA3A06')
    editorFactoryUnchanged = $true; modernWorkerUsed = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'standard-designer-manifest.json') -Encoding UTF8
& (Join-Path $PSScriptRoot 'Build-WorkflowTemplates.ps1') -OutputDirectory $output -VisualStudioIdePath $VisualStudioIdePath
& (Join-Path $PSScriptRoot 'Build-DynamicActivities.ps1') -OutputDirectory $output -VisualStudioIdePath $VisualStudioIdePath -OriginalDirectory $OriginalDirectory
Write-Output ('Prepared standard built-in workflow designer patch: ' + $output)
