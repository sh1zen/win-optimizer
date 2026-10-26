# Runs domain/integration suites and builds UI tests, checking native exits and TRX results.
# Commands, options and focused suites: tests/README.md.
param (
    [switch]$SkipUITests = $false,
    [switch]$IntegrationOnly = $false,
    [switch]$UseExistingReleaseBuild,
    [string]$MSBuildPath
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$solutionDir = Resolve-Path "$PSScriptRoot\.."
$resultsRoot = Join-Path $solutionDir ('build\test-results\' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $resultsRoot -Force

# Track results
$totalPassed = 0
$totalFailed = 0
$totalSkipped = 0
$failedProjects = @()

function Run-TestProject {
    param (
        [string]$Name,
        [string]$ProjectPath
    )

    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor DarkGray
    Write-Host "  Running $Name..." -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor DarkGray

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $trxFile = Join-Path $resultsRoot "$projectName.trx"
    $testArguments = @('test', $ProjectPath, '--verbosity', 'quiet',
        '--logger', "trx;LogFileName=$projectName.trx", '--results-directory', $resultsRoot)
    $originalErrorAction = $ErrorActionPreference
    try {
        # In PowerShell 5.1, capture native stderr before checking the native exit code.
        $ErrorActionPreference = 'Continue'
        $output = & dotnet @testArguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $originalErrorAction }
    if ($output.Trim()) { Write-Host $output -ForegroundColor DarkGray }

    # TRX results are independent of the SDK's console language.
    $passed = 0; $failed = 0; $skipped = 0
    $failureReason = $null
    if (Test-Path -LiteralPath $trxFile -PathType Leaf) {
        try {
            $result = [xml](Get-Content -LiteralPath $trxFile -Raw)
            $counters = $result.TestRun.ResultSummary.Counters
            if (-not $counters -or [int]$counters.total -le 0) {
                throw 'No test results were recorded.'
            }
            $passed = [int]$counters.passed
            $failed = [int]$counters.failed
            # Quiet console output can omit the assertion or timeout that failed CI.
            foreach ($testResult in $result.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed') {
                Write-Host "  $($testResult.testName)" -ForegroundColor Red
                Write-Host $testResult.Output.ErrorInfo.Message -ForegroundColor Red
                Write-Host $testResult.Output.ErrorInfo.StackTrace -ForegroundColor DarkGray
            }
            # xUnit can omit skipped tests from the summary counters.
            $skipped = @($result.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'NotExecuted' }).Count
            if ([int]$counters.total -ne $passed + $failed + $skipped) {
                $failureReason = 'The test run contains incomplete or aborted results.'
            }
        }
        catch { $failureReason = "Cannot read test results: $($_.Exception.Message)" }
    }
    else { $failureReason = 'The test command did not produce a TRX result file.' }

    if ($exitCode -ne 0) { $failureReason = "dotnet test exited with code $exitCode. $failureReason" }

    $script:totalPassed  += $passed
    $script:totalFailed  += $failed
    $script:totalSkipped += $skipped

    if ($failed -gt 0 -or $failureReason) {
        Write-Host "  FAILED: $passed passed, $failed failed, $skipped skipped" -ForegroundColor Red
        $script:failedProjects += $Name
        if ($failureReason) { Write-Host "  $failureReason" -ForegroundColor Red }
    }
    else {
        Write-Host "  PASSED: $passed passed, $skipped skipped" -ForegroundColor Green
    }
}

# Header
Write-Host ""
Write-Host ("=" * 60) -ForegroundColor Cyan
Write-Host "  win-optimizer Test Runner" -ForegroundColor Cyan
Write-Host ("=" * 60) -ForegroundColor Cyan

$startTime = Get-Date

if (-not $IntegrationOnly) {
    # Unit Tests - Core
    Run-TestProject `
        -Name "Core Unit Tests" `
        -ProjectPath "$solutionDir\tests\WinOptimizer.Core.Tests\WinOptimizer.Core.Tests.csproj"

    # Unit Tests - Application (no WinUI runtime required)
    Run-TestProject `
        -Name "Application Unit Tests" `
        -ProjectPath "$solutionDir\tests\WinOptimizer.Application.Tests\WinOptimizer.Application.Tests.csproj"

    # Unit Tests - Infrastructure
    Run-TestProject `
        -Name "Infrastructure Unit Tests" `
        -ProjectPath "$solutionDir\tests\WinOptimizer.Infrastructure.Tests\WinOptimizer.Infrastructure.Tests.csproj"

    # Unit Tests - UI (requires Visual Studio / WinUI SDK, built with MSBuild)
    if (-not $SkipUITests) {
        $uiProjectPath = "$solutionDir\tests\WinOptimizer.UI.Tests\WinOptimizer.UI.Tests.csproj"

        try {
            $MSBuildPath = & (Join-Path $PSScriptRoot 'Resolve-MSBuild.ps1') -MSBuildPath $MSBuildPath
            Write-Host ""
            Write-Host ("=" * 60) -ForegroundColor DarkGray
            Write-Host "  Building UI Tests with MSBuild..." -ForegroundColor Cyan
            Write-Host ("=" * 60) -ForegroundColor DarkGray

            # Packaging has already built the Release application and its references.
            # Standalone checks retain their isolated Debug application build.
            $uiConfiguration = 'Debug'
            $uiBuildRoot = (Join-Path $solutionDir 'build\ui-test-build') + '/'
            $uiBuildArguments = @()
            if ($UseExistingReleaseBuild) {
                $uiConfiguration = 'Release'
                $uiBuildRoot = (Join-Path $solutionDir 'build') + '/'
                foreach ($file in @('win-optimizer.exe', 'win-optimizer.dll')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $uiBuildRoot $file) -PathType Leaf)) {
                        throw 'Build the Release application before using -UseExistingReleaseBuild.'
                    }
                }
                $uiBuildArguments += '-property:BuildProjectReferences=false'
            }
            $originalErrorAction = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $buildOutput = & $MSBuildPath $uiProjectPath "-property:Configuration=$uiConfiguration" -property:Platform=x64 `
                    "-property:WinOptimizerBuildRoot=$uiBuildRoot" -verbosity:quiet -restore -maxCpuCount @uiBuildArguments 2>&1 | Out-String
                $buildExitCode = $LASTEXITCODE
            }
            finally { $ErrorActionPreference = $originalErrorAction }
            if ($buildOutput.Trim()) { Write-Host $buildOutput -ForegroundColor DarkGray }
            if ($buildExitCode -ne 0) {
                throw "UI test build exited with code $buildExitCode."
            }
            else {
                Write-Host "  Build succeeded" -ForegroundColor Green

                # WinUI test projects require Visual Studio Test Explorer to run.
                # The native Windows App SDK runtime DLLs cannot be loaded by
                # the command-line test host (dotnet test or vstest.console.exe).
                # The build-only check above confirms the tests still compile.
                Write-Host "  UI Tests: Build verified (run via VS Test Explorer)" -ForegroundColor Green
            }
        }
        catch {
            $failedProjects += 'UI Test Build'
            Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    else {
        Write-Host ""
        Write-Host "  Skipping UI Tests (-SkipUITests)" -ForegroundColor Yellow
    }
}

# Integration Tests
Run-TestProject `
    -Name "Integration Tests" `
    -ProjectPath "$solutionDir\tests\WinOptimizer.IntegrationTests\WinOptimizer.IntegrationTests.csproj"

# Summary
$elapsed = (Get-Date) - $startTime
$totalTests = $totalPassed + $totalFailed + $totalSkipped

Write-Host ""
Write-Host ("=" * 60) -ForegroundColor Cyan
Write-Host "  Test Summary" -ForegroundColor Cyan
Write-Host ("=" * 60) -ForegroundColor Cyan
Write-Host "  Total:   $totalTests" -ForegroundColor White
Write-Host "  Passed:  $totalPassed" -ForegroundColor Green
if ($totalFailed -gt 0) {
    Write-Host "  Failed:  $totalFailed" -ForegroundColor Red
}
else {
    Write-Host "  Failed:  0" -ForegroundColor Green
}
if ($totalSkipped -gt 0) {
    Write-Host "  Skipped: $totalSkipped" -ForegroundColor Yellow
}
Write-Host "  Time:    $($elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor White
Write-Host "  Results: $resultsRoot" -ForegroundColor White
Write-Host ("=" * 60) -ForegroundColor Cyan

if ($failedProjects.Count -gt 0) {
    Write-Host ""
    Write-Host "  Failed projects:" -ForegroundColor Red
    foreach ($proj in $failedProjects) {
        Write-Host "    - $proj" -ForegroundColor Red
    }
    Write-Host ""
    exit 1
}
else {
    Write-Host ""
    Write-Host "  All requested checks passed." -ForegroundColor Green
    Write-Host ""
    exit 0
}
