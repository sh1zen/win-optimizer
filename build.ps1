#requires -Version 5.1

<#
.SYNOPSIS
Builds win-optimizer with Visual Studio MSBuild and restores NuGet packages.
.DESCRIPTION
Requires the .NET SDK selected by global.json, the Windows SDK, and Visual
Studio 2026 / MSBuild 18 or later with the .NET desktop and C++ desktop
workloads (or Build Tools).
The executable is written to build/win-optimizer.exe; the build log is saved
to build/logs/build.log. Version.props supplies the YY.MM.DD build-date version;
-Version selects an explicit date for a release.
.EXAMPLE
.\build.ps1
.EXAMPLE
.\build.ps1 -Configuration Debug -Clean
.EXAMPLE
.\build.ps1 -MSBuildPath 'C:\tools\MSBuild.exe'
.EXAMPLE
.\build.ps1 -BuildRoot .\build\isolated
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Clean,
    [string]$MSBuildPath,
    [string]$BuildRoot,
    [ValidatePattern('^[0-9]{2}\.[0-9]{2}\.[0-9]{2}$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
# Check native exit codes explicitly, including in PowerShell 7 hosts.
$PSNativeCommandUseErrorActionPreference = $false

$projectPath = Join-Path $PSScriptRoot 'src\WinOptimizer.UI\WinOptimizer.UI.csproj'
if (-not $BuildRoot) { $BuildRoot = Join-Path $PSScriptRoot 'build' }
# A trailing forward slash also survives PowerShell 5.1's native argument quoting.
$BuildRoot = [System.IO.Path]::GetFullPath($BuildRoot).TrimEnd('\', '/') + '/'
$exePath = Join-Path $BuildRoot 'win-optimizer.exe'
$buildLog = Join-Path $BuildRoot 'logs\build.log'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "UI project not found: $projectPath"
}

$MSBuildPath = & (Join-Path $PSScriptRoot 'extras\Resolve-MSBuild.ps1') -MSBuildPath $MSBuildPath

$null = New-Item -ItemType Directory -Path (Split-Path -Parent $buildLog) -Force
$target = if ($Clean) { 'Rebuild' } else { 'Build' }
$buildArguments = @(
    $projectPath
    '-restore'
    '-maxCpuCount'
    "-target:$target"
    "-property:Configuration=$Configuration"
    '-property:Platform=x64'
    "-property:WinOptimizerBuildRoot=$BuildRoot"
    '-verbosity:minimal'
    '-nologo'
    '-fileLogger'
    "-fileLoggerParameters:LogFile=$buildLog;Verbosity=normal;Encoding=UTF-8"
)
if ($Version) { $buildArguments += "-property:WinOptimizerVersion=$Version" }

Write-Host "Building win-optimizer ($Configuration/x64) using $MSBuildPath" -ForegroundColor Cyan
# Resolve global.json and relative project paths from the repository root.
$originalPath = $env:PATH
Push-Location -LiteralPath $PSScriptRoot
try {
    # MSBuild's SDK resolver needs dotnet even when its directory is absent from PATH.
    if (-not (Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue)) {
        $programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
        $dotnetDirectory = Join-Path $programFiles64 'dotnet'
        if (-not (Test-Path -LiteralPath (Join-Path $dotnetDirectory 'dotnet.exe') -PathType Leaf)) {
            throw 'dotnet.exe not found. Install the x64 .NET 10 SDK or add its directory to PATH.'
        }
        $env:PATH = "$dotnetDirectory;$originalPath"
    }

    & $MSBuildPath @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE. Full log: $buildLog"
    }
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "MSBuild completed but the executable was not found: $exePath"
    }
}
finally {
    $env:PATH = $originalPath
    Pop-Location
}

Write-Host "Build succeeded: $exePath" -ForegroundColor Green
Write-Host "Build log: $buildLog"
