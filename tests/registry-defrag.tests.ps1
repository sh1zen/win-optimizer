$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scripts = Join-Path $PSScriptRoot '..\src\WinOptimizer.Infrastructure\Features\Cleaner\Scripts'
. (Join-Path $scripts 'Invoke-RegistryDefrag.ps1')

# Compile the same native rename helper the scheduler embeds in WinPE.
$tokens = $null
$parseErrors = $null
$scheduler = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $scripts 'Schedule-RegistryDefrag.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw ($parseErrors -join [Environment]::NewLine) }
$helper = $scheduler.Find({
    param($node)
    $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
        $node.Value.Contains('public static class WinOptimizerRegistryDefrag')
}, $true)
Add-Type -TypeDefinition $helper.Value

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected '$Expected', got '$Actual'." }
}

# Fake the registry loader and exporter. Verify the file operations on real local NTFS fixtures.
function Invoke-RegistryTool([string[]]$Arguments) {
    switch ($Arguments[0]) {
        'load' {
            if ($Arguments[2] -notmatch '\\hive-\d{3}\\(working|export)\.hiv$') {
                throw "The registry loader received a live path: $($Arguments[2])"
            }
            Assert-CleanHive $Arguments[2]
        }
        'save' {
            if ($script:rejectExport) { throw 'Simulated export failure' }
            $bytes = [byte[]]::new(4096)
            [Text.Encoding]::ASCII.GetBytes('regf').CopyTo($bytes, 0)
            [IO.File]::WriteAllBytes($Arguments[2], $bytes)
        }
        'unload' { }
        default { throw "Unexpected registry operation: $($Arguments[0])" }
    }
}

