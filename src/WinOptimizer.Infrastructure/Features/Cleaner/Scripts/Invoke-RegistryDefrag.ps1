$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-RegularEntry([string]$Path) {
    $entry = Get-Item -LiteralPath $Path -Force
    do {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Registry maintenance cannot follow a link or mount point: $Path"
        }
        $entry = if ($entry -is [IO.FileInfo]) { $entry.Directory } else { $entry.Parent }
    } while ($null -ne $entry)
}

function Get-FileHashValue([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# Flush the backup to disk and check it against the inactive source before any hive is changed.
function Copy-Verified([string]$Source, [string]$Destination) {
    $hash = Get-FileHashValue $Source
    $inputStream = [IO.File]::OpenRead($Source)
    try {
        $outputStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
        try {
            $inputStream.CopyTo($outputStream)
            $outputStream.Flush($true)
        }
        finally { $outputStream.Dispose() }
    }
    finally { $inputStream.Dispose() }
    if ((Get-FileHashValue $Destination) -ne $hash) { throw "Backup verification failed: $Source" }
    return $hash
}

function Assert-CleanHive([string]$Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        $header = $reader.ReadUInt32()
        $primarySequence = $reader.ReadUInt32()
        $secondarySequence = $reader.ReadUInt32()
        if ($header -ne 0x66676572 -or $primarySequence -ne $secondarySequence) {
            throw "The hive lacks a clean registry header. Restart Windows normally and scan again: $Path"
        }
    }
    finally { $reader.Dispose() }
}

function Assert-ColdShutdown([string]$Root) {
    $hibernate = Join-Path $Root 'hiberfil.sys'
    if (-not (Test-Path -LiteralPath $hibernate)) { return }
    Assert-RegularEntry $hibernate
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($hibernate))
    try {
        $signature = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
        if ($signature -ne "WAKE" -and $signature -ne [string]::new([char]0, 4)) {
            throw 'Windows has a saved hibernation state. Use Restart and schedule registry compaction again.'
        }
    }
    finally { $reader.Dispose() }
}

function Invoke-RegistryTool([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & (Join-Path ([Environment]::SystemDirectory) 'reg.exe') @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "reg.exe $($Arguments[0]) failed ($LASTEXITCODE). $($output -join ' ')" }
}

# Export only from a working copy. Mount and unload the export to verify that Windows can open it.
function Export-CompactHive([string]$Working, [string]$Candidate) {
    $key = 'HKLM\WinOptimizer_Defrag_' + [guid]::NewGuid().ToString('N')
    Invoke-RegistryTool @('load', $key, $Working)
    try { Invoke-RegistryTool @('save', $key, $Candidate) }
    finally { Invoke-RegistryTool @('unload', $key) }
    Invoke-RegistryTool @('load', $key, $Candidate)
    Invoke-RegistryTool @('unload', $key)
    Assert-CleanHive $Candidate
}

function New-PreparedHive($Target, [int]$Index) {
    Assert-RegularEntry $Target.Path
    Assert-CleanHive $Target.Path
    $folder = Join-Path $Target.Job ('hive-{0:D3}' -f $Index)
    [void][IO.Directory]::CreateDirectory($folder)
    $backup = Join-Path $folder 'original.hiv'
    $hash = Copy-Verified $Target.Path $backup
    $logs = @(foreach ($suffix in '.LOG', '.LOG1', '.LOG2') {
        $path = $Target.Path + $suffix
        if (Test-Path -LiteralPath $path) {
            Assert-RegularEntry $path
            $logBackup = $backup + $suffix
            $logHash = Copy-Verified $path $logBackup
            [pscustomobject]@{ Path = $path; Backup = $logBackup; Hash = $logHash; Parked = (Join-Path $folder "parked$suffix") }
        }
    })
    [pscustomobject]@{ Original = $Target.RelativePath; SHA256 = $hash; Logs = @($logs | ForEach-Object {
        [pscustomobject]@{ Name = [IO.Path]::GetFileName($_.Backup); SHA256 = $_.Hash }
    }) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'backup.json') -Encoding UTF8
    $working = Join-Path $folder 'working.hiv'
    $null = Copy-Verified $backup $working
    $export = Join-Path $folder 'export.hiv'
    Export-CompactHive $working $export
    $candidate = Join-Path $folder 'compacted.hiv'
    $candidateHash = Copy-Verified $export $candidate
    $savings = (Get-Item -LiteralPath $backup -Force).Length - (Get-Item -LiteralPath $candidate -Force).Length
    foreach ($scratch in 'working.hiv', 'export.hiv') {
        foreach ($suffix in '', '.LOG', '.LOG1', '.LOG2') {
            [IO.File]::Delete((Join-Path $folder ($scratch + $suffix)))
        }
    }
    [pscustomobject]@{
        Path = $Target.Path; Backup = $backup; Hash = $hash; Logs = $logs
        Candidate = $candidate; CandidateHash = $candidateHash; Savings = $savings
        Folder = $folder; Security = (Get-Acl -LiteralPath $Target.Path); Attributes = [IO.File]::GetAttributes($Target.Path)
    }
}

