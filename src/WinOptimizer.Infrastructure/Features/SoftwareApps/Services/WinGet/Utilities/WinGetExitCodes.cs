using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;

/// <summary>
/// Maps WinGet CLI HRESULT exit codes to <see cref="InstallFailureReason"/>.
/// Reference: https://github.com/microsoft/winget-cli/blob/v1.29.380/doc/windows/package-manager/winget/returnCodes.md
/// </summary>
public static class WinGetExitCodes
{
    // Success codes
    public const int Ok = 0;
    public const int RestartRequired = unchecked((int)0x8A150109);

    // Error codes (HRESULT values cast to int for exit code comparison)
    public const int PackageNotFound = unchecked((int)0x8A150014);
    public const int InstallerHashMismatch = unchecked((int)0x8A150011);
    public const int DownloadError = unchecked((int)0x8A150008);
    public const int BlockedByPolicy = unchecked((int)0x8A15003A);
    public const int NoApplicableInstallers = unchecked((int)0x8A150010);
    public const int PackageAgreementsNotAccepted = unchecked((int)0x8A150041);
    public const int NetworkError = unchecked((int)0x8A150107);
    public const int AlreadyInstalled = unchecked((int)0x8A150061);
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    public const int PackageNotInstalled = PackageNotFound;
    public const int FailedToOpenAllSources = unchecked((int)0x8A15004B);
    public const int ManifestError = unchecked((int)0x8A15002A);
    public const int OperationCancelled = unchecked((int)0x8A150005);
    public const int InstallerCancelled = unchecked((int)0x8A15010C);
    public const int CommandRequiresAdministrator = unchecked((int)0x8A150019);
    public const int NoSourcesDefined = unchecked((int)0x8A150015);

    // Uninstall-specific error codes
    // The underlying uninstaller returned a non-zero exit code (e.g. Chromium-based
    // apps always return 19 even on successful uninstall).
    public const int ExecUninstallCommandFailed = unchecked((int)0x8A150030);
    public const int NoUninstallInfoFound = unchecked((int)0x8A15002F);

    public static bool IsSuccess(int exitCode)
        => exitCode == Ok || exitCode == RestartRequired
        || exitCode == AlreadyInstalled || exitCode == UpdateNotApplicable;

    /// <summary>
    /// Returns true if the exit code indicates a potential false-positive uninstall failure
    /// that should be verified by checking whether the package is actually still installed.
    /// WinGet wraps any non-zero exit code from the underlying uninstaller into
    /// EXEC_UNINSTALL_COMMAND_FAILED, even when the uninstall actually succeeded
    /// (e.g. Chromium-based apps always return exit code 19).
    /// </summary>
    public static bool IsUninstallVerifiable(int exitCode)
        => exitCode == ExecUninstallCommandFailed || exitCode == NoUninstallInfoFound;

    public static InstallFailureReason MapExitCode(int exitCode) => exitCode switch
    {
        Ok => InstallFailureReason.None,
        RestartRequired => InstallFailureReason.None,
        AlreadyInstalled => InstallFailureReason.None,
        UpdateNotApplicable => InstallFailureReason.None,
        PackageNotFound => InstallFailureReason.PackageNotFound,
        InstallerHashMismatch => InstallFailureReason.HashMismatchOrInstallError,
        DownloadError => InstallFailureReason.DownloadError,
        NetworkError => InstallFailureReason.NetworkError,
        BlockedByPolicy => InstallFailureReason.BlockedByPolicy,
        NoApplicableInstallers => InstallFailureReason.NoApplicableInstallers,
        PackageAgreementsNotAccepted => InstallFailureReason.AgreementsNotAccepted,
        ManifestError => InstallFailureReason.Other,
        OperationCancelled or InstallerCancelled => InstallFailureReason.UserCancelled,
        _ => InstallFailureReason.Other
    };
}
