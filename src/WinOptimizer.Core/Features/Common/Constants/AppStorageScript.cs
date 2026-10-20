namespace WinOptimizer.Core.Features.Common.Constants;

/// <summary>Shared PowerShell 5.1 storage boundary for embedded and exported scripts.</summary>
public static class AppStorageScript
{
    public const string Helpers = """
function Get-WinOptimizerDataRoot {
    Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'win-optimizer'
}
function Assert-WinOptimizerStoragePath([string]$path) {
    $root=[IO.Path]::GetFullPath((Get-WinOptimizerDataRoot)).TrimEnd('\')
    $full=[IO.Path]::GetFullPath($path)
    if ($full -ine $root -and -not $full.StartsWith($root + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Application storage path escapes the data directory.'
    }
    $current=$full
    while ($current) {
        try { $attributes=[IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { $attributes=0 }
        catch [IO.DirectoryNotFoundException] { $attributes=0 }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Application storage cannot follow a link: $current" }
        $current=[IO.Path]::GetDirectoryName($current)
    }
}
function Get-WinOptimizerUserDirectory([string]$sid) {
    if (-not $sid -or (New-Object Security.Principal.SecurityIdentifier($sid)).Value -cne $sid) { throw 'Invalid storage account SID.' }
    Join-Path (Get-WinOptimizerDataRoot) ('Users\' + $sid)
}
function Initialize-WinOptimizerUserDirectory([string]$sid) {
    $root=Get-WinOptimizerDataRoot
    $path=Get-WinOptimizerUserDirectory $sid
    Assert-WinOptimizerStoragePath $path
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $admins=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system=New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $acl.SetOwner($admins); $acl.SetAccessRuleProtection($true,$false)
    foreach ($account in @($admins,$system)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
    }
    foreach ($directory in @($root,(Join-Path $root 'Users'))) {
        if (-not [IO.Directory]::Exists($directory)) { [void](New-Item -Path $directory -ItemType Directory -Force -ErrorAction Stop) }
        $existing=(Get-Acl -LiteralPath $directory).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($existing -notin @('S-1-5-32-544','S-1-5-18')) { throw "Application storage has an untrusted owner: $directory" }
        $parentAcl=New-Object Security.AccessControl.DirectorySecurity
        $parentAcl.SetOwner($admins); $parentAcl.SetAccessRuleProtection($true,$false)
        foreach ($account in @($admins,$system)) {
            $parentAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
        }
        $parentAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-11')),'ReadAndExecute','Allow')))
        Set-Acl -LiteralPath $directory -AclObject $parentAcl -ErrorAction Stop
    }
    $scriptDirectory=Join-Path $root 'Unattend'
    Assert-WinOptimizerStoragePath $scriptDirectory
    [void](New-Item -Path $scriptDirectory -ItemType Directory -Force -ErrorAction Stop)
    $scriptAcl=New-Object Security.AccessControl.DirectorySecurity
    $scriptAcl.SetOwner($admins); $scriptAcl.SetAccessRuleProtection($true,$false)
    foreach ($account in @($admins,$system)) {
        $scriptAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
    }
    $scriptAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-11')),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
    Set-Acl -LiteralPath $scriptDirectory -AclObject $scriptAcl -ErrorAction Stop
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'Modify','ContainerInherit,ObjectInherit','None','Allow')))
    [void](New-Item -Path $path -ItemType Directory -Force -ErrorAction Stop)
    Set-Acl -LiteralPath $path -AclObject $acl -ErrorAction Stop
    return $path
}
function Read-WinOptimizerJson([string]$path) {
    Assert-WinOptimizerStoragePath $path
    if (-not [IO.File]::Exists($path)) { return $null }
    $value=[IO.File]::ReadAllText($path) | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $value) { throw "Empty application state: $path" }
    return $value
}
function Read-WinOptimizerCompletionMarker([string]$path) {
    $saved=Read-WinOptimizerJson $path
    if ($null -ne $saved -and ($saved.Version -ne 1 -or $null -eq $saved.Value)) { throw "Invalid application completion state: $path" }
    return $saved
}
function Get-WinOptimizerTemporaryDirectory {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal=New-Object Security.Principal.WindowsPrincipal($identity)
        $root=if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Get-WinOptimizerDataRoot }
            else { Get-WinOptimizerUserDirectory $identity.User.Value }
        $directory=Join-Path $root 'Temp'
        Assert-WinOptimizerStoragePath $directory
        [void][IO.Directory]::CreateDirectory($directory)
        return $directory
    } finally { $identity.Dispose() }
}
function Save-WinOptimizerJson([string]$path, $value, [switch]$ReplaceExisting) {
    Assert-WinOptimizerStoragePath $path
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    $json=$value | ConvertTo-Json -Depth 12 -Compress
    $temporary=$path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $bytes=(New-Object Text.UTF8Encoding($false)).GetBytes($json)
        $stream=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ([IO.File]::ReadAllText($temporary) -cne $json) { throw 'Application state could not be verified.' }
        if ([IO.File]::Exists($path)) {
            if (-not $ReplaceExisting) { throw "Application state already exists: $path" }
            [IO.File]::Replace($temporary,$path,[System.Management.Automation.Language.NullString]::Value)
        } else { [IO.File]::Move($temporary,$path) }
        if ([IO.File]::ReadAllText($path) -cne $json) { throw 'Saved application state could not be verified.' }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}
""";
}
