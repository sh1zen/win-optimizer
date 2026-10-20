# Exercises production disk recovery and registry boundaries with synthetic data only.
$ErrorActionPreference='Stop'
$sourcePath=Join-Path $PSScriptRoot '../src/WinOptimizer.Infrastructure/Features/Management/Scripts/Startup.ps1'
$storageSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/WinOptimizer.Core/Features/Common/Constants/AppStorageScript.cs') -Raw
$helpers=[regex]::Match($storageSource,'(?s)public const string Helpers = """\r?\n(.*?)\r?\n""";').Groups[1].Value
if (-not $helpers) { throw 'The production storage helpers are missing.' }
. ([scriptblock]::Create($helpers))
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path $sourcePath),[ref]$tokens,[ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$testParent=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../build/autoruns-storage-tests'))
$testRoot=Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
# This is the sole filesystem boundary replacement; the production serialization and recovery functions run unchanged.
function Get-WinOptimizerDataRoot { $testRoot }
$passed=0
function Assert-Equal($expected,$actual) { if ($expected -cne $actual) { throw "Expected '$expected', received '$actual'." } }
function Assert-Fails([scriptblock]$action) {
    $failed=$false
    try { & $action } catch { $failed=$true }
    if (-not $failed) { throw 'Expected this operation to fail.' }
}
function New-RegistryBoundary {
    $key=[pscustomobject]@{Values=@{}; Kinds=@{}; FailSave=$false; FailDelete=$false; CorruptSave=$false; ReadCount=0; MutateOnRead=0}
    $key | Add-Member ScriptMethod GetValueNames { @($this.Values.Keys) }
    $key | Add-Member ScriptMethod GetValueKind {param($name) $this.Kinds[$name] }
    $key | Add-Member ScriptMethod GetValue {param($name,$fallback,$options)
        $this.ReadCount++
        if ($this.MutateOnRead -eq $this.ReadCount) { $this.Values[$name]='changed during preservation' }
        if ($this.Values.ContainsKey($name)) { $this.Values[$name] } else { $fallback }
    }
    $key | Add-Member ScriptMethod SetValue {param($name,$value,$kind)
        if ($this.FailSave) { throw 'Storage access denied' }
        $this.Values[$name]=if ($this.CorruptSave) { 'corrupt' } else { $value }; $this.Kinds[$name]=[string]$kind
    }
    $key | Add-Member ScriptMethod DeleteValue {param($name,$missingIsError)
        if ($this.FailDelete) { throw 'Delete access denied' }; $this.Values.Remove($name); $this.Kinds.Remove($name)
    }
    $key | Add-Member ScriptMethod Flush {}
    return $key
}
function New-Identity {
    [pscustomobject]@{Type='Registry'; Hive='CurrentUser'; View='Registry64'; Key='SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; Name='Example'; Kind='ExpandString'; Value='%ProgramFiles%\Example\App.exe --startup'; Stored=$false}
}
function New-Active($id) {
    $active=New-RegistryBoundary
    $active.Values[$id.Name]=$id.Value; $active.Kinds[$id.Name]=$id.Kind
    return $active
}
try {
    $id=New-Identity; $active=New-Active $id
    $path=Join-Path $testRoot 'normal.json'
    Save-DisabledRegistryValue $active $id $path
    $saved=Read-WinOptimizerJson $path
    Assert-Equal 0 $active.Values.Count; Assert-Equal $id.Value $saved.Value; Assert-Equal 'ExpandString' $saved.Kind; $passed++
    Restore-DisabledRegistryValue $active $id $path
    Assert-Equal $id.Value $active.Values['Example']; Assert-Equal 'ExpandString' $active.Kinds['Example']; Assert-Equal $false ([IO.File]::Exists($path)); $passed++

    foreach ($scenario in @('changedValue','changedType','existingBackup','saveDenied','deleteDenied','changedDuringSave')) {
        $id=New-Identity; $active=New-Active $id; $path=Join-Path $testRoot ($scenario + '.json')
        switch ($scenario) {
            'changedValue' { $active.Values['Example']='new data' }
            'changedType' { $active.Kinds['Example']='String' }
            'existingBackup' { Save-WinOptimizerJson $path @{Value='older data'} }
            'saveDenied' { $parent=Join-Path $testRoot 'blocked'; [IO.File]::WriteAllText($parent,'block'); $path=Join-Path $parent 'saved.json' }
            'deleteDenied' { $active.FailDelete=$true }
            'changedDuringSave' { $active.MutateOnRead=2 }
        }
        Assert-Fails { Save-DisabledRegistryValue $active $id $path }
        $expected=if ($scenario -eq 'changedValue') { 'new data' } elseif ($scenario -eq 'changedDuringSave') { 'changed during preservation' } else { $id.Value }
        Assert-Equal $expected $active.Values['Example']
        if ($scenario -eq 'existingBackup') { Assert-Equal 'older data' (Read-WinOptimizerJson $path).Value }
        if ($scenario -eq 'deleteDenied') {
            Assert-Equal $id.Value (Read-WinOptimizerJson $path).Value
            $active.FailDelete=$false
            Save-DisabledRegistryValue $active $id $path
            Assert-Equal 0 $active.Values.Count
        }
        $passed++
    }
    foreach ($scenario in @('saveDenied','corruptRestore','conflictingActive','corruptRecord')) {
        $id=New-Identity; $active=New-Active $id; $path=Join-Path $testRoot ('restore-' + $scenario + '.json')
        Save-DisabledRegistryValue $active $id $path
        switch ($scenario) {
            'saveDenied' { $active.FailSave=$true }
            'corruptRestore' { $active.CorruptSave=$true }
            'conflictingActive' { $active.Values['Example']='current data'; $active.Kinds['Example']='String' }
            'corruptRecord' { [IO.File]::WriteAllText($path,'{broken') }
        }
        Assert-Fails { Restore-DisabledRegistryValue $active $id $path }
        Assert-Equal $true ([IO.File]::Exists($path)); $passed++
    }
    foreach ($identity in @(
        @{Hive='Users'; View='Registry64'; Key='SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; Name='Example'; Kind='String'},
        @{Hive='CurrentUser'; View='Registry64'; Key='SYSTEM\CurrentControlSet'; Name='Example'; Kind='String'},
        @{Hive='CurrentUser'; View='Registry64'; Key='SOFTWARE\Microsoft\Windows\CurrentVersion\Run'; Name='Example'; Kind='Binary'})) {
        Assert-Fails { Assert-RegistryIdentity ([pscustomobject]$identity) }; $passed++
    }
    $userId=New-Identity; $machineId=New-Identity; $machineId.Hive='LocalMachine'
    if ((Get-RegistryStoragePath $userId) -eq (Get-RegistryStoragePath $machineId)) { throw 'Machine and user startup records share a path.' }; $passed++

    $pending=Join-Path $testRoot 'State\pending.json'
    Save-WinOptimizerJson $pending @{Version=1; Value=0}
    Save-WinOptimizerJson $pending @{Version=1; Value=1} -ReplaceExisting
    Assert-Equal 1 (Read-WinOptimizerCompletionMarker $pending).Value; $passed++
    $invalid=Join-Path $testRoot 'State\invalid.json'
    Save-WinOptimizerJson $invalid @{Other='data'}
    Assert-Fails { Read-WinOptimizerCompletionMarker $invalid }; $passed++
    Assert-Fails { Save-WinOptimizerJson (Join-Path $testRoot '..\outside.json') @{Value=1} }; $passed++
    Assert-Fails { Get-WinOptimizerUserDirectory '..\outside' }; $passed++

    $startup=Join-Path $testRoot 'synthetic-startup'; [void][IO.Directory]::CreateDirectory($startup)
    $file=Join-Path $startup 'Example.lnk'; [IO.File]::WriteAllText($file,'Synthetic startup bytes')
    $hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    $paths=@{Directory=(Join-Path $testRoot 'State\Autoruns\Startup'); Record=(Join-Path $testRoot 'State\Autoruns\Startup\example.json'); Payload=(Join-Path $testRoot 'State\Autoruns\Startup\example.payload.lnk')}
    $id=[pscustomobject]@{Folder=$startup; Name='Example.lnk'; Hash=$hash; Scope='Synthetic'}
    Save-DisabledStartupFile $id $paths
    Assert-Equal $false ([IO.File]::Exists($file)); Assert-Equal 'Synthetic startup bytes' ([IO.File]::ReadAllText($paths.Payload)); $passed++
    Restore-DisabledStartupFile $id $paths
    Assert-Equal $hash ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash); Assert-Equal $false ([IO.File]::Exists($paths.Record)); $passed++
    [IO.File]::WriteAllText($file,'Changed after scanning')
    Assert-Fails { Save-DisabledStartupFile $id $paths }
    Assert-Equal 'Changed after scanning' ([IO.File]::ReadAllText($file)); Assert-Equal $false ([IO.File]::Exists($paths.Record)); $passed++
    $id.Hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    Save-DisabledStartupFile $id $paths
    [IO.File]::WriteAllText($file,'New active file')
    Assert-Fails { Restore-DisabledStartupFile $id $paths }
    Assert-Equal 'New active file' ([IO.File]::ReadAllText($file)); Assert-Equal $true ([IO.File]::Exists($paths.Record)); $passed++
} finally {
    $absolute=[IO.Path]::GetFullPath($testRoot)
    if (-not $absolute.StartsWith($testParent.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($absolute) -notmatch '^[0-9a-f]{32}$') { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
Write-Output "PASS: $passed production storage checks; no real startup registration touched."
