# Run existing native UI suites against the published production assemblies and runtimes.
[CmdletBinding()]
param(
    [ValidateSet('Startup', 'SoftwareApps')][string]$Suite = 'Startup',
    [string]$PublishDirectory,
    [string]$MSBuildPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot 'build/publish' }
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')
if ((Get-Item -LiteralPath $publish).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The publish directory must not be linked.' }
$buildRoot = Join-Path $repoRoot $(if ($Suite -eq 'Startup') { 'build/startup-validation' } else { 'build/software-apps-tests' })
$executable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') -ProjectName "WinOptimizer.$Suite.Tests" `
    -BuildRoot $buildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation
$hostDirectory = Split-Path -Parent $executable
$originalHostFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $hostDirectory -Recurse -File) { $null = $originalHostFiles.Add($file.FullName) }
$executable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') -ProjectName "WinOptimizer.$Suite.Tests" `
    -BuildRoot $buildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation -SelfContained
$addedHostFiles = @(Get-ChildItem -LiteralPath $hostDirectory -Recurse -File | Where-Object { -not $originalHostFiles.Contains($_.FullName) })
$result = Join-Path $buildRoot 'published-result.txt'
$files = [Collections.Generic.List[string]]::new()
$directories = [Collections.Generic.List[string]]::new()
try {
    # Add only missing test-host dependencies. Existing runtime files are never overwritten.
    foreach ($file in Get-ChildItem -LiteralPath $hostDirectory -Recurse -File) {
        $relative = $file.FullName.Substring($hostDirectory.Length + 1)
        $destination = [IO.Path]::GetFullPath((Join-Path $publish $relative))
        if (-not $destination.StartsWith($publish + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe UI test fixture path.' }
        if (Test-Path -LiteralPath $destination) { continue }
        $parent = Split-Path -Parent $destination
        while (-not (Test-Path -LiteralPath $parent)) {
            $directories.Add($parent)
            $parent = Split-Path -Parent $parent
        }
        $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        $files.Add($destination)
        [IO.File]::Copy($file.FullName, $destination)
    }
    & (Join-Path $publish ([IO.Path]::GetFileName($executable))) $result
    if ($LASTEXITCODE -ne 0) { throw "$Suite UI checks failed from published output: $LASTEXITCODE. See $result" }
    Get-Content -LiteralPath $result
} finally {
    $testExitCode = $LASTEXITCODE
    # Remove only fixture files created by this run, then empty fixture directories.
    foreach ($file in $files) { Remove-Item -LiteralPath $file -Force }
    foreach ($directory in $directories | Select-Object -Unique | Sort-Object Length -Descending) {
        if (-not @(Get-ChildItem -LiteralPath $directory -Force).Count) { Remove-Item -LiteralPath $directory }
    }
    # Restore normal settings and remove only the extra runtime files introduced by this fixture.
    $null = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') -ProjectName "WinOptimizer.$Suite.Tests" `
        -BuildRoot $buildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation
    foreach ($file in $addedHostFiles) { if (Test-Path -LiteralPath $file.FullName) { Remove-Item -LiteralPath $file.FullName -Force } }
    $global:LASTEXITCODE = $testExitCode
}
