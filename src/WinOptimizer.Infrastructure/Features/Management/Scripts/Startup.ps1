param([Parameter(Mandatory=$true)][string]$RequestBase64)
# Native startup inventory and reversible controls. Never execute a registered command.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($RequestBase64)) | ConvertFrom-Json
$rows = New-Object 'Collections.Generic.List[object]'
$warnings = New-Object 'Collections.Generic.List[string]'

function Encode-Identity($value) {
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($value | ConvertTo-Json -Depth 8 -Compress)))
}
function Get-Hash([string]$text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToBase64String($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))) } finally { $hash.Dispose() }
}
function Add-Row($identity, [string]$name, [string]$category, [string]$location, [string]$command,
    [string]$image, [string]$publisher, [string]$profile, $enabled, [bool]$editable, [string]$details) {
    $rows.Add([pscustomobject]@{ Id=(Encode-Identity $identity); Name=$name; Category=$category; Location=$location;
        Command=$command; ImagePath=$image; Publisher=$publisher; Signer=''; Profile=$profile;
        Enabled=$enabled; CanToggle=$editable; Details=$details })
}
function Get-Approval($root, [string]$branch, [string]$name) {
    # StartupApproved belongs to the native Explorer registry view, including Run32 records.
    $hive = if ($root.Name -eq 'HKEY_LOCAL_MACHINE') { [Microsoft.Win32.RegistryHive]::LocalMachine } else { [Microsoft.Win32.RegistryHive]::CurrentUser }
    $view = if ([Environment]::Is64BitOperatingSystem) { [Microsoft.Win32.RegistryView]::Registry64 } else { [Microsoft.Win32.RegistryView]::Registry32 }
    $approvalRoot=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
    $key=$null
    try {
        $key = $approvalRoot.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\' + $branch)
        if ($null -eq $key) { return $true }
        $value = $key.GetValue($name, $null)
        if ($null -eq $value) { return $true }
        if ($value -isnot [byte[]] -or $value.Length -lt 1) { return $null }
        switch ($value[0]) { 2 { return $true }; 6 { return $true }; 3 { return $false }; 7 { return $false }; default { return $null } }
    } finally { if ($key) { $key.Dispose() }; $approvalRoot.Dispose() }
}
# Storage names hash the complete source identity; registry value names never become filesystem paths.
function Get-StorageName([string]$identity) {
    $hash=[Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity)))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
