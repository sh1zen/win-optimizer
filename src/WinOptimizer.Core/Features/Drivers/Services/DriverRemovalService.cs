using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Services;

/// <summary>Revalidates scanned review candidates before removal and retains partial batch outcomes.</summary>
public sealed class DriverRemovalService(IDriverInventorySource inventorySource, IDriverRemovalBackend backend,
    IApplicationModeService mode) : IDriverRemovalService
{
    public async Task<DriverRemovalReport> RemoveAsync(DriverScanReport report, IReadOnlyList<DriverRemovalTarget> selection,
        IProgress<DriverScanProgress>? progress, CancellationToken ct)
    {
        using var reservation = mode.BeginSystemOperation();
        ct.ThrowIfCancellationRequested();
        var targets = selection.Distinct().ToArray();
        if (targets.Length == 0 || targets.Any(target => !IsScanCandidate(report, target)))
            throw new DriverScanException("Select driver packages or disconnected devices from the current scan.");

        var current = await inventorySource.ReadAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        List<DriverRemovalOutcome> outcomes = [];
        // Remove device registrations first; package deletion never uninstalls dependent devices.
        foreach (var target in targets.OrderBy(target => target.Kind == DriverRemovalKind.Device ? 0 : 1))
        {
            if (ct.IsCancellationRequested) return new(outcomes, true);
            progress?.Report(new("Drivers_Removing", outcomes.Count, targets.Length));
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!StillEligible(report, current, target))
                    outcomes.Add(new(target, false, false, "The driver or device changed since the scan. Scan again before removing it."));
                else
                    outcomes.Add(await backend.RemoveAsync(target, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(outcomes, true); }
            catch (DriverScanException ex) { outcomes.Add(new(target, false, false, ex.Message)); }
        }
        progress?.Report(new("Drivers_Removing", outcomes.Count, targets.Length));
        return new(outcomes, ct.IsCancellationRequested);
    }

    /// <summary>Restricts requests to exact identities exposed as removable by the supplied scan.</summary>
    private static bool IsScanCandidate(DriverScanReport report, DriverRemovalTarget target) => target.Kind switch
    {
        DriverRemovalKind.Package => report.UnreferencedPackages.Any(package =>
            package.PublishedName.Equals(target.Id, StringComparison.OrdinalIgnoreCase)),
        DriverRemovalKind.Device => report.DisconnectedDevices.Any(device =>
            device.InstanceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    /// <summary>Rejects reconnected devices, changed identities and newly bound or protected packages.</summary>
    private static bool StillEligible(DriverScanReport report, DriverInventory current, DriverRemovalTarget target)
    {
        if (target.Kind == DriverRemovalKind.Package)
        {
            var scanned = report.UnreferencedPackages.Single(package => package.PublishedName.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            var package = current.Packages.SingleOrDefault(package => package.PublishedName.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            return current.BindingsComplete && package is not null && package == scanned && !package.Inbox && !package.BootCritical &&
                !current.Devices.Any(device => device.InfName.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        }
        var previous = report.DisconnectedDevices.Single(device => device.InstanceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        var device = current.Devices.SingleOrDefault(device => device.InstanceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
        return device is { Present: false } && device.InfName.Equals(previous.InfName, StringComparison.OrdinalIgnoreCase) &&
            device.Version == previous.Version && device.ClassName == previous.ClassName && device.Date == previous.Date &&
            device.HardwareIds.SequenceEqual(previous.HardwareIds, StringComparer.OrdinalIgnoreCase);
    }
}
