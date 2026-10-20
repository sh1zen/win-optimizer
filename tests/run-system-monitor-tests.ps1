# Builds and exercises the production widget with fake preferences and synthetic sensor values.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildRoot = Join-Path $repositoryRoot 'build/ui-test-build/'
$executable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.SystemMonitor.Tests -BuildRoot $buildRoot -DisableSharedCompilation
& $executable (Join-Path $repositoryRoot 'build/system-monitor-validation')
if ($LASTEXITCODE -ne 0) { throw "SysMonitor checks failed: $LASTEXITCODE" }
