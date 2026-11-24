param([Parameter(Mandatory)][string]$ScriptPath)
$ErrorActionPreference = 'Stop'

# Replace the Windows and filesystem reads while executing the bundled inventory script.
$device = [pscustomobject]@{ InstanceId = 'PCI\TEST'; FriendlyName = 'Test adapter'; Class = 'Net' }
$store = Join-Path $env:windir 'System32\DriverStore\FileRepository'
$visited = [Collections.Generic.List[string]]::new()
function Get-PnpDevice { param([switch]$PresentOnly, $ErrorAction) $device }
function Get-PnpDeviceProperty {
    param($InputObject, $ErrorAction)
    [pscustomobject]@{ KeyName = 'DEVPKEY_Device_DriverInfPath'; Data = 'oem2.inf' }
    [pscustomobject]@{ KeyName = 'DEVPKEY_Device_HardwareIds'; Data = @('PCI\VEN_1234&DEV_5678&SUBSYS_00011111', 'PCI\VEN_1234&DEV_5678') }
    [pscustomobject]@{ KeyName = 'DEVPKEY_Device_CompatibleIds'; Data = @('PCI\CC_0200') }
}
function Get-CimInstance {
    param($ClassName, $ErrorAction)
    switch ($ClassName) {
        'Win32_ComputerSystem' { [pscustomobject]@{ Manufacturer = 'Test OEM'; Model = 'Test Model'; SystemSKUNumber = '0123' } }
        'Win32_BaseBoard' { [pscustomobject]@{ Manufacturer = 'Test Board OEM'; Product = 'Test Board'; Version = '1.1' } }
        'Win32_OperatingSystem' { [pscustomobject]@{ Version = '10.0.26100'; ProductType = 1; SuiteMask = 272 } }
        'Win32_Processor' { [pscustomobject]@{ Name = 'Test Processor' } }
        default { throw "Unexpected CIM class $ClassName" }
    }
}
function Get-ItemPropertyValue { param($Path, $Name, $ErrorAction) '24H2' }
function Get-WindowsDriver {
    param([switch]$Online, $ErrorAction)
    foreach ($index in 1, 2) {
        [pscustomobject]@{
            Driver = "oem$index.inf"; OriginalFileName = (Join-Path $store "package$index\test.inf")
            ProviderName = 'Vendor'; ClassName = 'Net'; Version = '1.0'; Inbox = $false; BootCritical = $false
        }
    }
}
function Get-Item {
    param($LiteralPath, [switch]$Force, $ErrorAction)
    $visited.Add([IO.Path]::GetFileName($LiteralPath))
    [pscustomobject]@{ FullName = $LiteralPath; Attributes = [IO.FileAttributes]::Directory }
}
function Get-ChildItem {
    param($LiteralPath, [switch]$Force, $ErrorAction)
    [pscustomobject]@{ Attributes = [IO.FileAttributes]::Normal; PSIsContainer = $false; Length = 2048 }
}

$full = (& $ScriptPath) | ConvertFrom-Json
if (($visited -join ',') -ne 'package1,package2' -or $full.Packages[1].SizeBytes -ne 2048) {
    throw 'A full scan must measure both packages.'
}
if ($full.System.Build -ne 26100 -or $full.System.BoardVersion -ne '1.1' -or $full.System.Model -ne 'Test Model' -or
    $full.System.ProcessorNames[0] -ne 'Test Processor' -or
    $full.Devices[0].HardwareIds.Count -ne 2 -or $full.Devices[0].CompatibleIds[0] -ne 'PCI\CC_0200') {
    throw 'Scan evidence must retain the Windows/OEM context and separate ordered PnP identifiers.'
}
$visited.Clear()
$measureSelectedOnly = $true
$selectedPackages = @('OEM1.INF')
$targeted = (& $ScriptPath) | ConvertFrom-Json
if (($visited -join ',') -ne 'package1' -or $targeted.Packages[0].SizeBytes -ne 2048 -or $null -ne $targeted.Packages[1].SizeBytes) {
    throw 'Removal must measure only the exact selected package, regardless of case.'
}
if (-not $targeted.BindingsComplete -or $targeted.Devices[0].InfName -ne 'oem2.inf' -or $targeted.Packages.Count -ne 2) {
    throw 'Removal must keep complete binding and driver-store inventory evidence.'
}
$visited.Clear()
$selectedPackages = @()
$deviceOnly = (& $ScriptPath) | ConvertFrom-Json
if ($visited.Count -ne 0 -or -not $deviceOnly.Devices[0].Present -or $deviceOnly.Packages.Count -ne 2) {
    throw 'A device-only selection must keep presence evidence without traversing package folders.'
}
Write-Output 'Driver inventory script scenarios passed.'
