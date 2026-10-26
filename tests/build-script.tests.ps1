# Exercise the build entry point with a fake MSBuild boundary and isolated output.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureParent = Join-Path $repoRoot 'build'
$fixture = Join-Path $fixtureParent ('build-script-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force
$savedExitCode = $LASTEXITCODE
$passed = 0
try {
    $compiler = Join-Path $fixture 'MSBuild.ps1'
    # Only the external compiler is replaced. The production script owns arguments and errors.
    Set-Content -LiteralPath $compiler -Value @'
if ($args -contains '-version') {
    $global:LASTEXITCODE = 0
    '18.0.0'
    return
}
$args | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'arguments.json')
$scenario = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'scenario.json') -Raw | ConvertFrom-Json
$rootArgument = $args | Where-Object { $_ -like '-property:WinOptimizerBuildRoot=*' }
$outputRoot = $rootArgument.Substring('-property:WinOptimizerBuildRoot='.Length)
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
        try { & (Join-Path $repoRoot 'build.ps1') -MSBuildPath $compiler -BuildRoot $fixture @options }
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
        $passed++
    }
} finally {
    $global:LASTEXITCODE = $savedExitCode
    $cleanupPath = [IO.Path]::GetFullPath($fixture)
    if (-not $cleanupPath.StartsWith($fixtureParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe build fixture cleanup path.'
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force
}
Write-Host "Passed $passed build entry-point scenarios."
