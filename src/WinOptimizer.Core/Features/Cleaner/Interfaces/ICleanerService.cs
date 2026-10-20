using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Core.Features.Cleaner.Interfaces;

/// <summary>Owns a scan and only applies selections issued by that scan.</summary>
public interface ICleanerService
{
    Task<CleanerScanResult> ScanAsync(CleanerOptions options, IReadOnlyCollection<CleanerArea> areas,
        IProgress<CleanerProgress>? progress, CancellationToken ct);
    Task<CleanerApplyResult> ApplyAsync(IReadOnlyCollection<Guid> findingIds, IProgress<CleanerProgress>? progress, CancellationToken ct);
}

/// <summary>Windows boundary for discovery, evidence validation and cleanup.</summary>
public interface ICleanerBackend
{
    string BackupDirectory { get; }
    Task<IReadOnlyList<CleanerApplication>> GetApplicationsAsync(CancellationToken ct);
    Task<CleanerApplicationInventory> GetApplicationsAsync(CleanerOptions options, CancellationToken ct);
    Task<IReadOnlyList<CleanerRegistryAccount>> GetRegistryAccountsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetDrivesAsync(CancellationToken ct);
    Task<CleanerScanResult> ScanAsync(CleanerArea area, CleanerOptions options, IProgress<CleanerProgress>? progress, CancellationToken ct);
    Task CleanAsync(CleanerFinding finding, CleanerOptions options, CancellationToken ct);
}
