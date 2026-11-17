using System.Text.Json;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Reads device presence, INF bindings and driver-store metadata without changing devices.</summary>
public sealed class WindowsDriverInventorySource(DriverPowerShell shell) : IDriverInventorySource
{
    public Task<DriverInventory> ReadAsync(CancellationToken ct) => ReadInventoryAsync("", ct);

    /// <summary>Supplies the exact package identities whose sizes participate in removal revalidation.</summary>
    public Task<DriverInventory> ReadForRemovalAsync(IReadOnlySet<string> packageNames, CancellationToken ct)
    {
        var encodedNames = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(packageNames));
        return ReadInventoryAsync($"$selectedPackages=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedNames}')) | ConvertFrom-Json; $measureSelectedOnly=$true;", ct);
    }

    private async Task<DriverInventory> ReadInventoryAsync(string prelude, CancellationToken ct)
    {
        var inventory = await shell.RunAsync<DriverInventory>("DriverInventory", prelude, ct,
            preludeIsOptimization: true).ConfigureAwait(false);
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
