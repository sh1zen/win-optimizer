# Verify the actual distributable against MSBuild's payload without launching the application.
[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $InstallerPath) { $InstallerPath = Join-Path $repoRoot 'build/installers/win-optimizer.Installer.exe' }
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot 'build/publish' }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path.TrimEnd('\')

# Check the date text and both Windows numeric version resources on the real files.
function Assert-ArtifactVersion {
    param([string]$Path, [string]$ExpectedVersion)
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($metadata.ProductVersion.Trim() -cne $ExpectedVersion -or
        $metadata.FileVersion.Trim() -cne $ExpectedVersion) {
        throw "Version mismatch in ${Path}: product '$($metadata.ProductVersion.Trim())', file '$($metadata.FileVersion.Trim())'; expected $ExpectedVersion."
    }
    $expectedNumeric = [version]"$ExpectedVersion.0"
    foreach ($kind in @('File', 'Product')) {
        $actualNumeric = [version]::new($metadata."${kind}MajorPart", $metadata."${kind}MinorPart",
            $metadata."${kind}BuildPart", $metadata."${kind}PrivatePart")
        if ($actualNumeric -ne $expectedNumeric) {
            throw "Numeric $kind version mismatch in ${Path}: $actualNumeric; expected $expectedNumeric."
        }
    }
}

# Read the compiled entry assembly rather than recomputing the date after packaging.
$appMetadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishDirectory 'win-optimizer.dll'))
$releaseVersion = $appMetadata.ProductVersion.Trim()
if ($releaseVersion -cnotmatch '^[0-9]{2}\.[0-9]{2}\.[0-9]{2}$') {
    throw "The published application has an invalid version: $releaseVersion"
}
$null = [DateTime]::ParseExact("20$releaseVersion", 'yyyy.MM.dd', [Globalization.CultureInfo]::InvariantCulture)
foreach ($artifactPath in @($InstallerPath, (Join-Path $PublishDirectory 'win-optimizer.exe'),
        (Join-Path $PublishDirectory 'win-optimizer.dll'))) {
    Assert-ArtifactVersion -Path $artifactPath -ExpectedVersion $releaseVersion
}

$testParent = Join-Path $repoRoot 'build/package-tests'
$testRoot = Join-Path $testParent ([guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force

try {
    $destination = Join-Path $testRoot 'Portable app'
    $process = Start-Process -FilePath $InstallerPath -ArgumentList @(
        '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/TASKS=portableinstall', "/DIR=`"$destination`"", "/LOG=`"$testRoot\install.log`""
    ) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Packaged installation failed: $($process.ExitCode)." }
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'portable.marker'))) {
        throw 'The installed package is missing its portable marker.'
    }

    foreach ($applicationFile in @('win-optimizer.exe', 'win-optimizer.dll')) {
        Assert-ArtifactVersion -Path (Join-Path $destination $applicationFile) -ExpectedVersion $releaseVersion
    }

    # Match Inno's deliberate native-library exclusions, then verify every installed byte.
    $expected = @{}
    foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File) {
        if ($file.Name -in @('nul', 'onnxruntime.dll', 'DirectML.dll')) { continue }
        $relative = $file.FullName.Substring($PublishDirectory.Length + 1)
        $expected[$relative] = $file
        $installed = Join-Path $destination $relative
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf) -or
            (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "The package does not match the published file: $relative"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
        $relative = $file.FullName.Substring($destination.Length + 1)
        if ($relative -ne 'portable.marker' -and -not $expected.ContainsKey($relative)) {
            throw "Unexpected file in the installed package: $relative"
        }
    }
    foreach ($required in @('win-optimizer.exe', 'win-optimizer.dll', 'coreclr.dll', 'Microsoft.UI.Xaml.dll')) {
        if (-not $expected.ContainsKey($required)) { throw "Required application/runtime file is missing: $required" }
    }
    foreach ($language in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src/WinOptimizer.UI/Features/Common/Localization') -Filter '*.json') {
        $installed = Join-Path $destination "Localization/$($language.Name)"
        if (-not (Test-Path -LiteralPath $installed) -or
            (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $language.FullName).Hash) {
            throw "The package contains a missing or stale language resource: $($language.Name)"
        }
    }
}
finally {
    $cleanupPath = [IO.Path]::GetFullPath($testRoot)
    if (-not $cleanupPath.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package test cleanup path.' }
    [IO.Directory]::Delete($cleanupPath, $true)
}
Write-Host "Passed packaged portable installation: installer and application version $releaseVersion, $($expected.Count) published files, bundled runtimes and all language resources."
