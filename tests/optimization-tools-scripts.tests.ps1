# Validates production scripts in Windows PowerShell with fake native boundaries.
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskSource = Get-Content -LiteralPath (Join-Path $taskRepository 'src/WinOptimizer.Infrastructure/Features/Optimize/Services/OptimizationToolsService.cs') -Raw

function Assert-Optimization($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-ProductionScript([string]$Name) {
    $pattern = '(?s)internal const string ' + $Name + ' = """\r?\n(.*?)\r?\n\s*""";'
    $match = [regex]::Match($taskSource, $pattern)
    Assert-Optimization $match.Success "Production script missing: $Name"
    return $match.Groups[1].Value
}

$taskRead = Get-ProductionScript 'ReadNetworkScript'
$taskEnhance = Get-ProductionScript 'EnhanceNetworkScript'
& {
    function Get-NetTCPSetting($SettingName, $ErrorAction) {
        Assert-Optimization ($SettingName -eq 'Internet') 'Unexpected TCP template.'
        [pscustomobject]@{ AutoTuningLevelLocal = 'Restricted' }
    }
    function Get-NetOffloadGlobalSetting($ErrorAction) { [pscustomobject]@{ ReceiveSideScaling = 'Enabled' } }
    $state = (& ([scriptblock]::Create($taskRead))) | ConvertFrom-Json
    Assert-Optimization ($state.AutoTuningLevel -eq 'Restricted' -and $state.ReceiveSideScaling -eq 'Enabled') 'Network readback was not preserved.'
}

function Test-NetworkApply([int]$ExitCode, [bool]$RssFails) {
    $fixture = @{ RssCalls = 0; NetshArguments = ''; Failure = $null }
    function Join-Path($Path, $ChildPath) {
        Assert-Optimization ($ChildPath -eq 'System32\netsh.exe') 'Unexpected executable.'
        'Invoke-TestNetsh'
    }
    function Invoke-TestNetsh {
        $fixture.NetshArguments = $args -join ' '
        $global:LASTEXITCODE = $ExitCode
    }
    function Set-NetOffloadGlobalSetting($ReceiveSideScaling, $ErrorAction) {
        $fixture.RssCalls++
        Assert-Optimization ($ReceiveSideScaling -eq 'Enabled') 'Unexpected RSS state.'
        if ($RssFails) { throw 'Synthetic RSS failure' }
    }
    try { & ([scriptblock]::Create($taskEnhance)) }
    catch { $fixture.Failure = $_.Exception.Message }
    Assert-Optimization ($fixture.NetshArguments -eq 'interface tcp set global autotuninglevel=normal') 'Unexpected network command.'
    if ($ExitCode -ne 0) {
        Assert-Optimization ($fixture.Failure -like '*netsh exit*' -and $fixture.RssCalls -eq 0) 'Native failure did not stop RSS changes.'
    } elseif ($RssFails) {
        Assert-Optimization ($fixture.Failure -like '*RSS failure*') 'RSS error was concealed.'
    } else {
        Assert-Optimization ($null -eq $fixture.Failure -and $fixture.RssCalls -eq 1) 'Network enhancement failed.'
    }
}
Test-NetworkApply 0 $false
Test-NetworkApply 7 $false
Test-NetworkApply 0 $true

Write-Output 'PASS: network reads, native/RSS failures and RSS application.'
