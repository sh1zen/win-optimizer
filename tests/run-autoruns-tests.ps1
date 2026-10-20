# Tests the production autorun management UI using fake OS boundaries only.
[CmdletBinding()]
param([string]$MSBuildPath, [string]$BuildRoot)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$statusRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildRoot) { $BuildRoot = Join-Path $statusRoot 'build/autoruns-tests' }
$statusExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.Autoruns.Tests -BuildRoot $BuildRoot -MSBuildPath $MSBuildPath
& $statusExecutable `
    (Join-Path $statusRoot 'build/autoruns-preview')
if ($LASTEXITCODE -ne 0) { throw "Autoruns checks failed: $LASTEXITCODE" }
