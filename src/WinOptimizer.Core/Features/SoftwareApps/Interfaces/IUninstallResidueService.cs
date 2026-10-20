using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Core.Features.SoftwareApps.Interfaces;

/// <summary>Captures product identities before uninstall and supplies durable cleanup recovery.</summary>
public interface IUninstallResidueService
{
    Task<IUninstallResidueSession> CaptureAsync(IReadOnlyList<ItemDefinition> applications,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct);
    Task<IReadOnlyList<UninstallCleanupBackup>> GetBackupsAsync(CancellationToken ct);
    Task<UninstallRecoveryResult> RestoreAsync(Guid backupId, CancellationToken ct);
}

/// <summary>Owns targeted filesystem monitors, account-bound evidence and the lifetime of a reviewed scan.</summary>
public interface IUninstallResidueSession : IAsyncDisposable
{
    Task<UninstallResidueScan> InspectAsync(IReadOnlyList<UninstallNativeOutcome> outcomes,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct);
    /// <summary>Rechecks the same native outcomes after processes or account resources have changed.</summary>
    Task<UninstallResidueScan> RescanAsync(IProgress<UninstallResidueProgress>? progress, CancellationToken ct);
    Task<UninstallCleanupResult> CleanAsync(IReadOnlyCollection<Guid> selected,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct);
}

/// <summary>Desktop interaction boundary for reviewing individually selectable uninstall leftovers.</summary>
public interface IUninstallResidueReview
{
    /// <summary>A deferred review takes ownership of the session until its deferral scope is disposed.</summary>
    Task<UninstallReviewDisposition> ShowAsync(IUninstallResidueSession session, UninstallResidueScan scan, CancellationToken ct);
    /// <summary>Queues residue dialogs until the enclosing configuration flow has closed its overlay.</summary>
    IAsyncDisposable Defer();
    Task ShowBackupsAsync();
}

/// <summary>Runs native removal between identity capture and verified, user-reviewed residue cleanup.</summary>
public interface IAppUninstallWorkflow
{
    Task<AppUninstallResult> UninstallAsync(IReadOnlyList<ItemDefinition> applications, AppUninstallKind kind,
        bool saveRemovalScripts = true, CancellationToken ct = default);
}
