# Builds and runs production Security presentation against deterministic fake Windows boundaries.
[CmdletBinding()]
param([string]$MSBuildPath)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$securityRoot = Split-Path -Parent $PSScriptRoot
$securityBuildRoot = Join-Path $securityRoot 'build/security-tests'
$securityExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.Security.Tests -BuildRoot $securityBuildRoot -MSBuildPath $MSBuildPath
& $securityExecutable `
    (Join-Path $securityRoot 'build/security-preview')
if ($LASTEXITCODE -ne 0) { throw "Security checks failed: $LASTEXITCODE" }
