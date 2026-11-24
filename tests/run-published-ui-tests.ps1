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
$buildRoot = [IO.Path]::GetFullPath($buildRoot)
if ($publish -eq $buildRoot -or $publish.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $buildRoot.StartsWith($publish + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The published payload and native test build must use separate directories.'
}
$executable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') -ProjectName "WinOptimizer.$Suite.Tests" `
    -BuildRoot $buildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation
$hostDirectory = Split-Path -Parent $executable
$originalHostFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $hostDirectory -Recurse -File) { $null = $originalHostFiles.Add($file.FullName) }
$executable = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') -ProjectName "WinOptimizer.$Suite.Tests" `
    -BuildRoot $buildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation -SelfContained
$addedHostFiles = @(Get-ChildItem -LiteralPath $hostDirectory -Recurse -File | Where-Object { -not $originalHostFiles.Contains($_.FullName) })
$result = Join-Path $buildRoot 'published-result.txt'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$fixtureName = 'win-optimizer-published-ui-' + [guid]::NewGuid().ToString('N')
$fixture = [IO.Directory]::CreateDirectory((Join-Path $tempRoot $fixtureName))

# Stage the exact published bytes. Reject links before walking into another directory.
function Copy-PublishedPayload([string]$Source, [string]$Destination) {
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked payload entry: $($item.FullName)" }
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            $null = [IO.Directory]::CreateDirectory($target)
            Copy-PublishedPayload $item.FullName $target
        }
        else { [IO.File]::Copy($item.FullName, $target) }
    }
}

try {
    Copy-PublishedPayload $publish $fixture.FullName
    # Add only test-host dependencies unavailable in the staged distribution.
    foreach ($file in Get-ChildItem -LiteralPath $hostDirectory -Recurse -File) {
        $relative = $file.FullName.Substring($hostDirectory.Length + 1)
        $destination = [IO.Path]::GetFullPath((Join-Path $fixture.FullName $relative))
        if (-not $destination.StartsWith($fixture.FullName + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe UI test fixture path.' }
        if (Test-Path -LiteralPath $destination) { continue }
        $null = [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        [IO.File]::Copy($file.FullName, $destination)
    }
    & (Join-Path $fixture.FullName ([IO.Path]::GetFileName($executable))) $result
    if ($LASTEXITCODE -ne 0) { throw "$Suite UI checks failed from published output: $LASTEXITCODE. See $result" }
    Get-Content -LiteralPath $result
} finally {
    $testExitCode = $LASTEXITCODE
    if ($fixture.Parent.FullName -ne $tempRoot -or $fixture.Name -ne $fixtureName) {
        throw "Unexpected published UI fixture path: $($fixture.FullName)"
    }
    $fixture.Delete($true)
    # Restore normal settings and remove only the extra runtime files introduced by this fixture.
    $null = & (Join-Path $PSScriptRoot 'Build-NativeTestHost.ps1') -ProjectName "WinOptimizer.$Suite.Tests" `
        -BuildRoot $buildRoot -MSBuildPath $MSBuildPath -DisableSharedCompilation
    foreach ($file in $addedHostFiles) { if (Test-Path -LiteralPath $file.FullName) { Remove-Item -LiteralPath $file.FullName -Force } }
    $global:LASTEXITCODE = $testExitCode
}
