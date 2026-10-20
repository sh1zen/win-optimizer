using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Interfaces;

public interface IDriverRemovalService
{
    Task<DriverRemovalReport> RemoveAsync(DriverScanReport report, IReadOnlyList<DriverRemovalTarget> selection,
        IProgress<DriverScanProgress>? progress, CancellationToken ct);
}

/// <summary>Removes one exact Windows identity; an active native operation completes before cancellation.</summary>
public interface IDriverRemovalBackend
{
    Task<DriverRemovalOutcome> RemoveAsync(DriverRemovalTarget target, CancellationToken ct);
}
