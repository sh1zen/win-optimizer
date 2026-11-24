param([string]$Request, [string]$Worker, [string]$CancelEvent)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$system = [Environment]::SystemDirectory
$bcdedit = Join-Path $system 'bcdedit.exe'
$bootManager = '{9dea862c-5cdd-4e70-acc1-f32b344d4795}'

function Invoke-Tool([string]$Tool, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $Tool @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$([IO.Path]::GetFileName($Tool)) failed ($LASTEXITCODE). $($output -join ' ')"
    }
    return ($output -join [Environment]::NewLine)
}

# Read BCD element types by ID so translated BCDEdit field names do not affect preflight.
function Assert-BootSequenceAvailable {
    $store = ([wmiclass]'root\WMI:BcdStore').OpenStore('').Store
    $manager = $store.OpenObject($bootManager).Object
    $elements = $manager.EnumerateElements()
    if (-not $elements.ReturnValue) { throw 'Cannot inspect the Windows boot manager.' }
    if ($elements.Elements | Where-Object { $_.Type -eq 0x24000002 }) {
        throw 'A one-time boot is already scheduled. Restart Windows before scheduling registry compaction.'
    }
    $current = Invoke-Tool $bcdedit @('/enum', '{current}', '/v')
    $currentId = [regex]::Match($current, '\{[a-fA-F0-9-]{36}\}').Value
    $default = $manager.GetElement(0x23000003)
    if (-not $default.ReturnValue -or $currentId -ne $default.Element.Id) {
        throw 'The running Windows installation must be the default boot entry for unattended registry compaction.'
    }
    $loader = $store.OpenObject($currentId).Object.GetElement(0x12000002)
    if (-not $loader.ReturnValue -or $loader.Element.String -notmatch '\\winload\.(efi|exe)$') {
        throw 'Cannot identify the Windows boot loader.'
    }
    return $Matches[1]
}

function New-ProtectedDirectory([string]$Path) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $admins = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
        $identity = New-Object Security.Principal.SecurityIdentifier $sid
        $rule = New-Object Security.AccessControl.FileSystemAccessRule $identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'
        $acl.AddAccessRule($rule)
    }
    if (Test-Path -LiteralPath $Path) { throw "The maintenance directory already exists: $Path" }
    [void][IO.Directory]::CreateDirectory($Path, $acl)
}

function Assert-LocalHivePath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:\\[^:]+$' -or [IO.Path]::GetFullPath($Path) -ne $Path) {
        throw "Invalid registry hive path: $Path"
    }
    $entry = Get-Item -LiteralPath $Path -Force
    if ($entry.PSIsContainer) { throw "Expected a registry hive file: $Path" }
    do {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Registry hive paths containing links or mount points are unsupported: $Path"
        }
        $entry = if ($entry -is [IO.FileInfo]) { $entry.Directory } else { $entry.Parent }
    } while ($null -ne $entry)
}

