# Tests the production process-monitor UI using fake OS boundaries only.
[CmdletBinding()]
param([string]$MSBuildPath, [string]$BuildRoot)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$statusRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildRoot) { $BuildRoot = Join-Path $statusRoot 'build/process-monitor-tests' }
$statusExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.ProcessMonitor.Tests -BuildRoot $BuildRoot -MSBuildPath $MSBuildPath
& $statusExecutable `
    (Join-Path $statusRoot 'build/process-monitor-preview')
if ($LASTEXITCODE -ne 0) { throw "Process monitor checks failed: $LASTEXITCODE" }
