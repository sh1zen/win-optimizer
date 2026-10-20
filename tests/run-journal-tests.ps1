# Builds the production topic journal and runs isolated view-model and native layout checks.
[CmdletBinding()]
param([string]$MSBuildPath, [string]$BuildRoot)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$journalRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildRoot) { $BuildRoot = Join-Path $journalRoot 'build/journal-validation' }
$journalExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.Journal.Tests -BuildRoot $BuildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation
& $journalExecutable `
    (Join-Path $journalRoot 'build/journal-previews')
if ($LASTEXITCODE -ne 0) { throw "Journal checks failed: $LASTEXITCODE" }