function Move-HiveFile([string]$Source, $Hive) {
    $stagedAcl = [Security.AccessControl.FileSecurity]::new()
    $stagedAcl.SetSecurityDescriptorBinaryForm($Hive.Security.GetSecurityDescriptorBinaryForm())
    $stagedAcl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $Source -AclObject $stagedAcl
    # These attributes describe the file entry. NTFS manages allocation attributes such as SparseFile.
    $preserved = [IO.FileAttributes]'ReadOnly, Hidden, System, Archive, NotContentIndexed'
    $attributes = $Hive.Attributes -band $preserved
    if ($attributes -eq 0) { $attributes = [IO.FileAttributes]::Normal }
    [IO.File]::SetAttributes($Source, $attributes)
    [WinOptimizerRegistryDefrag]::Replace($Source, $Hive.Path)
    Set-Acl -LiteralPath $Hive.Path -AclObject $Hive.Security
}

function Install-PreparedHive($Hive) {
    # Both hives have clean headers. Once old logs are parked, either main file is bootable on its own.
    foreach ($log in $Hive.Logs) { [IO.File]::Move($log.Path, $log.Parked) }
    Move-HiveFile $Hive.Candidate $Hive
    if ((Get-FileHashValue $Hive.Path) -ne $Hive.CandidateHash) {
        throw "Installed hive verification failed: $($Hive.Path)"
    }
}

function Restore-PreparedHive($Hive) {
    if ((Get-FileHashValue $Hive.Backup) -ne $Hive.Hash) {
        throw "The original hive backup could not be verified: $($Hive.Backup)"
    }
    $exists = [IO.File]::Exists($Hive.Path)
    if (-not $exists -or (Get-FileHashValue $Hive.Path) -ne $Hive.Hash) {
        $restore = Join-Path $Hive.Folder 'restore.hiv'
        $null = Copy-Verified $Hive.Backup $restore
        Move-HiveFile $restore $Hive
    }
    if ((Get-FileHashValue $Hive.Path) -ne $Hive.Hash) { throw "Restored hive verification failed: $($Hive.Path)" }
    foreach ($log in $Hive.Logs) {
        if ([IO.File]::Exists($log.Parked)) { [IO.File]::Move($log.Parked, $log.Path) }
        if ((Get-FileHashValue $log.Path) -ne $log.Hash) { throw "Restored hive log verification failed: $($log.Path)" }
    }
}

# Prepare every hive first. Reverse every attempted replacement if any target fails.
function Invoke-HiveTransaction([object[]]$Targets, [scriptblock]$RecordSuccess) {
    $attempted = [Collections.Generic.List[object]]::new()
    try {
        $prepared = for ($index = 0; $index -lt $Targets.Count; $index++) {
            New-PreparedHive $Targets[$index] $index
        }
        foreach ($hive in $prepared) {
            if ($hive.Savings -le 4096) { continue }
            $attempted.Add($hive)
            Install-PreparedHive $hive
        }
        $result = [pscustomobject]@{ State = 'Completed'; Compacted = $attempted.Count; Errors = @() }
        if ($null -ne $RecordSuccess) { & $RecordSuccess $result }
        return $result
    }
    catch {
        $errors = [Collections.Generic.List[string]]::new()
        $errors.Add($_.Exception.Message)
        $state = if ($attempted.Count -eq 0) { 'Failed' } else { 'Restored' }
        for ($i = $attempted.Count - 1; $i -ge 0; $i--) {
            try { Restore-PreparedHive $attempted[$i] }
            catch {
                $state = 'RestoreFailed'
                $errors.Add($_.Exception.Message)
            }
        }
        return [pscustomobject]@{ State = $state; Compacted = 0; Errors = $errors.ToArray() }
    }
}

function Resolve-JobVolumes($Plan) {
    $drives = [IO.DriveInfo]::GetDrives() | Where-Object {
        $_.IsReady -and $_.DriveType -eq 'Fixed' -and $_.DriveFormat -eq 'NTFS' -and
        $_.Name -ne [IO.Path]::GetPathRoot([Environment]::SystemDirectory)
    }
    foreach ($volume in $Plan.Volumes) {
        $matches = @(
            foreach ($drive in $drives) {
                $folder = Join-Path $drive.Name $Plan.Job
                $marker = Join-Path $folder 'volume.id'
                if (Test-Path -LiteralPath $marker) {
                    Assert-RegularEntry $marker
                    if ([IO.File]::ReadAllText($marker) -ceq $volume.Id) {
                        [pscustomobject]@{ Job = $folder; Root = $drive.Name }
                    }
                }
            }
        )
        if ($matches.Count -ne 1) { throw 'Cannot identify exactly one offline volume for a reviewed hive.' }
        [pscustomobject]@{ Job = $matches[0].Job; Root = $matches[0].Root; Plan = $volume }
    }
}

