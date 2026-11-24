# Read-only inventory. Missing binding evidence suppresses unreferenced-package claims.
$devices = @(Get-PnpDevice -ErrorAction Stop)
$present = @{}
foreach ($device in @(Get-PnpDevice -PresentOnly -ErrorAction Stop)) { $present[$device.InstanceId] = $true }
$notices = [Collections.Generic.List[string]]::new()
$bindingsComplete = $true
$inventory = [Collections.Generic.List[object]]::new()
foreach ($device in $devices) {
    $properties = @{}
    try {
        # Enumerate available properties so an optional missing property is not a binding failure.
        foreach ($property in @(Get-PnpDeviceProperty -InputObject $device -ErrorAction Stop)) {
            $properties[$property.KeyName] = $property.Data
        }
    }
    catch {
        $bindingsComplete = $false
        $notices.Add(('{0}: {1}' -f $device.InstanceId, $_.Exception.Message))
    }
    $inventory.Add(@{
        InstanceId = [string]$device.InstanceId
        Name = $(if ($device.FriendlyName) { [string]$device.FriendlyName } else { [string]$device.InstanceId })
        ClassName = [string]$device.Class
        InfName = [string]$properties['DEVPKEY_Device_DriverInfPath']
        Present = $present.ContainsKey($device.InstanceId)
        Version = [string]$properties['DEVPKEY_Device_DriverVersion']
        Date = $(if ($properties['DEVPKEY_Device_DriverDate']) { ([DateTime]$properties['DEVPKEY_Device_DriverDate']).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) } else { '' })
        HardwareIds = [string[]]@($properties['DEVPKEY_Device_HardwareIds'] | Where-Object { $_ })
        CompatibleIds = [string[]]@($properties['DEVPKEY_Device_CompatibleIds'] | Where-Object { $_ })
        Manufacturer = [string]$properties['DEVPKEY_Device_Manufacturer']
        MatchingDeviceId = [string]$properties['DEVPKEY_Device_MatchingDeviceId']
        DriverProvider = [string]$properties['DEVPKEY_Device_DriverProvider']
    })
}
$packageSizes = @{}
$selectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($measureSelectedOnly) {
    foreach ($name in $selectedPackages) { [void]$selectedNames.Add([string]$name) }
}
$storeRoot = [IO.Path]::GetFullPath((Join-Path $env:windir 'System32\DriverStore\FileRepository')) + [IO.Path]::DirectorySeparatorChar
function Measure-DriverPackage([string]$infPath) {
    # Measure each package directory once, including shared package paths, without traversing reparse points.
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($infPath))
    if (-not $directory.StartsWith($storeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package path is outside the driver store.' }
    if (-not $packageSizes.ContainsKey($directory)) {
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($directory)
        $bytes = [long]0
        while ($pending.Count -gt 0) {
            $folder = Get-Item -LiteralPath $pending.Pop() -Force -ErrorAction Stop
            if ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Driver package contains a reparse point.' }
            foreach ($entry in @(Get-ChildItem -LiteralPath $folder.FullName -Force -ErrorAction Stop)) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Driver package contains a reparse point.' }
                if ($entry.PSIsContainer) { $pending.Push($entry.FullName) } else { $bytes += $entry.Length }
            }
        }
        $packageSizes[$directory] = $bytes
    }
    return $packageSizes[$directory]
}
$packages = @(Get-WindowsDriver -Online -ErrorAction Stop | ForEach-Object {
    $driver = $_
    $publishedName = [IO.Path]::GetFileName([string]$driver.Driver)
    $size = $null
    if (-not $measureSelectedOnly -or $selectedNames.Contains($publishedName)) {
        try { $size = Measure-DriverPackage ([string]$driver.OriginalFileName) }
        catch { $notices.Add(('{0}: Package size unavailable: {1}' -f $driver.Driver, $_.Exception.Message)) }
    }
    @{
        PublishedName = $publishedName
        OriginalName = [IO.Path]::GetFileName([string]$driver.OriginalFileName)
        Provider = [string]$driver.ProviderName
        ClassName = [string]$driver.ClassName
        Version = [string]$driver.Version
        Inbox = [bool]$driver.Inbox
        BootCritical = [bool]$driver.BootCritical
        Date = $(if ($driver.Date) { ([DateTime]$driver.Date).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture) } else { '' })
        SizeBytes = $size
    }
})
$system = @{}
if (-not $measureSelectedOnly) {
    try {
        $computer = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        $board = Get-CimInstance Win32_BaseBoard -ErrorAction Stop | Select-Object -First 1
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $version = [version]$os.Version
        $processors = [string[]]@(Get-CimInstance Win32_Processor -ErrorAction Stop | ForEach-Object { $_.Name } | Where-Object { $_ } | Select-Object -Unique)
        $system = @{
            Manufacturer = [string]$computer.Manufacturer
            Model = [string]$computer.Model
            Sku = [string]$computer.SystemSKUNumber
            BoardManufacturer = [string]$board.Manufacturer
            BoardProduct = [string]$board.Product
            BoardVersion = [string]$board.Version
            ProcessorNames = $processors
            Major = $version.Major
            Minor = $version.Minor
            Build = $version.Build
            ProductType = [int]$os.ProductType
            SuiteMask = [int]$os.SuiteMask
            DisplayVersion = [string](Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' DisplayVersion -ErrorAction SilentlyContinue)
        }
    }
    catch { $notices.Add(('System identity: {0}' -f $_.Exception.Message)) }
}
@{ Devices = @($inventory.ToArray()); Packages = $packages; BindingsComplete = $bindingsComplete; Notices = @($notices.ToArray()); System = $system } | ConvertTo-Json -Depth 6 -Compress
