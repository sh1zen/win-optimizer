using System;
using System.Threading;
using System.Threading.Tasks;
using WinGetPhase = WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities.WinGetProgressParser.WinGetPhase;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet;

/// <summary>
/// Handles WinGet CLI-based package install and uninstall operations.
/// </summary>
public class WinGetPackageInstaller : IWinGetPackageInstaller
{
    // No wall-clock cap (installs/uninstalls can legitimately run long); a stalled
    // process is caught by the idle-output timer instead.
    private const int IdleTimeoutMs = 180_000;

    private readonly WinGetComSession _comSession;
    private readonly ITaskProgressService _taskProgressService;
    private readonly ILogService _logService;
    private readonly ILocalizationService _localization;
    private readonly IInteractiveUserService _interactiveUserService;
    private readonly IFileSystemService _fileSystemService;

    public WinGetPackageInstaller(
        WinGetComSession comSession,
        ITaskProgressService taskProgressService,
        ILogService logService,
        ILocalizationService localization,
        IInteractiveUserService interactiveUserService,
        IFileSystemService fileSystemService)
    {
        _comSession = comSession;
        _taskProgressService = taskProgressService;
        _logService = logService;
        _localization = localization;
        _interactiveUserService = interactiveUserService;
        _fileSystemService = fileSystemService;
    }