function Get-AutorunStorage([bool]$machine) {
    $root=if ($machine) { Get-WinOptimizerDataRoot } else {
        Get-WinOptimizerUserDirectory ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    }
    Join-Path $root 'State\Autoruns'
}
function Get-RegistryStoragePath($id) {
    Assert-RegistryIdentity $id
    $root=Get-AutorunStorage ($id.Hive -eq 'LocalMachine')
    $directory=Join-Path $root ('Registry\' + $id.View)
    $name=Get-StorageName ($id.Hive + "`n" + $id.View + "`n" + $id.Key + "`n" + $id.Name)
    Join-Path $directory ($name + '.json')
}
function Assert-RegistrySnapshot($key,$id) {
    if ($key.GetValueNames() -notcontains $id.Name -or [string]$key.GetValueKind($id.Name) -cne $id.Kind -or
        [string]$key.GetValue($id.Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $id.Value) {
        throw 'The startup value changed since the scan. Refresh before changing it.'
    }
}
function Assert-SavedRegistrySnapshot($saved,$id) {
    Assert-RegistryIdentity $saved
    foreach ($field in @('Hive','View','Key','Name','Value','Kind')) {
        if ([string]$saved.$field -cne [string]$id.$field) { throw 'The saved startup entry changed. Refresh before changing it.' }
    }
}
function Save-DisabledRegistryValue($source,$id,[string]$path) {
    Assert-RegistryIdentity $id
    Assert-RegistrySnapshot $source $id
    $saved=Read-WinOptimizerJson $path
    if ($null -ne $saved) {
        Assert-SavedRegistrySnapshot $saved $id
    } else {
        Save-WinOptimizerJson $path @{Type='Registry'; Hive=$id.Hive; View=$id.View; Key=$id.Key;
            Name=$id.Name; Stored=$true; Value=$id.Value; Kind=$id.Kind}
    }
    Assert-SavedRegistrySnapshot (Read-WinOptimizerJson $path) $id
    Assert-RegistrySnapshot $source $id
    $source.DeleteValue($id.Name,$true); $source.Flush()
}
function Restore-DisabledRegistryValue($destination,$id,[string]$path) {
    $saved=Read-WinOptimizerJson $path
    if ($null -eq $saved) { throw 'The saved startup entry is missing.' }
    Assert-SavedRegistrySnapshot $saved $id
    if ($destination.GetValueNames() -contains $id.Name) { throw 'An active startup entry already exists. Nothing was overwritten.' }
    $destination.SetValue($id.Name,$id.Value,[Microsoft.Win32.RegistryValueKind]$id.Kind); $destination.Flush()
    Assert-RegistrySnapshot $destination $id
    Assert-SavedRegistrySnapshot (Read-WinOptimizerJson $path) $id
    [IO.File]::Delete($path)
}
function Read-Registry($hive,$view) {
    $root=[Microsoft.Win32.RegistryKey]::OpenBaseKey($hive,$view)
    try {
        foreach ($leaf in @('Run','RunOnce')) {
            $path='SOFTWARE\Microsoft\Windows\CurrentVersion\' + $leaf
            $active=$root.OpenSubKey($path)
            try {
                $records=New-Object 'Collections.Generic.List[object]'
                if ($active) {
                    foreach ($name in $active.GetValueNames()) {
                        $records.Add([pscustomobject]@{Type='Registry'; Hive=[string]$hive; View=[string]$view; Key=$path; Name=$name;
                            Stored=$false; Value=$active.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); Kind=[string]$active.GetValueKind($name)})
                    }
                }
                $storage=Join-Path (Get-AutorunStorage ($hive -eq 'LocalMachine')) ('Registry\' + $view)
                Assert-WinOptimizerStoragePath $storage
                if ([IO.Directory]::Exists($storage)) {
                    foreach ($file in [IO.Directory]::EnumerateFiles($storage,'*.json')) {
                        $record=Read-WinOptimizerJson $file
                        Assert-RegistryIdentity $record
                        if ((Get-RegistryStoragePath $record) -ine $file) { throw 'A saved startup record has an invalid identity.' }
                        if ($record.Hive -eq [string]$hive -and $record.View -eq [string]$view -and $record.Key -eq $path) { $records.Add($record) }
                    }
                }
                foreach ($id in $records) {
                    $supported=$id.Kind -in @('String','ExpandString')
                    $approval=$true
                    if ($leaf -eq 'Run') {
                        $branch=if ($view -eq 'Registry32' -and [Environment]::Is64BitOperatingSystem) { 'Run32' } else { 'Run' }
                        $approval=Get-Approval $root $branch $id.Name
                    }
                    $conflict=if ($id.Stored) { $active -and $active.GetValueNames() -contains $id.Name }
                        else { $supported -and $id.Name.Length -gt 0 -and [IO.File]::Exists((Get-RegistryStoragePath $id)) }
                    $details=if ($conflict) { 'Both an active entry and a saved entry exist. Resolve the conflict before changing this entry.' }
                        elseif ($approval -ne $true) { 'Windows controls this startup state. Use Windows Startup settings.' }
                        elseif ($id.Stored) { 'Disabled by win-optimizer; its original command and registry type are saved on disk.' }
                        else { 'Registry startup command. Disabling preserves its original command and registry type on disk.' }
                    $enabled=if ($id.Stored) { $false } else { $approval }
                    Add-Row $id $id.Name 'Logon' ("{0} ({1})\{2}" -f $hive,$view,$path) ([string]$id.Value) '' '' ([string]$hive) $enabled `
                        ($supported -and $id.Name.Length -gt 0 -and -not $conflict -and $approval -eq $true) $details
                }
            } finally { if ($active) { $active.Dispose() } }
        }
    } finally { $root.Dispose() }
}
function Test-PlainPath([string]$path) {
    $item = New-Object IO.FileInfo($path)
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
    return $true
}
function Get-StartupStoragePaths([string]$folder,[string]$name) {
    if ($folder -notin @([Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup')) -or
        [string]::IsNullOrWhiteSpace($name) -or $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $name.EndsWith('.') -or $name.EndsWith(' ') -or [IO.Path]::GetFileName($name) -cne $name -or $name -in @('.','..')) { throw 'Invalid startup file identity.' }
    $directory=Join-Path (Get-AutorunStorage ($folder -eq [Environment]::GetFolderPath('CommonStartup'))) 'Startup'
    $stem=Get-StorageName ($folder + "`n" + $name)
    @{Directory=$directory; Record=(Join-Path $directory ($stem + '.json')); Payload=(Join-Path $directory ($stem + '.payload' + [IO.Path]::GetExtension($name)))}
}
function Read-StartupFolder([string]$folder,[string]$scope) {
    $records=New-Object 'Collections.Generic.List[object]'
    if ([IO.Directory]::Exists($folder)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($folder)) {
            $name=[IO.Path]::GetFileName($file)
            if ($name -ieq 'desktop.ini') { continue }
            $hash=if (Test-PlainPath $file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash } else { '' }
            $records.Add([pscustomobject]@{Type='File'; Folder=$folder; Name=$name; Stored=$false; Hash=$hash; Scope=$scope})
        }
    }
    $storage=Join-Path (Get-AutorunStorage ($folder -eq [Environment]::GetFolderPath('CommonStartup'))) 'Startup'
    Assert-WinOptimizerStoragePath $storage
    if ([IO.Directory]::Exists($storage)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($storage,'*.json')) {
            $id=Read-WinOptimizerJson $file
            $paths=Get-StartupStoragePaths $id.Folder $id.Name
            if ($paths.Record -ine $file) { throw 'A saved startup file has an invalid identity.' }
            if ($id.Folder -eq $folder) { $records.Add($id) }
        }
    }
    foreach ($id in $records) {
        $paths=Get-StartupStoragePaths $folder $id.Name
        $original=Join-Path $folder $id.Name
        $file=if ($id.Stored) { $paths.Payload } else { $original }
        Assert-WinOptimizerStoragePath $paths.Payload
        $plain=[IO.File]::Exists($file) -and (Test-PlainPath $file)
        $unchanged=$plain -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ceq $id.Hash
        $conflict=if ($id.Stored) { [IO.File]::Exists($original) } else { [IO.File]::Exists($paths.Record) }
        $command=$original; $image=$original
        if ($plain -and [IO.Path]::GetExtension($id.Name) -ieq '.lnk') {
            $shell=$null; $shortcut=$null
            try {
                $shell=New-Object -ComObject WScript.Shell; $shortcut=$shell.CreateShortcut($file)
                $image=[string]$shortcut.TargetPath; $command=($image + ' ' + $shortcut.Arguments).Trim()
            } catch { $warnings.Add("Shortcut ${file}: $($_.Exception.Message)") }
            finally { if ($shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }; if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) } }
        }
        $root=if ($folder -eq [Environment]::GetFolderPath('CommonStartup')) { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
        $approval=Get-Approval $root 'StartupFolder' $id.Name
        $enabled=if ($id.Stored) { $false } else { $approval }
        $details=if ($conflict) { 'An active file and a saved file have the same source. Resolve the conflict before changing it.' }
            elseif (-not $unchanged) { 'The startup file is missing, changed or redirected. Inspect the saved data.' }
            elseif ($approval -ne $true) { 'Windows controls this startup state. Use Windows Startup settings.' }
            elseif ($id.Stored) { 'The original startup file is preserved in application data.' }
            else { 'Disabling preserves this file in application data.' }
        Add-Row $id $id.Name 'Startup folders' $folder $command $image '' $scope $enabled `
            ($unchanged -and -not $conflict -and $approval -eq $true) $details
    }
}
function Read-Tasks($folder) {
    foreach ($task in $folder.GetTasks(1)) {
        try {
            $definition = [xml]$task.Xml
            $startup = $null -ne $definition.SelectSingleNode("//*[local-name()='BootTrigger' or local-name()='LogonTrigger']")
            if (-not $startup) { continue }
            $commands = @($definition.SelectNodes("//*[local-name()='Exec']") | ForEach-Object { ([string]$_.Command + ' ' + [string]$_.Arguments).Trim() })
            $command = $commands -join "`n"
            $system = $task.Path.StartsWith('\Microsoft\', [StringComparison]::OrdinalIgnoreCase)
            $identity = @{Type='Task'; Path=[string]$task.Path; Hash=(Get-Hash $task.Xml); Enabled=[bool]$task.Enabled}
            Add-Row $identity $task.Name 'Scheduled tasks' $task.Path $command '' ([string]$definition.Task.RegistrationInfo.Author) `
                ([string]$definition.Task.Principals.Principal.UserId) ([bool]$task.Enabled) (-not $system) `
                ($(if ($system) { 'System task: inspect only.' } else { 'Boot/logon task. Disabling changes the whole task, including any other triggers.' }))
        } catch { $warnings.Add("Task $($task.Path): $($_.Exception.Message)") }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($task) }
    }
    foreach ($child in $folder.GetFolders(0)) {
        try { Read-Tasks $child } catch { $warnings.Add("Task folder $($child.Path): $($_.Exception.Message)") }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($child) }
    }
}
function Read-PackagedTasks {
    foreach ($package in Get-AppxPackage -ErrorAction Stop) {
        try {
            $manifest = Get-AppxPackageManifest -Package $package -ErrorAction Stop
            foreach ($node in $manifest.SelectNodes("//*[local-name()='Extension' and contains(@Category,'startupTask')]//*[local-name()='StartupTask']")) {
                $name = [string]$node.DisplayName
                if (-not $name -or $name.StartsWith('ms-resource:')) { $name = $package.Name + ' / ' + $node.TaskId }
                $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\' + $package.PackageFamilyName + '\' + $node.TaskId)
                $enabled = $null
                try { if ($key) { switch ($key.GetValue('State', -1)) { 0 { $enabled=$false }; 1 { $enabled=$false }; 2 { $enabled=$true }; 3 { $enabled=$false }; 4 { $enabled=$true } } } }
                finally { if ($key) { $key.Dispose() } }
                $extension = $node.ParentNode
                $exe = [string]$extension.Executable
                if (-not $exe) { $exe = [string]$extension.ParentNode.ParentNode.Executable }
                $image = if ($exe) { Join-Path $package.InstallLocation $exe } else { '' }
                Add-Row @{Type='Package'; Family=$package.PackageFamilyName; Task=[string]$node.TaskId} $name 'Windows apps' `
                    $package.PackageFamilyName $image $image ([string]$package.Publisher) $env:USERNAME $enabled $false `
                    'Declared packaged startup task. The reported state may be unavailable; manage this app in Windows Startup settings.'
            }
        } catch { $warnings.Add("Package $($package.Name): $($_.Exception.Message)") }
    }
}
function Assert-RegistryIdentity($id) {
    if ($id.Hive -notin @('CurrentUser','LocalMachine') -or $id.View -notin @('Registry32','Registry64') -or
        $id.Key -notin @('SOFTWARE\Microsoft\Windows\CurrentVersion\Run','SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce') -or
        $id.Kind -notin @('String','ExpandString') -or [string]::IsNullOrEmpty($id.Name)) { throw 'Invalid startup registry identity.' }
}
function Write-Registry($id,[bool]$enabled) {
    Assert-RegistryIdentity $id
    if ($enabled -ne [bool]$id.Stored) { throw 'Refresh this entry before changing it.' }
    $root=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]$id.Hive,[Microsoft.Win32.RegistryView]$id.View)
    $active=$null
    try {
        $branch=if ($id.View -eq 'Registry32' -and [Environment]::Is64BitOperatingSystem) { 'Run32' } else { 'Run' }
        if ($id.Key.EndsWith('\Run') -and (Get-Approval $root $branch $id.Name) -ne $true) { throw 'Windows startup approval changed. Refresh and use Windows Startup settings.' }
        $active=if ($enabled) { $root.CreateSubKey($id.Key,$true) } else { $root.OpenSubKey($id.Key,$true) }
        if (-not $active) { throw 'The startup key changed since the scan. Refresh before changing it.' }
        $path=Get-RegistryStoragePath $id
        if ($enabled) { Restore-DisabledRegistryValue $active $id $path }
        else { Save-DisabledRegistryValue $active $id $path }
    } finally { if ($active) { $active.Dispose() }; $root.Dispose() }
}
function Move-PreservedStartupFile([string]$source, [string]$destination, [string]$savedFolder, [string]$hash) {
    if (-not [IO.File]::Exists($source) -or -not (Test-PlainPath $source) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $hash) { throw 'The startup file changed. Refresh before changing it.' }
    [void][IO.Directory]::CreateDirectory($savedFolder)
    if (-not (Test-PlainPath $savedFolder)) { throw 'The disabled startup folder is redirected. No file was moved.' }
    [IO.File]::Move($source, $destination)
}
function Write-File($id,[bool]$enabled) {
    $paths=Get-StartupStoragePaths $id.Folder $id.Name
    if ($id.Type -ne 'File' -or $enabled -ne [bool]$id.Stored -or $id.Hash -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Invalid startup file identity.' }
    Assert-WinOptimizerStoragePath $paths.Record
    $root=if ($id.Folder -eq [Environment]::GetFolderPath('CommonStartup')) { [Microsoft.Win32.Registry]::LocalMachine } else { [Microsoft.Win32.Registry]::CurrentUser }
    if ((Get-Approval $root 'StartupFolder' $id.Name) -ne $true) { throw 'Windows startup approval changed. Use Windows Startup settings.' }
    if ($enabled) { Restore-DisabledStartupFile $id $paths }
    else { Save-DisabledStartupFile $id $paths }
}
function Save-DisabledStartupFile($id,$paths) {
    $original=Join-Path $id.Folder $id.Name
    Assert-WinOptimizerStoragePath $paths.Record
    Assert-WinOptimizerStoragePath $paths.Payload
    if ([IO.File]::Exists($paths.Record) -or [IO.File]::Exists($paths.Payload)) { throw 'A saved startup file already exists.' }
    if (-not [IO.File]::Exists($original) -or -not (Test-PlainPath $original) -or (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash -cne $id.Hash) {
        throw 'The startup file changed since the scan. Refresh before changing it.'
    }
    Save-WinOptimizerJson $paths.Record @{Type='File'; Folder=$id.Folder; Name=$id.Name; Stored=$true; Hash=$id.Hash; Scope=$id.Scope}
    try { Move-PreservedStartupFile $original $paths.Payload $paths.Directory $id.Hash }
    catch {
        if (-not [IO.File]::Exists($paths.Payload)) { [IO.File]::Delete($paths.Record) }
        throw
    }
}
function Restore-DisabledStartupFile($id,$paths) {
    Assert-WinOptimizerStoragePath $paths.Record
    Assert-WinOptimizerStoragePath $paths.Payload
    $saved=Read-WinOptimizerJson $paths.Record
    if ($null -eq $saved -or $saved.Hash -cne $id.Hash -or $saved.Folder -cne $id.Folder -or $saved.Name -cne $id.Name) { throw 'The saved startup file changed. Refresh the inventory.' }
    Move-PreservedStartupFile $paths.Payload (Join-Path $id.Folder $id.Name) $id.Folder $id.Hash
    [IO.File]::Delete($paths.Record)
}
function Write-Task($id, [bool]$enabled) {
    if (-not ([string]$id.Path).StartsWith('\') -or ([string]$id.Path).StartsWith('\Microsoft\',[StringComparison]::OrdinalIgnoreCase)) { throw 'System tasks cannot be changed here.' }
    $scheduler = New-Object -ComObject Schedule.Service; $folder=$null; $task=$null
    try {
        $scheduler.Connect(); $folder=$scheduler.GetFolder('\'); $task=$folder.GetTask($id.Path)
        $definition=[xml]$task.Xml
        if ((Get-Hash $task.Xml) -cne $id.Hash -or [bool]$task.Enabled -ne [bool]$id.Enabled) { throw 'The task changed. Refresh before changing it.' }
        if ($null -eq $definition.SelectSingleNode("//*[local-name()='BootTrigger' or local-name()='LogonTrigger']")) { throw 'This task cannot be changed here.' }
        $task.Enabled=$enabled
        if ([bool]$task.Enabled -ne $enabled) { throw 'The scheduler did not retain the requested state.' }
    } finally {
        foreach ($com in @($task,$folder,$scheduler)) { if ($com) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) } }
    }
}
try {
    if ($request.Action -eq 'Write') {
        $id = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($request.Entry.Id)) | ConvertFrom-Json
        switch ($id.Type) {
            'Registry' { Write-Registry $id ([bool]$request.Enabled) }
            'File' { Write-File $id ([bool]$request.Enabled) }
            'Task' { Write-Task $id ([bool]$request.Enabled) }
            default { throw 'This entry must be managed in Windows.' }
        }
        @{Applied=$true} | ConvertTo-Json -Compress
    } elseif ($request.Action -eq 'Read') {
        $views = if ([Environment]::Is64BitOperatingSystem) { @('Registry64','Registry32') } else { @('Registry32') }
        foreach ($hive in @('CurrentUser','LocalMachine')) {
            foreach ($view in $views) {
                # HKCU Run is shared between registry views; enumerate it once.
                if ($hive -eq 'CurrentUser' -and $view -eq 'Registry32' -and [Environment]::Is64BitOperatingSystem) { continue }
                try { Read-Registry ([Microsoft.Win32.RegistryHive]$hive) ([Microsoft.Win32.RegistryView]$view) }
                catch { $warnings.Add("Registry $hive / ${view}: $($_.Exception.Message)") }
            }
        }
        foreach ($scope in @('Startup','CommonStartup')) {
            try { Read-StartupFolder ([Environment]::GetFolderPath($scope)) ($(if ($scope -eq 'Startup') { $env:USERNAME } else { 'All users' })) }
            catch { $warnings.Add("Startup folder ${scope}: $($_.Exception.Message)") }
        }
        try { Read-PackagedTasks } catch { $warnings.Add("Windows apps: $($_.Exception.Message)") }
        $scheduler=$null; $folder=$null
        try { $scheduler=New-Object -ComObject Schedule.Service; $scheduler.Connect(); $folder=$scheduler.GetFolder('\'); Read-Tasks $folder }
        catch { $warnings.Add("Scheduled tasks: $($_.Exception.Message)") }
        finally { foreach ($com in @($folder,$scheduler)) { if ($com) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) } } }
        @{Entries=@($rows.ToArray()); Warnings=@($warnings.ToArray()); Coverage=("{0}\{1}; machine Run/RunOnce, startup folders, current-account Windows apps, accessible boot/logon tasks. Advanced view covers additional loading points." -f $env:USERDOMAIN,$env:USERNAME)} | ConvertTo-Json -Depth 8 -Compress
    } else { throw 'Unsupported startup operation.' }
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
