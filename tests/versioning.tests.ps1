# Evaluate the production version owner and UI import without restoring or compiling.
[CmdletBinding()]
param([string]$MSBuildPath)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$MSBuildPath = & (Join-Path $repoRoot 'extras/Resolve-MSBuild.ps1') -MSBuildPath $MSBuildPath
$versionFile = Join-Path $repoRoot 'Version.props'
$uiProject = Join-Path $repoRoot 'src/WinOptimizer.UI/WinOptimizer.UI.csproj'
$passed = 0

# Query the same MSBuild properties used by compilation and installer packaging.
function Read-VersionProperties {
    param([string]$Project, [string]$Version)
    $arguments = @(
        $Project, '-target:ValidateWinOptimizerVersion', '-property:Platform=x64',
        '-getProperty:WinOptimizerVersion,Version,FileVersion,AssemblyVersion,InformationalVersion,IncludeSourceRevisionInInformationalVersion',
        '-verbosity:quiet', '-nologo'
    )
    if ($Version) { $arguments += "-property:WinOptimizerVersion=$Version" }
    $output = & $MSBuildPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Version evaluation failed: $output" }
    return (($output -join "`n") | ConvertFrom-Json).Properties
}

# Numeric and product metadata share the exact padded date.
function Assert-VersionProperties {
    param($Properties, [string]$Expected)
    foreach ($name in @('WinOptimizerVersion', 'Version', 'FileVersion', 'AssemblyVersion')) {
        if ($Properties.$name -cne $Expected) { throw "Unexpected ${name}: $($Properties.$name), expected $Expected." }
    }
    if ($Properties.InformationalVersion -cne $Expected -or
        $Properties.IncludeSourceRevisionInInformationalVersion -ne 'false') {
        throw 'Product version must contain only the padded date, without a commit suffix.'
    }
}

# Allow either side of midnight without fixing today's date in source code.
foreach ($project in @($versionFile, $uiProject)) {
    $before = [DateTime]::Now.ToString('yy.MM.dd', [Globalization.CultureInfo]::InvariantCulture)
    $properties = Read-VersionProperties -Project $project
    $after = [DateTime]::Now.ToString('yy.MM.dd', [Globalization.CultureInfo]::InvariantCulture)
    if ($properties.WinOptimizerVersion -notin @($before, $after)) { throw 'Default version must be the build date.' }
    Assert-VersionProperties $properties $properties.WinOptimizerVersion
    $passed++

    foreach ($version in @('26.01.05', '24.02.29', '00.02.29', '99.12.31')) {
        Assert-VersionProperties (Read-VersionProperties $project $version) $version
        $passed++
    }
}

# Invalid release inputs fail both direct resolution and the real pre-compilation hook.
foreach ($project in @($versionFile, $uiProject)) {
    $target = if ($project -eq $uiProject) { 'PrepareForBuild' } else { 'ValidateWinOptimizerVersion' }
    foreach ($version in @('26.1.05', '2026.01.05', 'v26.01.05', '26.01.05.0', '26.01.05-beta',
            '26.00.05', '26.13.05', '26.04.31', '26.02.29')) {
        $output = & $MSBuildPath $project "-target:$target" "-property:WinOptimizerVersion=$version" `
            -property:Platform=x64 -verbosity:quiet -nologo 2>&1
        if ($LASTEXITCODE -eq 0) { throw "Invalid date was accepted: $version" }
        if (($output -join "`n") -notmatch 'Version.props') { throw "Failure did not come from version validation: $output" }
        $passed++
    }
}

Write-Host "Passed $passed build-version scenarios."
