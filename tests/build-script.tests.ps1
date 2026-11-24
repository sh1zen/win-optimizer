# Exercise the build entry point with a fake MSBuild boundary and isolated output.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureParent = Join-Path $repoRoot 'build'
$fixture = Join-Path $fixtureParent ('build-script-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force
$savedExitCode = $LASTEXITCODE
$passed = 0
try {
    $buildScript = Join-Path $repoRoot 'build.ps1'
    $compiler = Join-Path $fixture 'MSBuild.ps1'
    $lockPath = Join-Path $fixture '.win-optimizer-build.lock'
    # Only the external compiler is replaced. The production script owns arguments and errors.
    Set-Content -LiteralPath $compiler -Value @'
if ($args -contains '-version') {
    $global:LASTEXITCODE = 0
    '18.0.0'
    return
}
$scenario = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scenario.json') -Raw | ConvertFrom-Json
$rootArgument = $args | Where-Object { $_ -like '-property:WinOptimizerBuildRoot=*' }
$outputRoot = $rootArgument.Substring('-property:WinOptimizerBuildRoot='.Length)
$args | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'arguments.json')
if (Test-Path -LiteralPath (Join-Path $outputRoot 'hold-compiler')) {
    Set-Content -LiteralPath (Join-Path $outputRoot 'compiler-started') -Value $PID
    Start-Sleep -Seconds 30
    throw 'Timed out waiting for the contention test to stop the build process.'
}
if ($scenario.Executable) { Set-Content -LiteralPath (Join-Path $outputRoot 'win-optimizer.exe') -Value 'fixture' }
$global:LASTEXITCODE = $scenario.ExitCode
'@
    foreach ($scenario in @(
        @{ Options = @{}; Restore = $true; Target = 'Build'; ExitCode = 0; Executable = $true; Error = '' },
        @{ Options = @{ NoRestore = $true }; Restore = $false; Target = 'Build'; ExitCode = 0; Executable = $true; Error = '' },
        @{ Options = @{ NoRestore = $true; Clean = $true }; Restore = $false; Target = 'Rebuild'; ExitCode = 0; Executable = $true; Error = '' },
        @{ Options = @{}; Restore = $true; Target = 'Build'; ExitCode = 9; Executable = $true; Error = 'MSBuild failed with exit code 9' },
        @{ Options = @{}; Restore = $true; Target = 'Build'; ExitCode = 0; Executable = $false; Error = 'executable was not found' }
    )) {
        $scenario | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'scenario.json')
        $executable = Join-Path $fixture 'win-optimizer.exe'
        if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable }
        $originalPath = $env:PATH
        $originalLocation = (Get-Location).Path
        $failure = ''
        $options = $scenario.Options
        try { & $buildScript -MSBuildPath $compiler -BuildRoot $fixture @options }
        catch { $failure = $_.Exception.Message }
        if ($scenario.Error) {
            if ($failure -notlike "*$($scenario.Error)*") { throw "Missing build failure: $($scenario.Error). Actual: $failure" }
        } elseif ($failure) { throw $failure }
        $arguments = Get-Content -LiteralPath (Join-Path $fixture 'arguments.json') -Raw | ConvertFrom-Json
        if (($arguments -contains '-restore') -ne $scenario.Restore -or
            $arguments -notcontains "-target:$($scenario.Target)") { throw 'Incorrect restore/build targets.' }
        if ($arguments -notcontains '-graphBuild' -or
            ($arguments -contains '-property:RestoreUseStaticGraphEvaluation=true') -ne $scenario.Restore) {
            throw 'Build/restore did not use the static project graph.'
        }
        if ($env:PATH -cne $originalPath -or (Get-Location).Path -ne $originalLocation) { throw 'Build changed the caller environment.' }
        if (Test-Path -LiteralPath $lockPath) { throw 'Build left its lock file behind.' }
        $passed++
    }

    # Hold the first build inside MSBuild so the second invocation exercises real process contention.
    @{ ExitCode = 0; Executable = $true } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'scenario.json')
    $holdCompiler = Join-Path $fixture 'hold-compiler'
    $compilerStarted = Join-Path $fixture 'compiler-started'
    $workerError = Join-Path $fixture 'worker-error.log'
    $null = New-Item -ItemType File -Path $holdCompiler
    $hostName = if ($PSVersionTable.PSEdition -eq 'Desktop') { 'powershell.exe' } else { 'pwsh.exe' }
    $worker = Start-Process -FilePath (Join-Path $PSHOME $hostName) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $fixture 'worker-output.log') -RedirectStandardError $workerError `
        -ArgumentList '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $buildScript), `
            '-MSBuildPath', ('"{0}"' -f $compiler), '-BuildRoot', ('"{0}"' -f $fixture)
    try {
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $compilerStarted)) {
            if ($worker.HasExited -or $deadline.Elapsed.TotalSeconds -ge 15) {
                throw "The first build did not reach MSBuild. $(Get-Content -LiteralPath $workerError -Raw)"
            }
            Start-Sleep -Milliseconds 50
        }

        $failure = ''
        try { & $buildScript -MSBuildPath $compiler -BuildRoot (Join-Path $fixture '.') -Clean }
        catch { $failure = $_.Exception.Message }
        if ($failure -notlike '*Another build is already running*') { throw "Missing build contention error. Actual: $failure" }
        $passed++

        & $buildScript -MSBuildPath $compiler -BuildRoot (Join-Path $fixture 'independent')
        $passed++
    } finally {
        try {
            if (-not $worker.HasExited) { $worker.Kill() }
            if (-not $worker.WaitForExit(5000)) { throw 'The first build process did not stop.' }
        } finally {
            $worker.Dispose()
        }
    }

    if (Test-Path -LiteralPath $lockPath) { throw 'The terminated build left its lock file behind.' }
    Remove-Item -LiteralPath $holdCompiler
    & $buildScript -MSBuildPath $compiler -BuildRoot $fixture
    $passed++
} finally {
    $global:LASTEXITCODE = $savedExitCode
    $cleanupPath = [IO.Path]::GetFullPath($fixture)
    if (-not $cleanupPath.StartsWith($fixtureParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe build fixture cleanup path.'
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force
}
Write-Host "Passed $passed build entry-point scenarios."