$jobName = 'WinOptimizer.RegistryDefrag-' + [guid]::NewGuid().ToString('N')
$loaderId = [guid]::NewGuid().ToString('B')
$deviceId = [guid]::NewGuid().ToString('B')
$createdBcd = [Collections.Generic.List[string]]::new()
$directories = [Collections.Generic.List[string]]::new()
$mounted = $false
$scheduled = $false
$cancelled = $false
$cancelSignal = [Threading.EventWaitHandle]::OpenExisting($CancelEvent)
$mutex = [Threading.Mutex]::new($false, 'Global\WinOptimizer.RegistryDefrag')
$ownsMutex = $false
try {
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another registry compaction job is being prepared.' }
    if ($cancelSignal.WaitOne(0)) { throw [OperationCanceledException]::new() }
    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or $env:PROCESSOR_ARCHITEW6432) {
        throw 'Offline registry compaction requires x64 Windows.'
    }
    $extension = Assert-BootSequenceAvailable
    $adk = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\Assessment and Deployment Kit'
    $pe = Join-Path $adk 'Windows Preinstallation Environment\amd64'
    $source = Join-Path $pe 'en-us\winpe.wim'
    $sdi = Join-Path $pe 'Media\Boot\boot.sdi'
    $dism = Join-Path $adk 'Deployment Tools\amd64\DISM\dism.exe'
    $components = 'WinPE-WMI', 'WinPE-NetFX', 'WinPE-Scripting', 'WinPE-PowerShell'
    $packages = foreach ($component in $components) {
        Join-Path $pe "WinPE_OCs\$component.cab"
        Join-Path $pe "WinPE_OCs\en-us\${component}_en-us.cab"
    }
    foreach ($file in (@($source, $sdi, $dism) + $packages)) {
        if (-not [IO.File]::Exists($file)) {
            throw 'Install Windows ADK Deployment Tools and the matching Windows PE add-on in their default locations.'
        }
    }

    $paths = @(ConvertFrom-Json ([IO.File]::ReadAllText($Request)))
    if ($paths.Count -eq 0) { throw 'Select at least one registry hive.' }
    $systemRoot = [IO.Path]::GetPathRoot($system)
    $targets = foreach ($path in $paths) {
        Assert-LocalHivePath $path
        [pscustomobject]@{ Root = [IO.Path]::GetPathRoot($path); Path = $path.Substring(3) }
    }
    $volumes = @(@($systemRoot) + @($targets.Root) | Select-Object -Unique)
    $encryption = Get-CimInstance -Namespace 'root/cimv2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume
    $planVolumes = foreach ($root in $volumes) {
        $drive = [IO.DriveInfo]::new($root)
        if ($drive.DriveType -ne 'Fixed' -or $drive.DriveFormat -ne 'NTFS') {
            throw "Registry compaction requires local fixed NTFS volumes: $root"
        }
        $device = $root.Substring(0, 2)
        $volume = $encryption | Where-Object { $_.DriveLetter -eq $device }
        if ($null -eq $volume) { throw "Cannot verify BitLocker status for $device" }
        $status = Invoke-CimMethod -InputObject $volume -MethodName GetConversionStatus
        if ($status.ReturnValue -ne 0 -or $status.ConversionStatus -ne 0) {
            throw "Offline registry compaction requires a fully decrypted volume: $device"
        }
        $directory = Join-Path $root $jobName
        New-ProtectedDirectory $directory
        $directories.Add($directory)
        $identity = [guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText((Join-Path $directory 'volume.id'), $identity)
        [pscustomobject]@{
            Id = $identity; System = ($root -eq $systemRoot); Hives = @($targets | Where-Object Root -EQ $root | ForEach-Object Path)
        }
    }

    $job = Join-Path $systemRoot $jobName
    $wim = Join-Path $job 'boot.wim'
    $mount = Join-Path $job 'mount'
    [IO.File]::Copy($source, $wim)
    [IO.File]::SetAttributes($wim, [IO.FileAttributes]::Normal)
    [IO.File]::Copy($sdi, (Join-Path $job 'boot.sdi'))
    [IO.Directory]::CreateDirectory($mount) | Out-Null
    $null = Invoke-Tool $dism @('/Mount-Image', "/ImageFile:$wim", '/Index:1', "/MountDir:$mount")
    $mounted = $true
    $packageArguments = @($packages | ForEach-Object { "/PackagePath:$_" })
    $null = Invoke-Tool $dism (@("/Image:$mount", '/Add-Package') + $packageArguments)
    if ($cancelSignal.WaitOne(0)) { throw [OperationCanceledException]::new() }

    $peSystem = Join-Path $mount 'Windows\System32'
    $loaderPath = "\Windows\System32\Boot\winload.$extension"
    if (-not [IO.File]::Exists((Join-Path $mount $loaderPath.TrimStart('\')))) {
        $loaderPath = "\Windows\System32\winload.$extension"
        if (-not [IO.File]::Exists((Join-Path $mount $loaderPath.TrimStart('\')))) {
            throw 'The Windows PE image lacks a compatible boot loader.'
        }
    }
    [IO.File]::Copy($Worker, (Join-Path $peSystem 'Invoke-RegistryDefrag.ps1'))
    # Compile on the running Windows installation. WinPE only loads the finished helper.
    Add-Type -OutputAssembly (Join-Path $peSystem 'WinOptimizer.RegistryDefrag.dll') -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class WinOptimizerRegistryDefrag
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    // The offline worker needs restore rights to preserve the original file owner.
    public static void EnableRestorePrivilege()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020, out token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            long luid;
            if (!LookupPrivilegeValue(null, "SeRestorePrivilege", out luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            bool adjusted = AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (!adjusted || error != 0) throw new Win32Exception(error);
        }
        finally { CloseHandle(token); }
    }

    // A same-volume rename replaces the destination without a delete/recreate interval.
    public static void Replace(string source, string destination)
    {
        const uint replaceExisting = 1;
        const uint writeThrough = 8;
        if (!MoveFileEx(source, destination, replaceExisting | writeThrough))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@
    $plan = [pscustomobject]@{ Job = $jobName; Loader = $loaderId; Device = $deviceId; Volumes = @($planVolumes) }
    $plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $peSystem 'RegistryDefrag.json') -Encoding UTF8
    @'
@echo off
wpeinit
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%SystemRoot%\System32\Invoke-RegistryDefrag.ps1"
'@ | Set-Content -LiteralPath (Join-Path $peSystem 'startnet.cmd') -Encoding ASCII

    $null = Invoke-Tool $dism @('/Unmount-Image', "/MountDir:$mount", '/Commit')
    $mounted = $false
    [IO.Directory]::Delete($mount)

    # Create private options and a hidden loader. Arm the one-time sequence last.
    $null = Invoke-Tool $bcdedit @('/create', $deviceId, '/device', '/d', 'Win Optimizer registry maintenance')
    $createdBcd.Add($deviceId)
    $null = Invoke-Tool $bcdedit @('/set', $deviceId, 'ramdisksdidevice', "partition=$($systemRoot.Substring(0, 2))")
    $null = Invoke-Tool $bcdedit @('/set', $deviceId, 'ramdisksdipath', "\$jobName\boot.sdi")
    $null = Invoke-Tool $bcdedit @('/create', $loaderId, '/d', 'Win Optimizer Registry Defrag', '/application', 'osloader')
    $createdBcd.Insert(0, $loaderId)
    $ramdisk = "ramdisk=[$($systemRoot.Substring(0, 2))]\$jobName\boot.wim,$deviceId"
    $null = Invoke-Tool $bcdedit @('/set', $loaderId, 'device', $ramdisk)
    $null = Invoke-Tool $bcdedit @('/set', $loaderId, 'osdevice', $ramdisk)
    $null = Invoke-Tool $bcdedit @('/set', $loaderId, 'path', $loaderPath)
    $null = Invoke-Tool $bcdedit @('/set', $loaderId, 'systemroot', '\Windows')
    $null = Invoke-Tool $bcdedit @('/set', $loaderId, 'winpe', 'yes')
    $null = Invoke-Tool $bcdedit @('/set', $loaderId, 'detecthal', 'yes')
    $null = Assert-BootSequenceAvailable
    if ($cancelSignal.WaitOne(0)) { throw [OperationCanceledException]::new() }
    # Preserve the newest attempt even if the maintenance image never starts.
    $scheduleMarker = [IO.File]::Open((Join-Path $job 'scheduled'), [IO.FileMode]::CreateNew)
    try { $scheduleMarker.Flush($true) }
    finally { $scheduleMarker.Dispose() }
    $null = Invoke-Tool $bcdedit @('/bootsequence', $loaderId, '/addfirst')
    $scheduled = $true
}
catch [OperationCanceledException] { $cancelled = $true }
catch {
    Write-Output $_.Exception.Message
}
finally {
    if (-not $scheduled) {
        if ($mounted) {
            try { $null = Invoke-Tool $dism @('/Unmount-Image', "/MountDir:$mount", '/Discard'); $mounted = $false }
            catch { $cancelled = $false; Write-Output $_.Exception.Message }
        }
        $bootCleanupFailed = $false
        foreach ($id in $createdBcd) {
            try { $null = Invoke-Tool $bcdedit @('/delete', $id, '/cleanup') }
            catch { $cancelled = $false; $bootCleanupFailed = $true; Write-Output $_.Exception.Message }
        }
        if (-not $mounted -and -not $bootCleanupFailed) {
            foreach ($directory in $directories) {
                try {
                    foreach ($name in 'boot.wim', 'boot.sdi', 'volume.id', 'scheduled') { [IO.File]::Delete((Join-Path $directory $name)) }
                    $mountDirectory = Join-Path $directory 'mount'
                    if ([IO.Directory]::Exists($mountDirectory)) { [IO.Directory]::Delete($mountDirectory) }
                    [IO.Directory]::Delete($directory)
                }
                catch { $cancelled = $false; Write-Output $_.Exception.Message }
            }
        }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    $cancelSignal.Dispose()
}
if ($cancelled) { exit 2 }
if (-not $scheduled) { exit 1 }
exit 0
