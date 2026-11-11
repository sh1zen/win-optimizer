using System.Globalization;
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
            // Only compare the same driver class; extension offers can target another class's device.
            if (devices.Count > 0 && devices.All(device => IsCurrentOrNewer(device, candidate)))
                continue;
            updates.Add(new(candidate, devices));
        }
        ct.ThrowIfCancellationRequested();
        return new(inventory, unreferenced, disconnected, updates, true, excluded, notices, options);
    }

    /// <summary>Retains newer dates or versions and offers with incomplete or unrelated comparison evidence.</summary>
    private static bool IsCurrentOrNewer(DriverDevice device, DriverUpdate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.ClassName) ||
            !candidate.ClassName.Equals(device.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
        if (DateOnly.TryParseExact(candidate.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var availableDate) &&
            DateOnly.TryParseExact(device.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var installedDate) &&
            availableDate > installedDate) return false;
        if (!Version.TryParse(candidate.Version, out var available) || !Version.TryParse(device.Version, out var installed)) return false;
        // Windows driver versions have four components; missing trailing components represent zero.
        return new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build), Math.Max(0, installed.Revision)) >=
            new Version(available.Major, available.Minor, Math.Max(0, available.Build), Math.Max(0, available.Revision));
    }
}
