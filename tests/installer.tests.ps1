# Exercises production wizard callbacks and isolated installer/update/uninstaller payloads.
[CmdletBinding()]
param([string]$InnoCompiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Test-Path -LiteralPath $InnoCompiler -PathType Leaf)) {
    throw 'Inno Setup 6 is required. Pass its ISCC.exe path with -InnoCompiler.'
}
# Junction regressions need a local Windows filesystem even when the checkout is on a share.
$testParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'win-optimizer-installer-tests'))
$testRoot = Join-Path $testParent ([guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force
$testAppId = [guid]::NewGuid().ToString()
$testExtension = '.winoptimizer-test-' + $testAppId
$dataParent = Join-Path $testRoot 'Storage'
$dataRoot = Join-Path $dataParent 'win-optimizer'
$traceRegistry = 'Software\WinOptimizerInstallerTests\' + $testAppId
$prefetchRoot = Join-Path $testRoot 'Prefetch'
$taskFolderName = 'WinOptimizerInstallerTests-' + $testAppId
$profileKey = $traceRegistry + '\Profiles'
$testSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$scheduler = New-Object -ComObject 'Schedule.Service'
$scheduler.Connect()
$savedData = @(
    'Users/S-1-5-21-1001/Config/UserPreferences.json',
    'Users/S-1-5-21-1002/Backups/Configurations/saved.win-optimizer',
    'Backups/Cleaner/recovery.dat', 'History/ChangeHistory.jsonl',
    'Cache/Icons/cached.png', 'Tools/tool.exe', 'Scripts/saved.ps1'
)

# Seed both account data and shared recovery/cache files at the fixture's storage boundary.
function New-SavedDataFixture {
    foreach ($relative in $savedData) {
        $path = Join-Path $dataRoot $relative
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force
        Set-Content -LiteralPath $path -Value 'saved fixture data'
    }
}

# Each trace has unrelated neighbours and near matches, all under unique test boundaries.
function New-TraceFixture {
    param([string]$Installation, [switch]$EmptyTasks, [switch]$OtherTasks)
    $executable = Join-Path $Installation 'win-optimizer.exe'
    $script:traceValues = @()
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            foreach ($container in @(
                'Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched',
                'Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\ShowJumpView',
                'Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store',
                'Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Persisted',
                'Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
            )) {
                $keyPath = "$traceRegistry\$container"
                $key = $hive.CreateSubKey($keyPath)
                try {
                    foreach ($value in @(
                        @{ Name = $executable; Remove = $true },
                        @{ Name = '{6D809377-6AF0-444B-8957-A3773F02200E}' + $executable.Substring($testRoot.Length); Remove = $true },
                        @{ Name = "$executable.bak"; Remove = $false },
                        @{ Name = "D:\Other copy\win-optimizer.exe"; Remove = $false },
                        @{ Name = "D:\source\WinOptimizer.Startup.Tests.exe"; Remove = $false }
                    )) {
                        $key.SetValue($value.Name, [byte[]]@(1, 2, 3), [Microsoft.Win32.RegistryValueKind]::Binary)
                        $script:traceValues += @{ View = $view; Key = $keyPath; Name = $value.Name; Remove = $value.Remove }
                    }
                }
                finally { $key.Dispose() }
            }
            $shortcutKeyPath = "$traceRegistry\Microsoft\Windows\CurrentVersion\UFH\SHC"
            $key = $hive.CreateSubKey($shortcutKeyPath)
            try {
                $key.SetValue('0', [string[]]@('C:\Start menu\win-optimizer.lnk', $executable), [Microsoft.Win32.RegistryValueKind]::MultiString)
                $key.SetValue('1', [string[]]@('C:\Start menu\other.lnk', "$executable.bak"), [Microsoft.Win32.RegistryValueKind]::MultiString)
                $script:traceValues += @{ View = $view; Key = $shortcutKeyPath; Name = '0'; Remove = $true }
                $script:traceValues += @{ View = $view; Key = $shortcutKeyPath; Name = '1'; Remove = $false }
            }
            finally { $key.Dispose() }
            $key = $hive.CreateSubKey("$profileKey\$testSid")
            $key.Dispose()
            $key = $hive.CreateSubKey("$profileKey\$testSid.bak")
            $key.SetValue('ProfileImagePath', 'invalid backup registration')
            $key.Dispose()
        }
        finally { $hive.Dispose() }
    }
    $null = New-Item -ItemType Directory -Path $prefetchRoot -Force
    foreach ($name in @('WIN-OPTIMIZER.EXE-0D5A5407.pf', 'win-optimizer.exe-1234abcd.pf',
        'WIN-OPTIMIZER.EXE-ZZZZZZZZ.pf', 'WIN-OPTIMIZER.EXE-12345678.pf.bak', 'OTHER.EXE-12345678.pf')) {
        Set-Content -LiteralPath (Join-Path $prefetchRoot $name) -Value 'synthetic prefetch'
    }
    $root = $scheduler.GetFolder('\')
    $folder = $root.CreateFolder($taskFolderName)
    if (-not $EmptyTasks) {
        $paths = @{ Owned = $executable }
        if ($OtherTasks) { $paths.Other = 'D:\Other copy\win-optimizer.exe' }
        foreach ($entry in $paths.GetEnumerator()) {
            $definition = $scheduler.NewTask(0)
            $definition.Settings.Enabled = $false
            $definition.Settings.Hidden = $true
            $definition.Principal.LogonType = 3
            $action = $definition.Actions.Create(0)
            $action.Path = $entry.Value
            $null = $folder.RegisterTaskDefinition($entry.Key, $definition, 6, $testSid, $null, 3)
        }
        if ($OtherTasks) {
            $definition = $scheduler.NewTask(0)
            $definition.Settings.Enabled = $false
            $definition.Principal.LogonType = 3
            foreach ($path in @($executable, 'D:\Other copy\other.exe')) {
                $definition.Actions.Create(0).Path = $path
            }
            $null = $folder.RegisterTaskDefinition('Mixed', $definition, 6, $testSid, $null, 3)
        }
    }
}

# Assert the resource-level result, including preserved bytes for all unrelated values.
function Assert-TraceFixture {
    param([bool]$Removed, [switch]$OtherTasks, [switch]$EmptyTasks)
    foreach ($value in $traceValues) {
        $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $value.View)
        try {
            $key = $hive.OpenSubKey($value.Key)
            if (-not $key) { throw 'Cleanup removed a shared registry container.' }
            try {
                $present = $key.GetValueNames() -contains $value.Name
                if ($present -eq ($Removed -and $value.Remove)) {
                    throw "Wrong registry trace result: $($value.Key) / $($value.Name)"
                }
                if ($present) {
                    $expected = if ($value.Name -eq '0') { @('C:\Start menu\win-optimizer.lnk', (Join-Path $installedRoot 'win-optimizer.exe')) }
                        elseif ($value.Name -eq '1') { @('C:\Start menu\other.lnk', ((Join-Path $installedRoot 'win-optimizer.exe') + '.bak')) }
                        else { @(1, 2, 3) }
                    if (Compare-Object @($key.GetValue($value.Name)) $expected -SyncWindow 0) {
                        throw 'Cleanup changed an unrelated registry value.'
                    }
                }
            }
            finally { $key.Dispose() }
        }
        finally { $hive.Dispose() }
    }
    foreach ($name in @('WIN-OPTIMIZER.EXE-0D5A5407.pf', 'win-optimizer.exe-1234abcd.pf')) {
        if ((Test-Path -LiteralPath (Join-Path $prefetchRoot $name)) -eq $Removed) {
            throw "Wrong application Prefetch result: $name"
        }
    }
    foreach ($name in @('WIN-OPTIMIZER.EXE-ZZZZZZZZ.pf', 'WIN-OPTIMIZER.EXE-12345678.pf.bak', 'OTHER.EXE-12345678.pf')) {
        if ((Get-Content -LiteralPath (Join-Path $prefetchRoot $name) -Raw).Trim() -ne 'synthetic prefetch') {
            throw 'Cleanup changed an unrelated Prefetch file.'
        }
    }
    $folder = @($scheduler.GetFolder('\').GetFolders(0) | Where-Object Name -eq $taskFolderName)
    if ($Removed -and -not $OtherTasks) {
        if ($folder.Count) { throw 'Cleanup left an empty Task Scheduler folder.' }
    }
    else {
        if ($folder.Count -ne 1) { throw 'Cleanup removed a retained task folder.' }
        $names = @($folder[0].GetTasks(1) | ForEach-Object Name)
        if ((-not $EmptyTasks -and ($names -contains 'Owned') -eq $Removed) -or
            ($OtherTasks -and ($names -notcontains 'Other' -or $names -notcontains 'Mixed'))) {
            throw 'Cleanup did not preserve unrelated tasks or remove the application startup task.'
        }
    }
}

# Remove only this run's synthetic scheduler namespace, including after failed assertions.
function Remove-TaskFixture {
    if ($taskFolderName -ne ('WinOptimizerInstallerTests-' + $testAppId)) { throw 'Unsafe task cleanup identity.' }
    $root = $scheduler.GetFolder('\')
    foreach ($folder in $root.GetFolders(0)) {
        if ($folder.Name -eq $taskFolderName) {
            foreach ($task in $folder.GetTasks(1)) { $folder.DeleteTask($task.Name, 0) }
            $root.DeleteFolder($taskFolderName, 0)
            break
        }
    }
}

# Install the real production fixture into one reusable location and verify its uninstaller.
function Install-RegularFixture {
    param([string]$Destination = $regularRoot)
    $process = Start-Process -FilePath (Join-Path $testRoot 'win-optimizer.Installer.exe') `
        -ArgumentList @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            '/TASKS=regularinstall', "/DIR=`"$Destination`"") -WindowStyle Hidden -Wait -PassThru
    $executables = @(Get-ChildItem -LiteralPath $Destination -Filter 'unins*.exe')
    if ($process.ExitCode -ne 0 -or $executables.Count -ne 1) {
        throw 'The regular fixture did not install an uninstaller.'
    }
    foreach ($relative in $savedData) {
        if ((Get-Content -LiteralPath (Join-Path $dataRoot $relative) -Raw).Trim() -ne 'saved fixture data') {
            throw "Reinstallation changed existing saved data: $relative"
        }
    }
    return $executables[0].FullName
}

try {
    # Installer checks run before the build, including on fresh checkouts without build/logs.
    $logsRoot = Join-Path $repoRoot 'build/logs'
    $null = New-Item -ItemType Directory -Path $logsRoot -Force
    $installerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'extras/win-optimizer.Installer.iss') -Raw
    $code = [regex]::Match($installerSource, '(?ms)^\[Code\]\r?\n(.*?)(?=^\[Tasks\])').Groups[1].Value
    $tasks = [regex]::Match($installerSource, '(?ms)^\[Tasks\]\r?\n(.*?)(?=^\[Files\])').Groups[1].Value
    if (-not $code -or -not $tasks) { throw 'Production installer code/tasks were not found.' }

    # Compile every production section with only the input/output paths redirected.
    $payloadRoot = Join-Path $testRoot 'payload'
    $null = New-Item -ItemType Directory -Path $payloadRoot
    Set-Content -LiteralPath (Join-Path $payloadRoot 'win-optimizer.exe') -Value 'isolated payload'
    $fixtureVersion = '26.01.05'
    $compileSource = $installerSource.Replace('#define MyAppVersion ""', "#define MyAppVersion `"$fixtureVersion`"")
    $compileSource = $compileSource.Replace('C:\win-optimizer\build\installers', $testRoot)
    $compileSource = $compileSource.Replace('C:\win-optimizer\build\publish', $payloadRoot)
    $compileSource = $compileSource.Replace('C:\win-optimizer', $repoRoot)
    # Redirect the data boundary too: no test may address the real ProgramData store.
    $compileSource = $compileSource.Replace('{commonappdata}\win-optimizer', $dataRoot)
    # Redirect every cleanup boundary; the real Windows traces/profiles remain untouched.
    $compileSource = $compileSource.Replace('Software\Microsoft\', "$traceRegistry\Microsoft\")
    $compileSource = $compileSource.Replace('SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList', $profileKey)
    $compileSource = $compileSource.Replace('HKLM64, ProfilesKey', 'HKCU64, ProfilesKey')
    $compileSource = $compileSource.Replace("'win-optimizer'", "'$taskFolderName'")
    $compileSource = $compileSource.Replace('{win}\Prefetch', $prefetchRoot)
    $compileSource = $compileSource.Replace('{pf64}', $testRoot)
    # Only the fixture's registration identity changes; every install/uninstall entry remains real.
    $compileSource = $compileSource.Replace('51DB5BCA-8548-51D2-AD65-B6527C62A739', $testAppId)
    $compileSource = $compileSource.Replace('#define MyAppAssocExt ".win-optimizer"', "#define MyAppAssocExt `"$testExtension`"")
    $compilePath = Join-Path $testRoot 'production.iss'
    Set-Content -LiteralPath $compilePath -Value $compileSource -Encoding UTF8
    & $InnoCompiler /Q $compilePath
    if ($LASTEXITCODE -ne 0) { throw "Production installer compilation failed: $LASTEXITCODE" }

    # Exercise production metadata with leading zeros in the release date.
    $installerMetadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $testRoot 'win-optimizer.Installer.exe'))
    if ($installerMetadata.ProductVersion.Trim() -cne $fixtureVersion -or
        $installerMetadata.FileVersion.Trim() -cne $fixtureVersion) {
        throw 'Installer text versions must match the padded application release date.'
    }
    foreach ($kind in @('File', 'Product')) {
        $numericVersion = [version]::new($installerMetadata."${kind}MajorPart", $installerMetadata."${kind}MinorPart",
            $installerMetadata."${kind}BuildPart", $installerMetadata."${kind}PrivatePart")
        if ($numericVersion -ne [version]'26.1.5.0') { throw "Unexpected installer $kind version: $numericVersion" }
    }

    # Run the actual portable install path into an isolated folder, with no registration.
    $portableRoot = Join-Path $testRoot 'Portable app'
    $null = New-Item -ItemType Directory -Path $portableRoot
    $sentinelPath = Join-Path $portableRoot 'existing.txt'
    Set-Content -LiteralPath $sentinelPath -Value 'keep'
    $portableProcess = Start-Process -FilePath (Join-Path $testRoot 'win-optimizer.Installer.exe') `
        -ArgumentList @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            '/TASKS=portableinstall', "/DIR=`"$portableRoot`"", "/LOG=`"$testRoot\portable.log`"") `
        -WindowStyle Hidden -Wait -PassThru
    if ($portableProcess.ExitCode -ne 0 -or
        -not (Test-Path -LiteralPath (Join-Path $portableRoot 'win-optimizer.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $portableRoot 'portable.marker')) -or
        -not (Test-Path -LiteralPath $sentinelPath) -or
        (Get-ChildItem -LiteralPath $portableRoot -Filter 'unins*.exe')) {
        throw "Portable installation smoke check failed ($($portableProcess.ExitCode))."
    }

    # A normal update replaces application payloads while retaining untracked user files.
    $scriptsRoot = Join-Path $portableRoot 'Scripts'
    $unrelatedRoot = Join-Path $portableRoot 'Other program'
    $null = New-Item -ItemType Directory -Path $scriptsRoot, $unrelatedRoot
    Set-Content -LiteralPath (Join-Path $scriptsRoot 'keep.ps1') -Value 'user script'
    Set-Content -LiteralPath (Join-Path $unrelatedRoot 'keep.dll') -Value 'unrelated binary'
    Set-Content -LiteralPath (Join-Path $portableRoot 'win-optimizer.exe') -Value 'old payload'
    $updateProcess = Start-Process -FilePath (Join-Path $testRoot 'win-optimizer.Installer.exe') `
        -ArgumentList @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            '/TASKS=portableinstall', "/DIR=`"$portableRoot`"") -WindowStyle Hidden -Wait -PassThru
    if ($updateProcess.ExitCode -ne 0 -or
        (Get-Content -LiteralPath (Join-Path $portableRoot 'win-optimizer.exe') -Raw).Trim() -ne 'isolated payload' -or
        -not (Test-Path -LiteralPath $sentinelPath) -or
        -not (Test-Path -LiteralPath (Join-Path $scriptsRoot 'keep.ps1')) -or
        -not (Test-Path -LiteralPath (Join-Path $unrelatedRoot 'keep.dll'))) {
        throw 'The portable update did not preserve untracked files and replace the application.'
    }
    $wrongRegularMode = Start-Process -FilePath (Join-Path $testRoot 'win-optimizer.Installer.exe') `
        -ArgumentList @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            '/TASKS=regularinstall', "/DIR=`"$portableRoot`"") -WindowStyle Hidden -Wait -PassThru
    if ($wrongRegularMode.ExitCode -eq 0 -or
        -not (Test-Path -LiteralPath (Join-Path $portableRoot 'portable.marker')) -or
        (Get-ChildItem -LiteralPath $portableRoot -Filter 'unins*.exe')) {
        throw 'An in-place mode change must not convert or register the portable installation.'
    }

    # Register a unique synthetic application, then exercise its real uninstaller.
    $regularRoot = Join-Path $testRoot 'Regular app'
    $null = New-Item -ItemType Directory -Path $regularRoot
    $regularSentinel = Join-Path $regularRoot 'keep.json'
    Set-Content -LiteralPath $regularSentinel -Value '{"user":"data"}'
    $regularProcess = Start-Process -FilePath (Join-Path $testRoot 'win-optimizer.Installer.exe') `
        -ArgumentList @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            '/TASKS=regularinstall', "/DIR=`"$regularRoot`"") -WindowStyle Hidden -Wait -PassThru
    $uninstaller = @(Get-ChildItem -LiteralPath $regularRoot -Filter 'unins*.exe')
    if ($regularProcess.ExitCode -ne 0 -or $uninstaller.Count -ne 1 -or
        (Test-Path -LiteralPath (Join-Path $regularRoot 'portable.marker'))) {
        throw 'The regular fixture was not registered with an uninstaller.'
    }
    $wrongPortableMode = Start-Process -FilePath (Join-Path $testRoot 'win-optimizer.Installer.exe') `
        -ArgumentList @('/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            '/TASKS=portableinstall', "/DIR=`"$regularRoot`"") -WindowStyle Hidden -Wait -PassThru
    if ($wrongPortableMode.ExitCode -eq 0 -or -not (Test-Path -LiteralPath $uninstaller[0].FullName) -or
        (Test-Path -LiteralPath (Join-Path $regularRoot 'portable.marker'))) {
        throw 'An in-place mode change must preserve the registered installation and its uninstaller.'
    }
    New-SavedDataFixture
    $installedRoot = $regularRoot
    New-TraceFixture -Installation $installedRoot
    $uninstallProcess = Start-Process -FilePath $uninstaller[0].FullName `
        -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if ($uninstallProcess.ExitCode -ne 0 -or
        (Test-Path -LiteralPath (Join-Path $regularRoot 'win-optimizer.exe')) -or
        -not (Test-Path -LiteralPath $regularSentinel)) {
        throw 'Uninstallation must remove tracked application files and preserve untracked user files.'
    }
    foreach ($relative in $savedData) {
        if ((Get-Content -LiteralPath (Join-Path $dataRoot $relative) -Raw).Trim() -ne 'saved fixture data') {
            throw 'The default silent uninstall must preserve application data for reinstallation.'
        }
    }
    Assert-TraceFixture -Removed $false
    Remove-TaskFixture

    $outsideRoot = Join-Path $testRoot 'Other application data'
    $null = New-Item -ItemType Directory -Path (Join-Path $outsideRoot 'win-optimizer') -Force
    $outsideSentinel = Join-Path $outsideRoot 'win-optimizer/keep.json'
    Set-Content -LiteralPath $outsideSentinel -Value 'outside storage boundary'
    $uninstallScenariosPassed = 0
    foreach ($scenario in @(
        @{ Name = 'keep'; Policy = 'keep'; Shape = 'normal'; Error = '' },
        @{ Name = 'remove'; Policy = 'remove'; Shape = 'normal'; Error = '' },
        @{ Name = 'missing'; Policy = 'remove'; Shape = 'missing'; Error = '' },
        @{ Name = 'empty-tasks'; Policy = 'remove'; Shape = 'normal'; EmptyTasks = $true; Error = '' },
        @{ Name = 'other-tasks'; Policy = 'remove'; Shape = 'normal'; OtherTasks = $true; Error = '' },
        @{ Name = 'offline-missing'; Policy = 'remove'; Shape = 'offline-missing'; Error = '' },
        @{ Name = 'offline-invalid'; Policy = 'remove'; Shape = 'offline-invalid'; Error = 'Cannot load or release a Windows profile hive' },
        @{ Name = 'offline-linked'; Policy = 'remove'; Shape = 'offline-linked'; Error = 'linked application-data folder' },
        @{ Name = 'prefetch-linked'; Policy = 'remove'; Shape = 'prefetch-linked'; Error = 'linked application-data folder' },
        @{ Name = 'prefetch-locked'; Policy = 'remove'; Shape = 'prefetch-locked'; Error = 'Cannot remove application Prefetch file' },
        @{ Name = 'registry-denied'; Policy = 'remove'; Shape = 'registry-denied'; Error = 'Cannot remove registry value' },
        @{ Name = 'invalid-policy'; Policy = 'invalid'; Shape = 'normal'; Error = 'Invalid uninstall data policy' },
        @{ Name = 'file-root'; Policy = 'remove'; Shape = 'file'; Error = 'not a directory' },
        @{ Name = 'overlap'; Policy = 'remove'; Shape = 'overlap'; Error = 'application is installed inside its data folder' },
        @{ Name = 'root-link'; Policy = 'remove'; Shape = 'root-link'; Error = 'linked application-data folder' },
        @{ Name = 'ancestor-link'; Policy = 'remove'; Shape = 'ancestor-link'; Error = 'linked application-data folder' },
        @{ Name = 'child-link'; Policy = 'remove'; Shape = 'child-link'; Error = '' },
        @{ Name = 'locked'; Policy = 'remove'; Shape = 'locked'; Error = 'Some application data could not be removed' }
    )) {
        # All reset/deletion targets are fixed descendants of this run's unique fixture.
        if (-not ([IO.Path]::GetFullPath($dataParent)).StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe data-fixture cleanup path.' }
        if (Test-Path -LiteralPath $dataParent) { [IO.Directory]::Delete($dataParent, $true) }
        New-SavedDataFixture
        $installedRoot = if ($scenario.Shape -eq 'overlap') { $dataRoot } else { $regularRoot }
        $uninstallerPath = Install-RegularFixture -Destination $installedRoot
        New-TraceFixture -Installation $installedRoot -EmptyTasks:([bool]$scenario.EmptyTasks) -OtherTasks:([bool]$scenario.OtherTasks)
        $lockedFile = $null
        $deniedKey = $null
        $deniedRule = $null
        $offlineProfile = Join-Path $testRoot 'Offline profile'
        $offlineSid = 'S-1-5-21-1000000001-1000000002-1000000003-1001'
        try {
            switch ($scenario.Shape) {
                'missing' { [IO.Directory]::Delete($dataRoot, $true) }
                'file' {
                    [IO.Directory]::Delete($dataRoot, $true)
                    Set-Content -LiteralPath $dataRoot -Value 'not a directory'
                }
                'root-link' {
                    [IO.Directory]::Delete($dataRoot, $true)
                    $null = New-Item -ItemType Junction -Path $dataRoot -Target $outsideRoot
                }
                'ancestor-link' {
                    [IO.Directory]::Delete($dataParent, $true)
                    $null = New-Item -ItemType Junction -Path $dataParent -Target $outsideRoot
                }
                'child-link' {
                    $null = New-Item -ItemType Junction -Path (Join-Path $dataRoot 'linked-cache') -Target $outsideRoot
                }
                'locked' {
                    $lockedFile = [IO.File]::Open((Join-Path $dataRoot $savedData[0]),
                        [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                }
                'prefetch-locked' {
                    $lockedFile = [IO.File]::Open((Join-Path $prefetchRoot 'WIN-OPTIMIZER.EXE-0D5A5407.pf'),
                        [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                }
                'prefetch-linked' {
                    [IO.Directory]::Delete($prefetchRoot, $true)
                    $null = New-Item -ItemType Junction -Path $prefetchRoot -Target $outsideRoot
                }
                'registry-denied' {
                    $deniedKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
                        "$traceRegistry\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched",
                        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
                        [Security.AccessControl.RegistryRights]::ChangePermissions)
                    $acl = $deniedKey.GetAccessControl()
                    $deniedRule = [Security.AccessControl.RegistryAccessRule]::new([Security.Principal.SecurityIdentifier]::new($testSid),
                        [Security.AccessControl.RegistryRights]::SetValue, [Security.AccessControl.AccessControlType]::Deny)
                    $acl.AddAccessRule($deniedRule)
                    $deniedKey.SetAccessControl($acl)
                }
            }
            if ($scenario.Shape -like 'offline-*') {
                if ($scenario.Shape -eq 'offline-linked') {
                    $null = New-Item -ItemType Junction -Path $offlineProfile -Target $outsideRoot
                }
                else {
                    $null = New-Item -ItemType Directory -Path $offlineProfile -Force
                    if ($scenario.Shape -eq 'offline-invalid') {
                        Set-Content -LiteralPath (Join-Path $offlineProfile 'NTUSER.DAT') -Value 'not a registry hive'
                    }
                }
                $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$profileKey\$offlineSid")
                try { $key.SetValue('ProfileImagePath', $offlineProfile) }
                finally { $key.Dispose() }
            }
            $logPath = Join-Path $testRoot ("uninstall-$($scenario.Name).log")
            $process = Start-Process -FilePath $uninstallerPath `
                -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
                    "/DATA=$($scenario.Policy)", "/LOG=`"$logPath`"") -WindowStyle Hidden -Wait -PassThru
            $log = Get-Content -LiteralPath $logPath -Raw
            Copy-Item -LiteralPath $logPath -Destination (Join-Path $logsRoot "uninstaller-$($scenario.Name).log") -Force
            $payloadExists = Test-Path -LiteralPath (Join-Path $installedRoot 'win-optimizer.exe')
            if ($scenario.Error) {
                if ($process.ExitCode -eq 0 -or -not $payloadExists -or $log -notmatch $scenario.Error) {
                    throw "Uninstall must stop before payload removal: $($scenario.Name) ($($process.ExitCode)).`n$log"
                }
                if ($scenario.Shape -in @('normal', 'overlap')) {
                    foreach ($relative in $savedData) {
                        if ((Get-Content -LiteralPath (Join-Path $dataRoot $relative) -Raw).Trim() -ne 'saved fixture data') {
                            throw 'Aborted uninstallation changed saved application data.'
                        }
                    }
                }
                if ($scenario.Shape -eq 'locked' -and -not (Test-Path -LiteralPath (Join-Path $dataRoot $savedData[0]))) {
                    throw 'Cleanup failed without retaining the locked file for a retry.'
                }
            }
            elseif ($process.ExitCode -ne 0 -or $payloadExists) {
                throw "Uninstall scenario failed: $($scenario.Name) ($($process.ExitCode)).`n$log"
            }
            elseif ($scenario.Policy -eq 'keep') {
                foreach ($relative in $savedData) {
                    if ((Get-Content -LiteralPath (Join-Path $dataRoot $relative) -Raw).Trim() -ne 'saved fixture data') {
                        throw "Saved data was not preserved: $relative"
                    }
                }
            }
            elseif (Test-Path -LiteralPath $dataRoot) { throw 'Full cleanup left application data behind.' }
            if (-not $scenario.Error) {
                Assert-TraceFixture -Removed ($scenario.Policy -eq 'remove') -OtherTasks:([bool]$scenario.OtherTasks) -EmptyTasks:([bool]$scenario.EmptyTasks)
            }
            elseif ($scenario.Shape -in @('normal', 'file', 'overlap', 'root-link', 'ancestor-link')) {
                Assert-TraceFixture -Removed $false
            }
            if ((Get-Content -LiteralPath $outsideSentinel -Raw).Trim() -ne 'outside storage boundary' -or
                -not (Test-Path -LiteralPath $regularSentinel)) {
                throw 'Uninstallation crossed the storage boundary or removed an untracked file.'
            }
            $uninstallScenariosPassed++
        }
        finally {
            Remove-TaskFixture
            if ($lockedFile) { $lockedFile.Dispose() }
            if ($deniedKey) {
                try {
                    if ($deniedRule) {
                        $acl = $deniedKey.GetAccessControl()
                        $acl.RemoveAccessRuleSpecific($deniedRule)
                        $deniedKey.SetAccessControl($acl)
                    }
                }
                finally { $deniedKey.Dispose() }
            }
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree("$profileKey\$offlineSid", $false)
            if ($scenario.Shape -eq 'prefetch-linked') { [IO.Directory]::Delete($prefetchRoot) }
            # Each offline scenario owns its directory, including any failed hive-load files.
            # Directory.Delete removes a junction itself without traversing its target.
            if ($scenario.Shape -like 'offline-*' -and (Test-Path -LiteralPath $offlineProfile)) {
                [IO.Directory]::Delete($offlineProfile, $true)
            }
            if ($scenario.Shape -eq 'root-link' -and (Test-Path -LiteralPath $dataRoot)) { [IO.Directory]::Delete($dataRoot) }
            if ($scenario.Shape -eq 'ancestor-link' -and (Test-Path -LiteralPath $dataParent)) { [IO.Directory]::Delete($dataParent) }
            if (Test-Path -LiteralPath (Join-Path $installedRoot 'win-optimizer.exe')) {
                $repair = Start-Process -FilePath $uninstallerPath `
                    -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DATA=keep') `
                    -WindowStyle Hidden -Wait -PassThru
                if ($repair.ExitCode -ne 0) { throw 'Cannot clean the failed uninstaller fixture.' }
            }
        }
    }

    # Use the same callbacks and task definitions, with checks before installation starts.
    $checks = @'
