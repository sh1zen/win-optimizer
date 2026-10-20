# Verifies production Network controls with fake Windows boundaries and native rendering.
[CmdletBinding()]
param([string]$MSBuildPath)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$networkRoot = Split-Path -Parent $PSScriptRoot
$networkBuildRoot = Join-Path $networkRoot 'build'
$networkExecutable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') `
    -ProjectName WinOptimizer.Network.Tests -BuildRoot $networkBuildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation
& $networkExecutable `
    (Join-Path $networkRoot 'build/network-preview')
if ($LASTEXITCODE -ne 0) { throw "Network checks failed: $LASTEXITCODE" }
