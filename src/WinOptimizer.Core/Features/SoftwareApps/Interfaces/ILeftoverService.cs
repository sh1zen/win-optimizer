using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>Discovers stale registrations and unreferenced installer caches, with fresh verification and recoverable cleanup.</summary>
public interface ILeftoverService
{
    Task<LeftoverScan> ScanAsync(CancellationToken ct = default);
    Task<LeftoverCleanupResult> RemoveAsync(IReadOnlyCollection<Guid> selected, CancellationToken ct = default);
}