procedure AssertEqual(Expected, Actual, Context: String);
begin
  if CompareText(Expected, Actual) <> 0 then
    RaiseException(Context + ': expected ' + Expected + ', got ' + Actual);
end;

<event('NextButtonClick')>
function CheckInstallerFlow(CurPageID: Integer): Boolean;
var
  InitialDir, RegularDir, PortableDir: String;
begin
  Result := True;
  if CurPageID = wpSelectTasks then
  begin
    NextButtonClick(wpSelectTasks);
    InitialDir := CustomDirPage.Values[0];
    if ExpandConstant('{param:DIR|}') <> '' then
      AssertEqual(ExpandConstant('{param:DIR}'), InitialDir, 'Explicit destination')
    else if WizardIsTaskSelected('portableinstall') then
      AssertEqual(ExpandConstant('{userdesktop}\win-optimizer'), InitialDir, 'Portable default')
    else
      AssertEqual(RegularInstallDir, InitialDir, 'Resolved regular default');

    // A user-entered folder must survive Next and Back without an appended name.
    RegularDir := ExpandConstant('{param:TestRoot}\My regular folder');
    PortableDir := ExpandConstant('{param:TestRoot}\My portable folder');
    WizardSelectTasks('regularinstall');
    NextButtonClick(wpSelectTasks);
    CustomDirPage.Values[0] := RegularDir;
    NextButtonClick(CustomDirPage.ID);
    AssertEqual(RegularDir, WizardForm.DirEdit.Text, 'Exact selected folder');
    NextButtonClick(wpSelectTasks);
    AssertEqual(RegularDir, CustomDirPage.Values[0], 'Back with same mode');

    WizardSelectTasks('portableinstall');
    NextButtonClick(wpSelectTasks);
    CustomDirPage.Values[0] := PortableDir;
    NextButtonClick(CustomDirPage.ID);
    AssertEqual(PortableDir, WizardForm.DirEdit.Text, 'Portable selected folder');
    if WizardIsTaskSelected('regularinstall') then
      RaiseException('Installation modes must be exclusive');
    WizardSelectTasks('regularinstall');
    NextButtonClick(wpSelectTasks);
    AssertEqual(RegularDir, CustomDirPage.Values[0], 'Restore regular folder');
    WizardSelectTasks('portableinstall');
    NextButtonClick(wpSelectTasks);
    AssertEqual(PortableDir, CustomDirPage.Values[0], 'Restore portable folder');

    WizardForm.DirEdit.Text := InitialDir;
    CustomDirPage.Values[0] := InitialDir;
    Log('INSTALLER FLOW CHECKS PASSED');
  end;

  // Abort before installation starts or any application/registry writes.
  if CurPageID = wpReady then
    Result := False;