function Get-FileHashValue([string]$Path) {
    if ($Path -eq $script:rejectInstalled -and (Get-Item -LiteralPath $Path).Length -eq 4096) {
        $script:rejectInstalled = $null
        throw 'Simulated verification failure after replacement'
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function New-FixtureHive([string]$Job, [string]$Name, [bool]$WithLog) {
    $path = Join-Path $Job $Name
    $bytes = [byte[]]::new(16384)
    [Text.Encoding]::ASCII.GetBytes('regf').CopyTo($bytes, 0)
    [IO.File]::WriteAllBytes($path, $bytes)
    if ($WithLog) { [IO.File]::WriteAllText($path + '.LOG1', 'original log') }
    [pscustomobject]@{ Path = $path; Job = $Job; RelativePath = $Name }
}

function Invoke-BootEntryRemoval([string]$System, [string]$Entry) {
    $script:bootEntries.Add($Entry)
    if ($Entry -eq $script:rejectBootEntry) { throw "Simulated BCDEdit failure for $Entry" }
}

$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\build'))
$fixture = Join-Path $testRoot ('registry-defrag-tests-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
try {
    $script:rejectExport = $false
    $script:rejectInstalled = $null
    $completedJob = Join-Path $fixture 'completed'
    [void][IO.Directory]::CreateDirectory($completedJob)
    Claim-MaintenanceJob $completedJob
    $replayed = $true
    try { Claim-MaintenanceJob $completedJob }
    catch [IO.IOException] { $replayed = $false }
    Assert-Equal $false $replayed 'A claimed job must never run twice.'
    $completed = New-FixtureHive $completedJob 'NTUSER.DAT' $false
    $originalHash = Get-FileHashValue $completed.Path
    $result = Invoke-HiveTransaction @($completed) { param($receipt) Write-Receipt $completedJob $receipt }
    Assert-Equal 'Completed' $result.State 'Compaction failed.'
    Assert-Equal 1 $result.Compacted 'Compaction count is wrong.'
    Assert-Equal 4096 (Get-Item -LiteralPath $completed.Path).Length 'The replacement was not installed.'
    Assert-Equal $originalHash (Get-FileHashValue (Join-Path $completedJob 'hive-000\original.hiv')) 'The verified original was lost.'
    Assert-Equal 'Completed' ((Get-Content -Raw -LiteralPath (Join-Path $completedJob 'result.json') | ConvertFrom-Json).State) 'The receipt is incorrect.'

    $plan = [pscustomobject]@{ Loader = 'loader'; Device = 'device' }
    foreach ($failedEntry in 'loader', 'device', '') {
        $script:bootEntries = [Collections.Generic.List[string]]::new()
        $script:rejectBootEntry = $failedEntry
        foreach ($name in 'boot.sdi', 'boot.wim') { [IO.File]::WriteAllText((Join-Path $completedJob $name), 'fixture') }
        Write-Receipt $completedJob $result -CleanupPending $true
        $pending = Get-Content -Raw -LiteralPath (Join-Path $completedJob 'result.json') | ConvertFrom-Json
        Assert-Equal $true $pending.CleanupPending 'The provisional receipt must report pending boot cleanup.'

        Complete-MaintenanceBoot 'fixture' $plan $completedJob $true $result

        $receipt = Get-Content -Raw -LiteralPath (Join-Path $completedJob 'result.json') | ConvertFrom-Json
        Assert-Equal 'Completed' $receipt.State 'Boot cleanup must preserve the compaction outcome.'
        Assert-Equal $false $receipt.CleanupPending 'The receipt must contain the final cleanup outcome.'
        Assert-Equal $(if ($failedEntry -eq 'loader') { 'loader' } else { 'loader,device' }) ($script:bootEntries -join ',') 'Incorrect boot-entry removal order.'
        Assert-Equal $(if ($failedEntry) { "Simulated BCDEdit failure for $failedEntry" } else { '' }) ($receipt.CleanupErrors -join ' ') 'A failed boot cleanup must be recorded.'
        foreach ($name in 'boot.sdi', 'boot.wim') {
            Assert-Equal ([bool]$failedEntry) ([IO.File]::Exists((Join-Path $completedJob $name))) 'Boot images must be retained until both entries are removed.'
        }
    }

    $failedJob = Join-Path $fixture 'failed'
    [void][IO.Directory]::CreateDirectory($failedJob)
    $failed = New-FixtureHive $failedJob 'SYSTEM' $true
    $script:rejectExport = $true
    $result = Invoke-HiveTransaction @($failed)
    Assert-Equal 'Failed' $result.State 'Export failure should block replacement.'
    Assert-Equal $originalHash (Get-FileHashValue $failed.Path) 'Export failure modified the original.'
    Assert-Equal 'original log' ([IO.File]::ReadAllText($failed.Path + '.LOG1')) 'Export failure modified the original log.'

    $rollbackJob = Join-Path $fixture 'restored'
    [void][IO.Directory]::CreateDirectory($rollbackJob)
    $first = New-FixtureHive $rollbackJob 'SOFTWARE' $true
    $second = New-FixtureHive $rollbackJob 'SYSTEM' $true
    $script:rejectExport = $false
    $script:rejectInstalled = $second.Path
    $result = Invoke-HiveTransaction @($first, $second)
    Assert-Equal 'Restored' $result.State 'Both attempted replacements should be restored.'
    Assert-Equal 0 $result.Compacted 'Rolled-back replacements were counted.'
    foreach ($hive in $first, $second) {
        Assert-Equal $originalHash (Get-FileHashValue $hive.Path) 'The original hive was not restored.'
        Assert-Equal 'original log' ([IO.File]::ReadAllText($hive.Path + '.LOG1')) 'The original log was not restored.'
    }
    Write-Output 'Registry defragmentation transaction checks passed.'
}
finally {
    $resolved = (Resolve-Path -LiteralPath $fixture).ProviderPath
    if ([IO.Path]::GetDirectoryName($resolved) -ne $testRoot -or
        [IO.Path]::GetFileName($resolved) -notmatch '^registry-defrag-tests-[a-f0-9]{32}$') { throw 'Invalid fixture path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
