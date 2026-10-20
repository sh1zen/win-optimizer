using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Services;

/// <summary>Identifies unassigned packages conservatively and filters official offers using package evidence.</summary>
public sealed class DriverScanService(IDriverInventorySource inventorySource, IDriverUpdateSource updateSource) : IDriverScanService
{
    public async Task<DriverScanReport> ScanAsync(DriverScanOptions options, IProgress<DriverScanProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("Drivers_Inventory"));
        var inventory = await inventorySource.ReadAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var boundInfs = inventory.Devices.Select(device => device.InfName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreferenced = inventory.BindingsComplete
            ? inventory.Packages.Where(package => !package.Inbox && !package.BootCritical && !boundInfs.Contains(package.PublishedName)).ToArray()
            : [];
        var disconnected = inventory.Devices.Where(device => !device.Present).ToArray();
        var hardware = new Dictionary<string, List<DriverDevice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in inventory.Devices.Where(device => device.Present))
            foreach (var id in device.HardwareIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!hardware.TryGetValue(id, out var devices)) hardware.Add(id, devices = []);
                devices.Add(device);
            }
        List<string> notices = [.. inventory.Notices];
        DriverUpdateSearchResult search;
        try
        {
            progress?.Report(new("Drivers_SearchingUpdates"));
            search = await updateSource.SearchAsync(progress, ct).ConfigureAwait(false);
        }
        catch (DriverScanException ex)
        {
            ct.ThrowIfCancellationRequested();
            notices.Add(ex.Message);
            return new(inventory, unreferenced, disconnected, [], false, 0, notices, options);
        }
        notices.AddRange(search.Notices);
        List<DriverUpdateMatch> updates = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = 0;
        foreach (var candidate in search.Updates)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(candidate.Id)) continue;
            if (options.OnlyWhqlUpdates && candidate.Certification != DriverCertification.Whql) { excluded++; continue; }
            IReadOnlyList<DriverDevice> devices = hardware.TryGetValue(candidate.HardwareId, out var matches) ? matches : [];
            // Windows offers some optional replacements with an equal or older version; these are not upgrades.
            if (Version.TryParse(candidate.Version, out var available) && devices.Count > 0 &&
                devices.All(device => Version.TryParse(device.Version, out var installed) && installed >= available))
                continue;
            updates.Add(new(candidate, devices));
        }
        ct.ThrowIfCancellationRequested();
        return new(inventory, unreferenced, disconnected, updates, true, excluded, notices, options);
    }
}
