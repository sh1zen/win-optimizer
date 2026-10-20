# Verifies shell readiness, deferred inventory/backup, first-frame rendering and section navigation with fake OS boundaries.
[CmdletBinding()]
param([string]$MSBuildPath, [string]$BuildRoot)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$startupRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildRoot) { $BuildRoot = Join-Path $startupRoot 'build/startup-validation' }
$startupExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.Startup.Tests -BuildRoot $BuildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation
& $startupExecutable `
    (Join-Path $BuildRoot 'startup-result.txt')
if ($LASTEXITCODE -ne 0) { throw "Startup checks failed: $LASTEXITCODE" }
