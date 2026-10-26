# Builds a native UI test host from cached packages and returns its executable path.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectName,
    [Parameter(Mandatory)][string]$BuildRoot,
    [string]$MSBuildPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$DisableSharedCompilation,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ($ProjectName -notmatch '^WinOptimizer\.[A-Za-z]+\.Tests$') {
    throw "Invalid native test project name: $ProjectName"
}
$projectPath = Join-Path $PSScriptRoot "$ProjectName/$ProjectName.csproj"
$project = [xml](Get-Content -LiteralPath $projectPath -Raw)
$MSBuildPath = & (Join-Path $repositoryRoot 'extras/Resolve-MSBuild.ps1') -MSBuildPath $MSBuildPath
$buildDirectory = [IO.Path]::GetFullPath($BuildRoot).TrimEnd('\', '/') + '/'
$packageCache = $env:NUGET_PACKAGES
if (-not $packageCache) { $packageCache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$buildArguments = @($projectPath, '-restore', '-maxCpuCount:1', '-nodeReuse:false',
    "-p:Configuration=$Configuration", '-p:Platform=x64', "-p:WinOptimizerBuildRoot=$buildDirectory",
    '-p:NuGetAudit=false', "-p:RestoreSources=$packageCache", '-verbosity:minimal', '-nologo')
if ($DisableSharedCompilation) { $buildArguments += '-p:UseSharedCompilation=false' }
if ($SelfContained) { $buildArguments += '-p:SelfContained=true' }
& $MSBuildPath @buildArguments | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "$ProjectName build failed: $LASTEXITCODE" }

$framework = $project.Project.PropertyGroup.TargetFramework | Select-Object -First 1
$runtime = $project.Project.PropertyGroup.RuntimeIdentifier | Select-Object -First 1
$executable = [IO.Path]::GetFullPath((Join-Path $buildDirectory "tests/$ProjectName/x64/$Configuration/$framework/$runtime/$ProjectName.exe"))
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Native test build did not produce its executable: $executable"
}
$executable
