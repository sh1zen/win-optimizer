namespace WinOptimizer.Core.Features.SoftwareApps.Models;

public enum InstallFailureReason
{
    None,
    PackageNotFound,
    HashMismatchOrInstallError,
    DownloadError,
    NetworkError,
    BlockedByPolicy,
    NoApplicableInstallers,
    AgreementsNotAccepted,
    WinGetNotAvailable,
    UserCancelled,
    Other
}
