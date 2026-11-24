# The installer can edit the registry, Explorer registrations and Task Scheduler.
# Run it only in a disposable Windows VM.
function Test-InstallerTestEnvironment([string]$TestDirectory, [switch]$GitHubHostedRunner) {
    if ($GitHubHostedRunner) {
        if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
            $env:RUNNER_OS -cne 'Windows') {
            throw '-GitHubHostedRunner requires a GitHub-hosted Windows runner. No installer was started.'
        }
        return $true
    }
    $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name.Split('\')[-1]
    $guestTests = Join-Path ([IO.Path]::GetTempPath()) 'WinOptimizerInstallerGuest\tests'
    return $account -eq 'WDAGUtilityAccount' -and
        [IO.Path]::GetFullPath($TestDirectory) -eq [IO.Path]::GetFullPath($guestTests) -and
        (Test-Path -LiteralPath 'C:\WinOptimizerTestInput\launch.ps1' -PathType Leaf) -and
        (Test-Path -LiteralPath 'C:\WinOptimizerTestOutput' -PathType Container)
}

function New-InstallerSandboxConfiguration([string]$InputFolder, [string]$OutputFolder,
    [string]$PublishDirectory) {
    $inputPath = [Security.SecurityElement]::Escape($InputFolder)
    $outputPath = [Security.SecurityElement]::Escape($OutputFolder)
    $publishPath = [Security.SecurityElement]::Escape($PublishDirectory)
    return @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <MappedFolders>
    <MappedFolder><HostFolder>$inputPath</HostFolder><SandboxFolder>C:\WinOptimizerTestInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$publishPath</HostFolder><SandboxFolder>C:\WinOptimizerPublish</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$outputPath</HostFolder><SandboxFolder>C:\WinOptimizerTestOutput</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\WinOptimizerTestInput\launch.ps1</Command></LogonCommand>
</Configuration>
"@
}

function Invoke-InstallerSandboxTest([string]$RepoRoot, [string]$InstallerPath, [string]$PublishDirectory) {
    $sandbox = Join-Path ([Environment]::SystemDirectory) 'WindowsSandbox.exe'
    if (-not (Test-Path -LiteralPath $sandbox -PathType Leaf)) {
        throw 'Windows Sandbox is unavailable. The installer test requires a disposable guest. No installer was started.'
    }

    $installer = Get-Item -LiteralPath $InstallerPath
    $publish = Get-Item -LiteralPath $PublishDirectory
    if ($installer.PSIsContainer -or -not $publish.PSIsContainer -or
        -not (Test-Path -LiteralPath (Join-Path $publish.FullName 'win-optimizer.dll') -PathType Leaf)) {
        throw 'Expected an installer file and a win-optimizer publish directory.'
    }
    if ($publish.FullName -notmatch '^[a-zA-Z]:\\.+' -or $publish.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The Windows Sandbox publish directory must be a regular directory on a local drive.'
    }

    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $stageName = 'win-optimizer-installer-sandbox-' + [guid]::NewGuid().ToString('N')
    $stage = [IO.Directory]::CreateDirectory((Join-Path $tempRoot $stageName))
    $launcher = $null
    try {
        $inputFolder = $stage.CreateSubdirectory('input').FullName
        $outputFolder = $stage.CreateSubdirectory('output').FullName
        $sourceFolder = Join-Path $inputFolder 'source'
        foreach ($file in 'tests/packaged-payload.tests.ps1', 'tests/InstallerSandbox.ps1',
            'src/WinOptimizer.UI/Features/Common/Localization') {
            $destination = Join-Path $sourceFolder $file
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            Copy-Item -LiteralPath (Join-Path $RepoRoot $file) -Destination $destination -Recurse
        }
        Copy-Item -LiteralPath $installer.FullName -Destination (Join-Path $inputFolder 'installer.exe')
        $guestLauncher = @'
$ErrorActionPreference = 'Stop'
$output = 'C:\WinOptimizerTestOutput'
$guestRoot = Join-Path ([IO.Path]::GetTempPath()) 'WinOptimizerInstallerGuest'
$status = 'FAIL'
try {
    [void][IO.Directory]::CreateDirectory($guestRoot)
    foreach ($item in (Get-ChildItem -LiteralPath 'C:\WinOptimizerTestInput\source')) {
        Copy-Item -LiteralPath $item.FullName -Destination $guestRoot -Recurse
    }
    Copy-Item -LiteralPath 'C:\WinOptimizerTestInput\installer.exe' -Destination $guestRoot
    & (Join-Path $guestRoot 'tests\packaged-payload.tests.ps1') -InstallerPath (Join-Path $guestRoot 'installer.exe') `
        -PublishDirectory 'C:\WinOptimizerPublish' *> (Join-Path $output 'run.log')
    $status = 'PASS'
}
catch { $_ | Out-String | Add-Content -LiteralPath (Join-Path $output 'run.log') }
finally {
    $pending = Join-Path $output 'result.pending'
    [IO.File]::WriteAllText($pending, $status)
    [IO.File]::Move($pending, (Join-Path $output 'result.txt'))
}
'@
        Set-Content -LiteralPath (Join-Path $inputFolder 'launch.ps1') -Encoding UTF8 -Value $guestLauncher
        $configPath = Join-Path $stage.FullName 'installer.wsb'
        Set-Content -LiteralPath $configPath -Encoding UTF8 -Value (New-InstallerSandboxConfiguration $inputFolder $outputFolder $publish.FullName)
        $launcher = Start-Process -FilePath $sandbox -ArgumentList ('"' + $configPath + '"') -WindowStyle Hidden -PassThru

        $deadline = [Diagnostics.Stopwatch]::StartNew()
        $result = Join-Path $outputFolder 'result.txt'
        while (-not (Test-Path -LiteralPath $result -PathType Leaf) -and $deadline.Elapsed.TotalMinutes -lt 15) {
            Start-Sleep -Milliseconds 500
        }
        $runLog = Join-Path $outputFolder 'run.log'
        if (Test-Path -LiteralPath $runLog -PathType Leaf) {
            $logDirectory = [IO.Directory]::CreateDirectory((Join-Path $RepoRoot 'build/logs'))
            Copy-Item -LiteralPath $runLog -Destination (Join-Path $logDirectory.FullName 'PackagedPayload-Sandbox.log')
            Get-Content -LiteralPath $runLog | Write-Host
        }
        if (-not (Test-Path -LiteralPath $result -PathType Leaf)) {
            throw 'Windows Sandbox did not return an installer test result before the timeout.'
        }
        if ([IO.File]::ReadAllText($result) -ne 'PASS') {
            throw 'The isolated installer test failed. See build/logs/PackagedPayload-Sandbox.log.'
        }
    }
    finally {
        if ($launcher) {
            try {
                if (-not $launcher.HasExited) {
                    [void]$launcher.CloseMainWindow()
                    if (-not $launcher.WaitForExit(5000)) { $launcher.Kill() }
                }
            }
            finally { $launcher.Dispose() }
        }
        if ($stage.Parent.FullName -ne $tempRoot -or $stage.Name -ne $stageName) {
            throw "Unexpected test staging path: $($stage.FullName)"
        }
        $stage.Delete($true)
    }
}
