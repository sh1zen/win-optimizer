# Runs production app sorting and view-model checks against deterministic fake system boundaries.
[CmdletBinding()]
param([string]$MSBuildPath, [string]$BuildRoot)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$sortingRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildRoot) { $BuildRoot = Join-Path $sortingRoot 'build/software-apps-tests' }
$sortingExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.SoftwareApps.Tests -BuildRoot $BuildRoot -MSBuildPath $MSBuildPath
$sortingResult = Join-Path $BuildRoot 'sorting-result.txt'
if (Test-Path -LiteralPath $sortingResult) { Remove-Item -LiteralPath $sortingResult }
& $sortingExecutable $sortingResult
$sortingExitCode = $LASTEXITCODE
if (Test-Path -LiteralPath $sortingResult) { Get-Content -LiteralPath $sortingResult }
if ($sortingExitCode -ne 0) { throw "Software & Apps checks failed: $sortingExitCode" }
