#requires -Version 5.1
# Finds a registered MSBuild installation compatible with the project's .NET SDK.
[CmdletBinding()]
param([string]$MSBuildPath)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

if (-not $MSBuildPath) {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswherePath -PathType Leaf) {
        $candidates = & $vswherePath -latest -products '*' -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\Current\Bin\amd64\MSBuild.exe'
        if ($LASTEXITCODE -ne 0) {
            throw "Visual Studio discovery failed with exit code $LASTEXITCODE."
        }
        $MSBuildPath = $candidates | Select-Object -First 1
    }

    if (-not $MSBuildPath) {
        $command = Get-Command MSBuild.exe -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($command) { $MSBuildPath = $command.Source }
    }
}

if (-not $MSBuildPath -or -not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw 'MSBuild.exe not found. Install Visual Studio 2026 / Build Tools with the .NET desktop and C++ desktop workloads, or pass -MSBuildPath.'
}
$MSBuildPath = (Resolve-Path -LiteralPath $MSBuildPath).Path

$versionText = & $MSBuildPath -version -nologo
$msbuildVersion = $null
if ($LASTEXITCODE -ne 0 -or
    -not [version]::TryParse(($versionText -join '').Trim(), [ref]$msbuildVersion)) {
    throw "Could not determine the MSBuild version: $MSBuildPath"
}
if ($msbuildVersion -lt [version]'18.0') {
    throw "MSBuild $msbuildVersion is incompatible with .NET 10. Install Visual Studio 2026 / Build Tools with MSBuild 18 or later, or pass -MSBuildPath."
}

$MSBuildPath
