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
}
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
