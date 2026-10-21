# Execute the production workflow scripts with real version validation and a fake GitHub boundary.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$passed = 0

# Read a named step's literal PowerShell block so tests exercise the shipped workflow.
function Read-WorkflowScript {
    param([string]$Workflow, [string]$Step)
    $source = Get-Content -LiteralPath (Join-Path $repoRoot ".github/workflows/$Workflow") -Raw
    $pattern = '(?ms)^      - name: ' + [regex]::Escape($Step) +
        '\r?\n(?:(?!^      - name:).)*?^        run: \|\r?\n(?<body>(?:^          [^\r\n]*(?:\r?\n|$)|^\r?\n)+)'
    $match = [regex]::Match($source, $pattern)
    if (-not $match.Success) { throw "Workflow script not found: $Workflow / $Step" }
    return [scriptblock]::Create(($match.Groups['body'].Value -replace '(?m)^ {10}', ''))
}

$versionScript = Read-WorkflowScript 'build-installer.yml' 'Resolve package version'
$releaseScript = Read-WorkflowScript 'release.yml' 'Download verified installer and publish release'
$savedEnvironment = @{}
foreach ($name in @('PACKAGE_VERSION', 'GITHUB_REF_TYPE', 'GITHUB_REF_NAME', 'GITHUB_OUTPUT',
        'GH_REPO', 'RELEASE_TAG', 'RUN_ID', 'INSTALLER_ARTIFACT')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$resultsRoot = Join-Path $repoRoot 'build/test-results'
$null = New-Item -ItemType Directory -Path $resultsRoot -Force
$outputFile = Join-Path $resultsRoot ("workflow-" + [guid]::NewGuid().ToString('N') + '.tmp')
Push-Location $repoRoot
try {
    # Tag dates override the build date/manual input; MSBuild rejects invalid calendar dates.
    foreach ($scenario in @(
        @{ Type = 'branch'; Ref = 'main'; Input = ''; Expected = ''; Invalid = $false },
        @{ Type = 'branch'; Ref = 'main'; Input = '26.01.05'; Expected = '26.01.05'; Invalid = $false },
        @{ Type = 'tag'; Ref = 'v24.02.29'; Input = '26.01.05'; Expected = '24.02.29'; Invalid = $false },
        @{ Type = 'tag'; Ref = 'v00.02.29'; Input = ''; Expected = '00.02.29'; Invalid = $false },
        @{ Type = 'tag'; Ref = 'v26.1.05'; Input = ''; Expected = ''; Invalid = $true },
        @{ Type = 'tag'; Ref = 'v26.01.05-beta'; Input = ''; Expected = ''; Invalid = $true },
        @{ Type = 'tag'; Ref = 'V26.01.05'; Input = ''; Expected = ''; Invalid = $true },
        @{ Type = 'tag'; Ref = 'v26.02.29'; Input = ''; Expected = ''; Invalid = $true },
        @{ Type = 'tag'; Ref = 'v26.04.31'; Input = ''; Expected = ''; Invalid = $true },
        @{ Type = 'branch'; Ref = 'main'; Input = 'v26.01.05'; Expected = ''; Invalid = $true }
    )) {
        $env:GITHUB_REF_TYPE = $scenario.Type
        $env:GITHUB_REF_NAME = $scenario.Ref
        $env:PACKAGE_VERSION = $scenario.Input
        $env:GITHUB_OUTPUT = $outputFile
        Set-Content -LiteralPath $outputFile -Value '' -NoNewline
        $before = [DateTime]::Now.ToString('yy.MM.dd', [Globalization.CultureInfo]::InvariantCulture)
        $failure = ''
        try { $null = & $versionScript 2>&1 } catch { $failure = $_.Exception.Message }
        $after = [DateTime]::Now.ToString('yy.MM.dd', [Globalization.CultureInfo]::InvariantCulture)
        $output = [IO.File]::ReadAllText($outputFile).Trim()
        if ($scenario.Invalid) {
            if (-not $failure -or $output) { throw "Invalid version published an output: $($scenario.Ref) / $($scenario.Input)" }
        } else {
            if ($failure) { throw $failure }
            $expected = if ($scenario.Expected) { @($scenario.Expected) } else { @($before, $after) }
            if ($output -cnotin @($expected | ForEach-Object { "version=$_" })) {
                throw "Unexpected workflow version: $output"
            }
        }
        $passed++
    }

    $env:GH_REPO = 'example/win-optimizer'
    $env:RELEASE_TAG = 'v26.01.05'
    $env:RUN_ID = '123'
    $env:INSTALLER_ARTIFACT = 'win-optimizer-windows-x64-v26.01.05'
    foreach ($failedCall in @(0, 1, 2)) {
        $state = @{ Calls = [Collections.Generic.List[object]]::new(); FailedCall = $failedCall }
        $failure = & {
            param($flow, $state)
            # Only the external CLI is faked; command ordering and error handling are production code.
            function gh {
                $state.Calls.Add(@($args))
                $global:LASTEXITCODE = if ($state.Calls.Count -eq $state.FailedCall) { 1 } else { 0 }
            }
            try { & $flow; return '' } catch { return $_.Exception.Message }
        } $releaseScript $state
        $expectedCalls = if ($failedCall -eq 1) { 1 } else { 2 }
        if ($state.Calls.Count -ne $expectedCalls -or ([bool]$failure -ne ($failedCall -ne 0))) {
            throw 'Release did not stop at the failed GitHub operation.'
        }
        $download = @('run', 'download', '123', '--repo', 'example/win-optimizer', '--name',
            'win-optimizer-windows-x64-v26.01.05', '--dir', 'build/installers')
        if (Compare-Object $download $state.Calls[0] -SyncWindow 0) { throw 'Download must select this run and versioned installer artifact.' }
        if ($state.Calls.Count -eq 2) {
            $publish = @('release', 'create', 'v26.01.05', 'build/installers/win-optimizer.Installer.exe',
                '--repo', 'example/win-optimizer', '--verify-tag', '--generate-notes', '--title', 'win-optimizer v26.01.05')
            if (Compare-Object $publish $state.Calls[1] -SyncWindow 0) { throw 'Release tag, asset or publication options changed.' }
        }
        $passed++
    }
} finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
    if (Test-Path -LiteralPath $outputFile) { Remove-Item -LiteralPath $outputFile -Force }
}

Write-Host "Passed $passed workflow version and release scenarios."