    public async Task<bool> IsWinGetInstalledAsync(CancellationToken cancellationToken = default)
    {
        // Bundled CLI is always available if the app is correctly installed
        var exePath = WinGetCliRunner.GetWinGetExePath(_interactiveUserService);
        if (exePath != null && _fileSystemService.FileExists(exePath))
            return true;

        // Fallback: try COM init (covers edge cases)
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            return await Task.Run(() => _comSession.EnsureComInitialized(), linkedCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public async Task<PackageInstallResult> InstallPackageAsync(string packageId, string? source = null, string? displayName = null, string? installerOverride = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new ArgumentException("Package ID cannot be null or empty", nameof(packageId));

        displayName ??= packageId;

        _taskProgressService?.UpdateProgress(10, _localization.GetString("Progress_WinGet_CheckingPrerequisites", displayName));

        if (!await IsWinGetInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            _taskProgressService?.UpdateProgress(0, _localization.GetString("Progress_WinGet_FailedInstallManager", displayName));
            return PackageInstallResult.Failed(InstallFailureReason.WinGetNotAvailable, "WinGet CLI not found");
        }

        try
        {
            _taskProgressService?.UpdateProgress(20, _localization.GetString("Progress_WinGet_StartingInstallation", displayName));

            var arguments = $"install --id {packageId} --silent --accept-package-agreements --accept-source-agreements --force --disable-interactivity";
            if (!string.IsNullOrEmpty(source))
                arguments += $" --source {source}";
            if (!string.IsNullOrWhiteSpace(installerOverride))
                arguments += $" --override \"{installerOverride}\"";

            var (result, _) = await RunPackageCommandAsync(arguments, displayName, uninstall: false, cancellationToken).ConfigureAwait(false);

            // If the user cancelled while the process was running, throw before
            // checking the exit code so the OperationCanceledException handler fires
            // and the Chocolatey fallback prompt is never reached.
            cancellationToken.ThrowIfCancellationRequested();

            if (WinGetExitCodes.IsSuccess(result.ExitCode))
            {
                _taskProgressService?.UpdateProgress(100, _localization.GetString("Progress_WinGet_InstalledSuccess", displayName));
                return PackageInstallResult.Succeeded();
            }

            var failureReason = WinGetExitCodes.MapExitCode(result.ExitCode);
            var errorMessage = GetInstallErrorMessageCli(packageId, failureReason, result.ExitCode);
            _logService?.LogError($"Installation failed for {packageId}: {errorMessage} (exit code: {result.ExitCode})");
            _taskProgressService?.UpdateProgress(0, errorMessage);
            return PackageInstallResult.Failed(failureReason, errorMessage);
        }
        catch (OperationCanceledException)
        {
            _taskProgressService?.UpdateProgress(0, _localization.GetString("Progress_WinGet_InstallationCancelled", displayName));
            throw;
        }
        catch (Exception ex)
        {
            _logService?.LogError($"Error installing {packageId}: {ex.Message}");

            var errorMessage = _localization.GetString("Progress_WinGet_InstallationError", displayName, ex.Message);
            _taskProgressService?.UpdateProgress(0, errorMessage);
            return PackageInstallResult.Failed(InstallFailureReason.Other, errorMessage);
        }
    }

    public async Task<bool> UninstallPackageAsync(string packageId, string? source = null, string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new ArgumentException("Package ID cannot be null or empty", nameof(packageId));

        displayName ??= packageId;

        _taskProgressService?.UpdateProgress(10, _localization.GetString("Progress_WinGet_CheckingPrerequisitesUninstall", displayName));

        if (!await IsWinGetInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            _taskProgressService?.UpdateProgress(0, _localization.GetString("Progress_WinGet_NotInstalled"));
            return false;
        }

        try
        {
            _taskProgressService?.UpdateProgress(20, _localization.GetString("Progress_WinGet_StartingUninstallation", displayName));

            var arguments = $"uninstall --id {packageId} --silent --accept-source-agreements --force --disable-interactivity";
            if (!string.IsNullOrEmpty(source))
                arguments += $" --source {source}";

            var (result, logTag) = await RunPackageCommandAsync(arguments, displayName, uninstall: true, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var succeeded = WinGetExitCodes.IsSuccess(result.ExitCode);
            // Some uninstallers report success before exiting; WinGet can also wrap
            // a completed uninstall in a nonzero exit code. Verify either outcome.
            if (succeeded || WinGetExitCodes.IsUninstallVerifiable(result.ExitCode))
            {
                if (!succeeded)
                    _logService?.LogInformation($"[{logTag}] {packageId} returned 0x{result.ExitCode:X8} — verifying whether package was actually removed");
                _taskProgressService?.UpdateProgress(95, _localization.GetString("Progress_WinGet_VerifyingUninstall", displayName));
                bool stillInstalled = await IsPackageStillInstalledAsync(packageId, source, cancellationToken).ConfigureAwait(false);
                if (stillInstalled)
                {
                    var outcome = succeeded ? "uninstall" : "failed uninstall";
                    _logService?.LogInformation($"[{logTag}] {packageId} still detected after {outcome} — waiting for interactive uninstaller");
                    _taskProgressService?.UpdateProgress(95, _localization.GetString("Progress_WinGet_WaitingForUninstaller", displayName));
                    const int pollIntervalMs = 3000;
                    const int maxWaitMs = 60_000;
                    int elapsed = 0;
                    while (elapsed < maxWaitMs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
                        elapsed += pollIntervalMs;
                        if (!await IsPackageStillInstalledAsync(packageId, source, cancellationToken).ConfigureAwait(false))
                        {
                            stillInstalled = false;
                            var exitNote = succeeded ? string.Empty : $" (despite exit code 0x{result.ExitCode:X8})";
                            _logService?.LogInformation($"[{logTag}] {packageId} confirmed uninstalled after {elapsed / 1000}s{exitNote}");
                            break;
                        }
                    }
                }
                if (!stillInstalled && !succeeded)
                    _logService?.LogInformation($"[{logTag}] {packageId} verified as uninstalled despite WinGet exit code 0x{result.ExitCode:X8}");
                if (!stillInstalled)
                {
                    _taskProgressService?.UpdateProgress(100, _localization.GetString("Progress_WinGet_UninstalledSuccess", displayName));
                    return true;
                }
                _logService?.LogWarning($"[{logTag}] {packageId} is still installed after verification — uninstall truly failed");
                _taskProgressService?.UpdateProgress(0, $"{displayName} is still installed after removal verification.");
                return false;
            }

            var failureReason = WinGetExitCodes.MapExitCode(result.ExitCode);
            var errorMessage = GetUninstallErrorMessageCli(packageId, failureReason, result.ExitCode);
            _logService?.LogError($"Uninstallation failed for {packageId}: {errorMessage} (exit code: {result.ExitCode})");
            _taskProgressService?.UpdateProgress(0, errorMessage);
            return false;
        }
        catch (OperationCanceledException)
        {
            _taskProgressService?.UpdateProgress(0, _localization.GetString("Progress_WinGet_UninstallationCancelled", displayName));
            throw;
        }
        catch (Exception ex)
        {
            _logService?.LogError($"Error uninstalling {packageId}: {ex.Message}");
            _taskProgressService?.UpdateProgress(0, _localization.GetString("Progress_WinGet_UninstallationError", displayName, ex.Message));
            return false;
        }
    }

    /// <summary>Runs one package command with shared transcript, progress and timeout handling.</summary>
    private async Task<(WinGetCliRunner.WinGetCliResult Result, string LogTag)> RunPackageCommandAsync(
        string arguments, string displayName, bool uninstall, CancellationToken cancellationToken)
    {
        var wingetExe = WinGetCliRunner.GetWinGetExePath(_interactiveUserService) ?? "winget";
        var logTag = WinGetCliRunner.GetLogTag(wingetExe);

        _logService?.LogInformation($"[{logTag}] Running: winget {arguments}");

        // Emit metadata header for the task output dialog
        var startTime = DateTime.Now;
        _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
        {
            TerminalOutput = $"Command: {wingetExe} {arguments}"
        });
        _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
        {
            TerminalOutput = $"Start Time: \"{startTime:yyyy/MM/dd HH:mm:ss}\""
        });
        _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
        {
            TerminalOutput = "---"
        });

        var lastProgressReport = DateTime.MinValue;

        var lastLoggedPhase = (WinGetPhase?)null;

        void HandleOutputLine(string line)
        {
            try
            {
                // ConPTY also emits transient progress bars as permanent lines on LF.
                if (IsProgressBarLine(line))
                    return;

                var displayLine = WinGetProgressParser.TranslateLine(line);

                var progress = WinGetProgressParser.ParseLine(line);
                if (progress != null)
                {
                    if (progress.Phase != lastLoggedPhase
                        && (progress.Phase is WinGetPhase.Found
                            or WinGetPhase.Complete
                            or WinGetPhase.Error
                            || progress.Phase == (uninstall ? WinGetPhase.Uninstalling : WinGetPhase.Installing)))
                    {
                        lastLoggedPhase = progress.Phase;
                        if (displayLine != null)
                            _logService?.LogInformation($"[{logTag}] {displayLine}");
                    }

                    var (progressPercent, statusKey) = progress.Phase switch
                    {
                        WinGetPhase.Found => (30, "Progress_WinGet_FoundPackage"),
                        WinGetPhase.Downloading when !uninstall => (30 + (int)((progress.Percent ?? 0) * 0.4), "Progress_Downloading"),
                        WinGetPhase.Installing when !uninstall => (70 + (int)((progress.Percent ?? 0) * 0.25), "Progress_Installing"),
                        WinGetPhase.Uninstalling when uninstall => (60, "Progress_Uninstalling"),
                        WinGetPhase.Complete => (100, uninstall ? "Progress_WinGet_UninstalledSuccess" : "Progress_WinGet_InstalledSuccess"),
                        _ => (50, "Progress_Processing")
                    };
                    var statusText = _localization.GetString(statusKey, displayName);

                    var now = DateTime.UtcNow;
                    if (progress.Phase == WinGetPhase.Complete
                        || (now - lastProgressReport).TotalMilliseconds >= 250)
                    {
                        lastProgressReport = now;
                        _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
                        {
                            Progress = progressPercent,
                            StatusText = statusText,
                            TerminalOutput = progress.Percent.HasValue && progress.Phase != WinGetPhase.Complete
                                ? null : (displayLine ?? line),
                        });
                    }
                }
                else if (displayLine != null)
                {
                    _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
                    {
                        TerminalOutput = displayLine
                    });
                }
            }
            catch (Exception ex)
            {
                _logService?.LogWarning($"Progress reporting error (ignored): {ex.Message}");
            }
        }

        void HandleErrorLine(string line)
        {
            _logService?.LogWarning($"[{logTag}-err] {line}");
            // Keep native errors visible in the task transcript.
            _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
            {
                TerminalOutput = line
            });
        }

        void HandleProgressLine(string line)
        {
            try
            {
                var displayLine = WinGetProgressParser.TranslateLine(line);
                _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
                {
                    TerminalOutput = displayLine ?? line,
                    IsProgressIndicator = true
                });
            }
            catch (Exception ex)
            {
                _logService?.LogWarning($"Progress reporting error (ignored): {ex.Message}");
            }
        }

        var result = await WinGetCliRunner.RunAsync(
            arguments,
            onOutputLine: HandleOutputLine,
            onErrorLine: HandleErrorLine,
            cancellationToken: cancellationToken,
            timeoutMs: 0,
            idleTimeoutMs: IdleTimeoutMs,
            interactiveUserService: _interactiveUserService,
            onProgressLine: HandleProgressLine).ConfigureAwait(false);

        // Emit metadata footer for the task output dialog
        var endTime = DateTime.Now;
        _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
        {
            TerminalOutput = "---"
        });

        // Explain app-owned termination separately from WinGet exit codes.
        var terminationNote = WinGetCliRunner.DescribeTermination(result, timeoutMs: 0, idleTimeoutMs: IdleTimeoutMs);
        if (terminationNote != null)
        {
            _logService?.LogWarning($"[{logTag}] {terminationNote}");
            _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
            {
                TerminalOutput = terminationNote
            });
        }

        _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
        {
            TerminalOutput = $"End Time: \"{endTime:yyyy/MM/dd HH:mm:ss}\""
        });
        _taskProgressService?.UpdateDetailedProgress(new TaskProgressDetail
        {
            TerminalOutput = $"Process return value: \"{result.ExitCode}\" (0x{result.ExitCode:X8})"
        });

        return (result, logTag);
    }

    /// <summary>
    /// Queries the exact installed identity; only an explicit no-applications result proves absence.
    /// Inventory errors propagate to the uninstall failure handler, and cancellation propagates to its caller.
    /// </summary>
    private async Task<bool> IsPackageStillInstalledAsync(string packageId, string? source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var arguments = $"list --exact --id {packageId} --accept-source-agreements --disable-interactivity";
        if (!string.IsNullOrEmpty(source))
            arguments += $" --source {source}";

        var result = await WinGetCliRunner.RunAsync(
            arguments,
            cancellationToken: cancellationToken,
            timeoutMs: 10_000,
            interactiveUserService: _interactiveUserService).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (result.Termination == WinGetCliRunner.TerminationReason.None)
        {
            if (result.ExitCode == WinGetExitCodes.Ok) return true;
            if (result.ExitCode == WinGetExitCodes.PackageNotFound) return false;
        }
        throw new IOException($"Cannot verify removal of {packageId}: WinGet inventory exited with 0x{result.ExitCode:X8} ({result.Termination}).");
    }

    /// <summary>
    /// Returns true if the line is a progress bar (contains Unicode block elements).
    /// These lines arrive as permanent output via onOutputLine when ConPTY re-emits
    /// a \r\n terminated progress bar, but they are already handled by onProgressLine.
    /// </summary>
    private static bool IsProgressBarLine(string line)
    {
        foreach (char c in line)
        {
            if (c >= '\u2588' && c <= '\u258F') return true;
            if (c == '\u2591') return true;
        }
        return false;
    }

    private string GetInstallErrorMessageCli(string packageId, InstallFailureReason reason, int exitCode)
    {
        return reason switch
        {
            InstallFailureReason.PackageNotFound => _localization.GetString("Progress_WinGet_PackageNotFound", packageId),
            InstallFailureReason.BlockedByPolicy => _localization.GetString("Progress_WinGet_Error_BlockedByPolicy", packageId),
            InstallFailureReason.DownloadError => _localization.GetString("Progress_WinGet_Error_DownloadError", packageId),
            InstallFailureReason.HashMismatchOrInstallError => _localization.GetString("Progress_WinGet_Error_InstallError", packageId, exitCode),
            InstallFailureReason.NoApplicableInstallers => _localization.GetString("Progress_WinGet_Error_NoApplicableInstallers", packageId),
            InstallFailureReason.AgreementsNotAccepted => _localization.GetString("Progress_WinGet_Error_AgreementsNotAccepted", packageId),
            InstallFailureReason.NetworkError => _localization.GetString("Progress_WinGet_NetworkError", packageId),
            _ => _localization.GetString("Progress_WinGet_Error_InstallFailed", packageId, exitCode)
        };
    }

    private string GetUninstallErrorMessageCli(string packageId, InstallFailureReason reason, int exitCode)
    {
        return reason switch
        {
            InstallFailureReason.PackageNotFound => _localization.GetString("Progress_WinGet_PackageNotInstalled", packageId),
            InstallFailureReason.BlockedByPolicy => _localization.GetString("Progress_WinGet_Error_UninstallBlockedByPolicy", packageId),
            _ => _localization.GetString("Progress_WinGet_Error_UninstallFailed", packageId, exitCode)
        };
    }
}
