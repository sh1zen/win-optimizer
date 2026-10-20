namespace WinOptimizer.Core.Features.SoftwareApps.Models;

public enum UninstallArtifactKind { File, Directory, RegistryValue, RegistryKey }
public enum UninstallVerificationState { Removed, StillInstalled, Unknown, Deferred }
public enum AppUninstallKind { Windows, External }
public enum UninstallReviewDisposition { Completed, Deferred }

/// <summary>A reviewed resource identity; mutation accepts its opaque ID rather than a UI-supplied path.</summary>
public sealed record UninstallResidue(Guid Id, string AppId, string AppName, string Account,
    UninstallArtifactKind Kind, string Path, string? ValueName, string Data, long Bytes,
    DateTime? ModifiedUtc, bool View32, bool CanDelete, string Reason);

public sealed record UninstallNativeOutcome(string AppId, bool Success, bool Deferred, string? Message);
public sealed record UninstallVerification(string AppId, string AppName, UninstallVerificationState State, string Detail);
public sealed record UninstallResidueScan(IReadOnlyList<UninstallResidue> Findings,
    IReadOnlyList<UninstallVerification> Applications, IReadOnlyList<string> Notices, int ObservedChanges);
public sealed record UninstallResidueProgress(string Message, int Completed = 0, int Total = 0);
public sealed record UninstallCleanupResult(IReadOnlyList<Guid> Removed, IReadOnlyList<string> Errors, Guid? BackupId);
public sealed record UninstallCleanupBackup(Guid Id, DateTime CreatedUtc, string Applications, int Items);
public sealed record UninstallRecoveryResult(int Restored, IReadOnlyList<string> Errors);
public sealed record AppUninstallResult(IReadOnlyList<UninstallVerification> Applications, IReadOnlyList<string> Notices);