function Write-Receipt([string]$Job, $Result, [bool]$CleanupPending = $false, [string[]]$CleanupErrors = @()) {
    $temporary = Join-Path $Job ('result-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $receipt = [pscustomobject]@{
            State = $Result.State; Compacted = $Result.Compacted; Errors = $Result.Errors
            CleanupPending = $CleanupPending; CleanupErrors = $CleanupErrors
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes(($receipt | ConvertTo-Json -Depth 4))
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        $path = Join-Path $Job 'result.json'
        if ([IO.File]::Exists($path)) { [IO.File]::Replace($temporary, $path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $path) }
    }
    finally { [IO.File]::Delete($temporary) }
}

function Invoke-BootEntryRemoval([string]$System, [string]$Entry) {
    $null = & (Join-Path $System 'bcdedit.exe') /delete $Entry /cleanup 2>$null
    if ($LASTEXITCODE -ne 0) { throw "BCDEdit could not remove boot entry $Entry (exit $LASTEXITCODE)." }
}

function Complete-MaintenanceBoot([string]$System, $Plan, [string]$Job, [bool]$Claimed, $Result) {
    $errors = [Collections.Generic.List[string]]::new()
    try {
        Invoke-BootEntryRemoval $System $Plan.Loader
        Invoke-BootEntryRemoval $System $Plan.Device
        # The boot images are safe to discard once both generated BCD objects are gone.
        if ($Job) {
            foreach ($name in 'boot.sdi', 'boot.wim') {
                try { [IO.File]::Delete((Join-Path $Job $name)) }
                catch { $errors.Add("Could not remove ${name}: $($_.Exception.Message)") }
            }
        }
    }
    catch { $errors.Add($_.Exception.Message) }

    if (!$Claimed -and $errors.Count -gt 0) {
        throw "Cannot confirm maintenance boot cleanup. $($errors -join ' ') Restart from Windows Boot Manager."
    }
    if ($Claimed) {
        try { Write-Receipt $Job $Result -CleanupErrors $errors.ToArray() }
        catch { Write-Warning "Could not publish the boot cleanup result: $($_.Exception.Message)" }
    }
    foreach ($issue in $errors) { Write-Warning $issue }
}

function Claim-MaintenanceJob([string]$Job) {
    $claim = [IO.File]::Open((Join-Path $Job 'started'), [IO.FileMode]::CreateNew)
    try { $claim.Flush($true) }
    finally { $claim.Dispose() }
}

function Start-RegistryMaintenance {
    if (-not (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\MiniNT')) {
        throw 'Registry maintenance must run in Windows PE.'
    }
    $system = [Environment]::SystemDirectory
    $plan = ConvertFrom-Json ([IO.File]::ReadAllText((Join-Path $system 'RegistryDefrag.json')))
    if ($plan.Job -cnotmatch '^WinOptimizer\.RegistryDefrag-[a-f0-9]{32}$') { throw 'Invalid maintenance job.' }
    $systemVolume = @($plan.Volumes | Where-Object System)
    if ($systemVolume.Count -ne 1) { throw 'Invalid maintenance system volume.' }

    $job = $null
    $claimed = $false
    try {
        $offlineWindows = Resolve-JobVolumes ([pscustomobject]@{ Job = $plan.Job; Volumes = $systemVolume })
        $job = $offlineWindows.Job
        # The boot manager consumes the one-time sequence. A durable claim also prevents re-entry into this job.
        Claim-MaintenanceJob $job
        $claimed = $true
        Assert-ColdShutdown $offlineWindows.Root
        Add-Type -Path (Join-Path $system 'WinOptimizer.RegistryDefrag.dll')
        [WinOptimizerRegistryDefrag]::EnableRestorePrivilege()
        $targets = foreach ($volume in (Resolve-JobVolumes $plan)) {
            foreach ($relative in $volume.Plan.Hives) {
                $path = [IO.Path]::GetFullPath((Join-Path $volume.Root $relative))
                if ($relative -ne $path.Substring(3) -or -not $path.StartsWith($volume.Root, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Invalid offline hive path.'
                }
                [pscustomobject]@{ Path = $path; Job = $volume.Job; RelativePath = $relative }
            }
        }
        $result = Invoke-HiveTransaction @($targets) { param($success) Write-Receipt $job $success -CleanupPending $true }
    }
    catch {
        if ($job -and -not $claimed -and [IO.File]::Exists((Join-Path $job 'started'))) {
            throw "Registry maintenance already started. Review the protected job directory $job before restarting."
        }
        $result = [pscustomobject]@{ State = 'Failed'; Compacted = 0; Errors = @($_.Exception.Message) }
    }
    if ($result.State -ne 'Completed') {
        foreach ($issue in $result.Errors) { Write-Warning $issue }
        if ($claimed) {
            try { Write-Receipt $job $result -CleanupPending ($result.State -ne 'RestoreFailed') }
            catch { Write-Warning $_.Exception.Message }
        }
    }
    if ($result.State -eq 'RestoreFailed') {
        throw "Registry recovery needs attention. Review the protected job directory $job before restarting."
    }

    Complete-MaintenanceBoot $system $plan $job $claimed $result
    & (Join-Path $system 'wpeutil.exe') reboot
    if ($LASTEXITCODE -ne 0) { throw 'Windows PE could not restart the computer.' }
}

if ($MyInvocation.InvocationName -ne '.') { Start-RegistryMaintenance }
