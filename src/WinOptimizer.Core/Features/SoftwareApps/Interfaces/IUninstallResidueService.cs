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
    /// <summary>Verifies native completion without enumerating leftover files or registry resources.</summary>
    Task<AppUninstallResult> VerifyAsync(IReadOnlyList<UninstallNativeOutcome> outcomes,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct);
    /// <summary>Scans leftovers on explicit request, rechecking the retained native outcomes.</summary>
    Task<UninstallResidueScan> ScanAsync(IProgress<UninstallResidueProgress>? progress, CancellationToken ct);
    Task<UninstallCleanupResult> CleanAsync(IReadOnlyCollection<Guid> selected,
        IProgress<UninstallResidueProgress>? progress, CancellationToken ct);
}

/// <summary>Desktop interaction boundary for reviewing individually selectable uninstall leftovers.</summary>
public interface IUninstallResidueReview
{
    /// <summary>Blocks the application during the native uninstaller and restores it when disposed.</summary>
    Task<IAsyncDisposable> SuspendForUninstallAsync(CancellationToken ct);
    /// <summary>Asks before automatic removal when the application has no registered native uninstaller.</summary>
    Task<bool> ConfirmAutomaticUninstallAsync(ItemDefinition application, CancellationToken ct);
    /// <summary>A deferred review takes ownership of the session until its deferral scope is disposed.</summary>
    Task<UninstallReviewDisposition> ShowAsync(IUninstallResidueSession session, AppUninstallResult verification, CancellationToken ct);
    /// <summary>Queues residue reviews until the enclosing configuration flow has closed its overlay.</summary>
    IAsyncDisposable Defer();
    Task ShowBackupsAsync();
}

/// <summary>Runs native removal between identity capture and verified, user-reviewed residue cleanup.</summary>
public interface IAppUninstallWorkflow
{
    /// <summary>Publishes verified outcomes to the caller's cached inventory before opening residue review.</summary>
    Task<AppUninstallResult> UninstallAsync(IReadOnlyList<ItemDefinition> applications, AppUninstallKind kind,
        bool saveRemovalScripts = true, CancellationToken ct = default, Func<AppUninstallResult, Task>? refreshInventory = null);
}
