#requires -Version 5.1
# Archive matching compiler symbols without copying them into runtime deployments.
[CmdletBinding()]
param(
    [string]$MSBuildPath,
    [string]$BuildRoot,
    [ValidatePattern('^[0-9]{2}\.[0-9]{2}\.[0-9]{2}$')]
    [Parameter(Mandatory)][string]$Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $BuildRoot) { $BuildRoot = Join-Path $repoRoot 'build' }
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot).TrimEnd('\', '/') + '/'
$MSBuildPath = & (Join-Path $PSScriptRoot 'Resolve-MSBuild.ps1') -MSBuildPath $MSBuildPath
$project = Join-Path $repoRoot 'src/WinOptimizer.UI/WinOptimizer.UI.csproj'
$assembly = Join-Path $BuildRoot 'win-optimizer.dll'
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf) -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion.Trim() -cne $Version) {
    throw 'Build the requested Release version before exporting its debug symbols.'
}
$result = & $MSBuildPath $project -target:CollectReleaseDebugSymbols -getTargetResult:CollectReleaseDebugSymbols `
    -property:Configuration=Release -property:Platform=x64 "-property:WinOptimizerVersion=$Version" `
    "-property:WinOptimizerBuildRoot=$BuildRoot" -verbosity:quiet -nologo
if ($LASTEXITCODE -ne 0) { throw 'Release debug symbol collection failed.' }
$collection = (($result -join [Environment]::NewLine) | ConvertFrom-Json).TargetResults.CollectReleaseDebugSymbols
if ($collection.Result -ne 'Success') { throw 'Release debug symbol collection did not succeed.' }
$symbols = @($collection.Items | ForEach-Object { $_.FullPath })
if (-not $symbols.Count) { throw 'No Release debug symbols were collected.' }
if (@($symbols | Group-Object { [IO.Path]::GetFileName($_) } | Where-Object Count -gt 1).Count) {
    throw 'Release symbol names are ambiguous; archive paths must be unique.'
}
$destination = Join-Path $BuildRoot "symbols/win-optimizer-v$Version-win-x64.zip"
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
Compress-Archive -LiteralPath $symbols -DestinationPath $destination -CompressionLevel Optimal -Force
Write-Host "Release debug symbols: $destination"
