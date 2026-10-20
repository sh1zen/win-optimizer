using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Reads device presence, INF bindings and driver-store metadata without changing devices.</summary>
public sealed class WindowsDriverInventorySource(DriverPowerShell shell) : IDriverInventorySource
{
    public async Task<DriverInventory> ReadAsync(CancellationToken ct)
    {
        var inventory = await shell.RunAsync<DriverInventory>("DriverInventory", "", ct).ConfigureAwait(false);
        if (inventory.Devices is null || inventory.Packages is null || inventory.Notices is null ||
            inventory.Devices.Any(device => device is null || string.IsNullOrWhiteSpace(device.InstanceId) ||
                device.Name is null || device.ClassName is null || device.Version is null || device.InfName is null ||
                device.Date is null || device.HardwareIds is null || device.HardwareIds.Any(string.IsNullOrWhiteSpace)) ||
            inventory.Packages.Any(package => package is null || string.IsNullOrWhiteSpace(package.PublishedName) ||
                package.OriginalName is null || package.Provider is null || package.ClassName is null || package.Version is null ||
                package.Date is null || package.SizeBytes < 0) ||
            inventory.Notices.Any(notice => notice is null))
            throw new DriverScanException("Windows returned an incomplete device or driver inventory.");
        return inventory;
    }
}
