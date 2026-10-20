# Tests the production Windows servicing UI using fake OS boundaries only.
[CmdletBinding()]
param([string]$MSBuildPath, [string]$BuildRoot)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$statusRoot = Split-Path -Parent $PSScriptRoot
if (-not $BuildRoot) { $BuildRoot = Join-Path $statusRoot 'build/windows-status-tests' }
$statusExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.WindowsStatus.Tests -BuildRoot $BuildRoot -MSBuildPath $MSBuildPath
& $statusExecutable `
    (Join-Path $statusRoot 'build/windows-status-preview')
if ($LASTEXITCODE -ne 0) { throw "Windows status checks failed: $LASTEXITCODE" }
