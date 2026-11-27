using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Interfaces;

public interface IDriverInventorySource
{
    Task<DriverInventory> ReadAsync(CancellationToken ct);

    /// <summary>Reads binding evidence for all devices and measures packages selected for removal.</summary>
    Task<DriverInventory> ReadForRemovalAsync(IReadOnlySet<string> packageNames, CancellationToken ct) => ReadAsync(ct);
}

/// <summary>Collects records for one official source against the shared scan snapshot.</summary>
public interface IDriverUpdateSource
{
    DriverSources Source { get; }
    Task<DriverUpdateSearchResult> SearchAsync(DriverDiscoveryContext context, IProgress<DriverScanProgress>? progress, CancellationToken ct);
}

public interface IDriverPackageService
{
    Task<DriverPackageResult> AcquireAsync(DriverUpdate update, DriverInventory inventory, bool download,
        IProgress<DriverScanProgress>? progress, CancellationToken ct, bool licenseAccepted = false);
}

public interface IDriverScanService
{
    Task<DriverScanReport> ScanAsync(DriverScanOptions options, IProgress<DriverScanProgress>? progress, CancellationToken ct,
        ConfirmDriverLicenseAsync? confirmLicense = null);
}

/// <summary>Requests permission to acquire packages covered by the publisher's license for the current scan.</summary>
public delegate Task<bool> ConfirmDriverLicenseAsync(Uri licensePage, CancellationToken ct);
