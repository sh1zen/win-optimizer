# Exercise the production test runner with deterministic TRX results at the dotnet boundary.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tokens = $null; $parseErrors = $null
$runner = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'extras/run-win-optimizer-tests.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$function = $runner.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Run-TestProject'
}, $false)
if (@($function).Count -ne 1) { throw 'Expected one production Run-TestProject function.' }
$definition = [scriptblock]::Create($function.Extent.Text)
$resultsParent = Join-Path $repoRoot 'build/test-results'
$resultsRoot = Join-Path $resultsParent ('runner-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $resultsRoot -Force
$savedExitCode = $LASTEXITCODE
$passed = 0
try {
    foreach ($scenario in @(
        @{ Outcome = 'Passed'; ExitCode = 0; Failure = $false; Message = '' },
        @{ Outcome = 'Failed'; ExitCode = 1; Failure = $true; Message = 'Fixture timeout details' },
        @{ Outcome = 'Failed'; ExitCode = 0; Failure = $true; Message = 'Fixture timeout details' },
        @{ Outcome = 'Passed'; ExitCode = 1; Failure = $true; Message = 'dotnet test exited with code 1' },
        @{ Outcome = ''; ExitCode = 1; Failure = $true; Message = 'did not produce a TRX result file' }
    )) {
        $totalPassed = 0; $totalFailed = 0; $totalSkipped = 0; $failedProjects = @()
        $state = @{ Lines = [Collections.Generic.List[string]]::new() }
        & {
            param($definition, $scenario, $state)
            # Fake only the external test command and console output.
            function dotnet {
                $path = Join-Path $resultsRoot 'Fixture.trx'
                if ($scenario.Outcome) {
                    $failed = [int]($scenario.Outcome -eq 'Failed')
                    $succeeded = 1 - $failed
                    $xml = @"
<TestRun><Results><UnitTestResult testName="Fixture.NetworkWorker" outcome="$($scenario.Outcome)">
<Output><ErrorInfo><Message>Fixture timeout details</Message><StackTrace>at Fixture.NetworkWorker()</StackTrace></ErrorInfo></Output>
</UnitTestResult></Results><ResultSummary><Counters total="1" passed="$succeeded" failed="$failed" /></ResultSummary></TestRun>
"@
                    Set-Content -LiteralPath $path -Value $xml
                } elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
                $global:LASTEXITCODE = $scenario.ExitCode
            }
            function Write-Host {
                param($Object, $ForegroundColor)
                $state.Lines.Add([string]$Object)
            }
            . $definition
            Run-TestProject -Name 'Fixture tests' -ProjectPath 'Fixture.csproj'
        } $definition $scenario $state
        if (($failedProjects.Count -gt 0) -ne $scenario.Failure) { throw 'Unexpected test-project success/failure decision.' }
        $messages = $state.Lines -join "`n"
        if ($scenario.Message -and $messages -notlike "*$($scenario.Message)*") { throw "Missing failure diagnostic: $($scenario.Message)" }
        if ($scenario.Outcome -eq 'Failed' -and
            ($totalFailed -ne 1 -or $messages -notlike '*Fixture.NetworkWorker*' -or $messages -notlike '*at Fixture.NetworkWorker()*')) {
            throw 'Failed test details or counters were lost.'
        }
        if ($scenario.Outcome -eq 'Passed' -and $totalPassed -ne 1) { throw 'Passing test counters were lost.' }
        if ($scenario.Outcome -eq 'Passed' -and $messages -like '*Fixture timeout details*') { throw 'A passing test was reported as failed.' }
        $passed++
    }
} finally {
    $global:LASTEXITCODE = $savedExitCode
    $cleanupPath = [IO.Path]::GetFullPath($resultsRoot)
    if (-not $cleanupPath.StartsWith($resultsParent + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe runner fixture cleanup path.' }
    [IO.Directory]::Delete($cleanupPath, $true)
}
Write-Host "Passed $passed test-runner result and diagnostic scenarios."
