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
$taskCopilotMatch = [regex]::Match($taskSource, '(?s)return \$\$"""\r?\n(.*?)\r?\n\s*""";')
Assert-Optimization $taskCopilotMatch.Success 'Production Copilot script missing.'
$taskSid = 'S-1-5-21-111-222-333-1001'
$taskCopilot = $taskCopilotMatch.Groups[1].Value.Replace('{{normalizedSid}}', $taskSid)
foreach ($taskScript in @($taskRead, $taskEnhance, $taskCopilot)) {
    $taskTokens = $null; $taskErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseInput($taskScript, [ref]$taskTokens, [ref]$taskErrors)
    Assert-Optimization ($taskErrors.Count -eq 0) "Production script syntax failed: $taskErrors"
}

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

function Test-CopilotRemoval([bool]$KeepRegistration) {
    $fixture = @{
        Packages = @(
            [pscustomobject]@{ Name = 'Microsoft.Copilot'; PublisherId = '8wekyb3d8bbwe'; PackageFullName = 'target-package' },
            [pscustomobject]@{ Name = 'Microsoft.Copilot'; PublisherId = 'another-publisher'; PackageFullName = 'foreign-package' },
            [pscustomobject]@{ Name = 'Microsoft.MicrosoftOfficeHub'; PublisherId = '8wekyb3d8bbwe'; PackageFullName = 'office-package' }
        )
        Removed = @(); Failure = $null; Reads = 0
    }
    function Get-AppxPackage($User, $Name, $ErrorAction) {
        Assert-Optimization ($User -eq $taskSid -and $Name -eq 'Microsoft.Copilot') 'Wrong account/package query.'
        $fixture.Reads++
        $fixture.Packages
    }
    function Remove-AppxPackage($User, $Package, $ErrorAction) {
        Assert-Optimization ($User -eq $taskSid -and $Package -eq 'target-package') 'Wrong account/package removal.'
        $fixture.Removed += $Package
        if (-not $KeepRegistration) { $fixture.Packages = @($fixture.Packages | Where-Object { $_.PackageFullName -ne $Package }) }
    }
    try { & ([scriptblock]::Create($taskCopilot)) }
    catch { $fixture.Failure = $_.Exception.Message }
    Assert-Optimization ($fixture.Removed.Count -eq 1 -and $fixture.Removed[0] -eq 'target-package') 'Removal was not restricted to the standalone Microsoft app.'
    Assert-Optimization ($fixture.Reads -eq 2) 'Removal was not verified.'
    if ($KeepRegistration) {
        Assert-Optimization ($fixture.Failure -like '*still registered*') 'Failed removal was reported as success.'
    } else {
        Assert-Optimization ($null -eq $fixture.Failure) "Removal failed: $($fixture.Failure)"
    }
}
Test-CopilotRemoval $false
Test-CopilotRemoval $true
Write-Output 'PASS: script syntax, network reads, native/RSS failures, account-scoped Copilot removal and readback.'