end;
'@
    $harness = @"
#define MyAppName "win-optimizer"
#define MyAppExeName "win-optimizer.exe"
#define MyAppDataRoot "$dataRoot"
[Setup]
AppName=win-optimizer installer checks
AppVersion=1.0.0
DefaultDirName={tmp}\win-optimizer
PrivilegesRequired=lowest
Uninstallable=no
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=$testRoot
OutputBaseFilename=InstallerFlow.Tests
[Code]
$code
$checks
[Tasks]
$tasks
"@
    $harnessPath = Join-Path $testRoot 'flow.iss'
    Set-Content -LiteralPath $harnessPath -Value $harness -Encoding UTF8
    & $InnoCompiler /Q $harnessPath
    if ($LASTEXITCODE -ne 0) { throw "Installer flow compilation failed: $LASTEXITCODE" }

    $passed = 6 + $uninstallScenariosPassed
    $scenarios = @(
        @{ Tasks = '/TASKS=regularinstall'; ExplicitDir = $false },
        @{ Tasks = '/TASKS=regularinstall'; ExplicitDir = $true },
        @{ Tasks = '/TASKS=portableinstall'; ExplicitDir = $false },
        @{ Tasks = '/TASKS=portableinstall'; ExplicitDir = $true },
        @{ Tasks = '/MERGETASKS=regularinstall\desktopicon,regularinstall\startmenuicon'; ExplicitDir = $true },
        @{ Tasks = '/MERGETASKS=portableinstall'; ExplicitDir = $true }
    )
    foreach ($scenario in $scenarios) {
        $scenarioRoot = Join-Path $testRoot "scenario-$passed"
        $null = New-Item -ItemType Directory -Path $scenarioRoot
        $logPath = Join-Path $scenarioRoot 'setup.log'
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            $scenario.Tasks, "/TestRoot=`"$scenarioRoot`"", "/LOG=`"$logPath`"")
        if ($scenario.ExplicitDir) { $arguments += "/DIR=`"$scenarioRoot\Custom app location`"" }
        $process = Start-Process -FilePath (Join-Path $testRoot 'InstallerFlow.Tests.exe') `
            -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        $log = Get-Content -LiteralPath $logPath -Raw
        if ($process.ExitCode -ne 1 -or $log -notmatch 'INSTALLER FLOW CHECKS PASSED' -or
            $log -match 'Exception message:') {
            throw "Installer scenario $($scenario.Tasks) (explicit DIR: $($scenario.ExplicitDir)) failed ($($process.ExitCode)):`n$log"
        }
        $passed++
    }

    # Exercise the launcher's real orchestration with fake download/process boundaries.
    $tokens = $null; $parseErrors = $null
    $launcherAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repoRoot 'win-optimizer.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    $launcherFlow = $launcherAst.EndBlock.Statements |
        Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] }
    $launcherBlock = [scriptblock]::Create($launcherFlow.Extent.Text)
    foreach ($exitCode in @(0, 2, 1)) {
        $state = @{ Downloads = 0; Starts = 0; Messages = @(); ExitCode = $exitCode }
        $result = & {
            param($flow, $state)
            function Get-FileFromWeb { param($URL, $File) $state.Downloads++ }
            function Write-Host { param($Object, $ForegroundColor) $state.Messages += [string]$Object }
            function Start-Process {
                param($FilePath, $ArgumentList, [switch]$Wait, [switch]$PassThru, $ErrorAction)
                if ($FilePath -ne $installerPath -or $ArgumentList -or -not $Wait -or -not $PassThru) {
                    throw 'The interactive installer must be launched once with exit-code tracking.'
                }
                $state.Starts++
                [pscustomobject]@{ ExitCode = $state.ExitCode }
            }
            $installerPath = 'installer.exe'; $downloadUrl = 'https://example.invalid/installer.exe'
            try { & $flow; return '' } catch { return $_.Exception.Message }
        } $launcherBlock $state
        if ($state.Downloads -ne 1 -or $state.Starts -ne 1) { throw 'Unexpected launcher boundary calls.' }
        $messages = $state.Messages -join "`n"
        if ($exitCode -eq 0 -and ($result -or $messages -notmatch 'Installation completed\.')) {
            throw 'Successful installation was not reported.'
        }
        if ($exitCode -eq 2 -and ($result -or $messages -notmatch 'Installation cancelled\.' -or
            $messages -match 'Installation completed\.')) { throw 'Cancellation was not reported accurately.' }
        if ($exitCode -eq 1 -and ($result -notmatch 'installer exited with code 1' -or
            $messages -match 'Installation completed\.')) { throw 'Installer failure was not propagated.' }
        $passed++
    }
    Write-Host "Passed $passed installer/launcher scenarios; production installer compilation succeeded."
}
finally {
    # These identities are generated for this test run and never address a real application.
    try {
        Remove-TaskFixture
        foreach ($testRegistryPath in @(
            "HKCU:\Software\Classes\$testExtension",
            "HKCU:\Software\Classes\win-optimizerFile$testExtension",
            "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$testAppId}_is1"
            "HKCU:\$traceRegistry"
        )) {
            if (Test-Path -LiteralPath $testRegistryPath) { Remove-Item -LiteralPath $testRegistryPath -Recurse -Force }
        }
    }
    finally {
        # Delete only the unique directory beneath the verified test-managed parent.
        $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedRoot.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installer test cleanup path.' }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
