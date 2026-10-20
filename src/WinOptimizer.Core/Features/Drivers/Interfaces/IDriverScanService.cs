using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Interfaces;

public interface IDriverInventorySource
{
    Task<DriverInventory> ReadAsync(CancellationToken ct);
}

/// <summary>Returns applicable driver offers from Microsoft's public Windows Update service.</summary>
public interface IDriverUpdateSource
{
    Task<DriverUpdateSearchResult> SearchAsync(IProgress<DriverScanProgress>? progress, CancellationToken ct);
}

public interface IDriverScanService
{
    Task<DriverScanReport> ScanAsync(DriverScanOptions options, IProgress<DriverScanProgress>? progress, CancellationToken ct);
}
