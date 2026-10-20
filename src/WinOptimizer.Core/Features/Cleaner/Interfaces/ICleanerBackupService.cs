using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Core.Features.Cleaner.Interfaces;

/// <summary>Creates durable cleanup snapshots before mutation and restores individual missing resources.</summary>
public interface ICleanerBackupService
{
    Task<CleanerBackupInfo> CreateAsync(IReadOnlyList<CleanerFinding> findings, CancellationToken ct);
    Task<IReadOnlyList<CleanerBackupInfo>> ListAsync(CancellationToken ct);
    Task<CleanerRestoreResult> RestoreAsync(Guid backupId, CancellationToken ct);
}
