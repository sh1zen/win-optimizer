using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Native;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Coordinates cancellable restore-point preparation and verifies native creation.</summary>
public class SystemBackupService : ISystemBackupService
{
    private readonly ILogService _logService;
    private readonly ILocalizationService _localization;
    private readonly ISystemBackupBackend _backend;
    private readonly TimeProvider _clock;

    private const int VerificationMaxRetries = 10;
    private static readonly TimeSpan VerificationRetryDelay = TimeSpan.FromSeconds(3);

    public SystemBackupService(ILogService logService, ILocalizationService localization,
        IProcessExecutor processExecutor, ISystemRestoreService systemRestoreService)
        : this(logService, localization, new WindowsSystemBackupBackend(logService, processExecutor, systemRestoreService), TimeProvider.System) { }

    /// <summary>Supplies native operations and time explicitly so cancellation can be tested without Windows mutations.</summary>
    internal SystemBackupService(ILogService logService, ILocalizationService localization,
        ISystemBackupBackend backend, TimeProvider clock)
    { _logService = logService; _localization = localization; _backend = backend; _clock = clock; }

    public async Task<BackupResult> CreateRestorePointAsync(
        string? name = null,
        IProgress<TaskProgressDetail>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var restorePointName = name
                ?? $"win-optimizer Restore Point - {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";

            _logService.Log(LogLevel.Info, $"Creating restore point '{restorePointName}'...");

            // Check if System Restore is enabled
            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_CheckingRestoreStatus"),
                IsIndeterminate = true
            });

            cancellationToken.ThrowIfCancellationRequested();
            var isEnabled = _backend.IsEnabled();
            cancellationToken.ThrowIfCancellationRequested();
            if (!isEnabled)
            {
                _logService.Log(LogLevel.Warning, "System Restore is currently disabled, enabling...");

                progress?.Report(new TaskProgressDetail
                {
                    StatusText = _localization.GetString("Progress_EnablingRestore"),
                    IsIndeterminate = true
                });

                cancellationToken.ThrowIfCancellationRequested();
                var enabled = await _backend.EnableAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!enabled)
                {
                    _logService.Log(LogLevel.Error, "Failed to enable System Restore");
                    return BackupResult.CreateFailure(
                        "Failed to enable System Restore - cannot create restore point");
                }

                _logService.Log(LogLevel.Info, "System Restore enabled successfully");
            }

            // Ensure shadow storage has enough free space
            cancellationToken.ThrowIfCancellationRequested();
            await _backend.EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Create the restore point
            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_CreatingRestorePoint"),
                IsIndeterminate = true
            });

            cancellationToken.ThrowIfCancellationRequested();
            var (apiSuccess, statusCode) = await _backend.CreateAsync(restorePointName, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!apiSuccess)
            {
                var statusDesc = SrClientApi.GetStatusDescription(statusCode);
                _logService.Log(LogLevel.Error, $"Failed to create restore point. Status: {statusCode} ({statusDesc})");
                return BackupResult.CreateFailure($"Failed to create system restore point: {statusDesc}");
            }

            if (statusCode != SrClientApi.ERROR_SUCCESS)
            {
                var statusDesc = SrClientApi.GetStatusDescription(statusCode);
                _logService.Log(LogLevel.Warning, $"SRSetRestorePointW returned success but status code is {statusCode} ({statusDesc})");
            }

            // Verify creation
            _logService.Log(LogLevel.Info, "Restore point API call succeeded, verifying creation...");

            var verifiedDate = await VerifyRestorePointCreatedAsync(restorePointName, cancellationToken).ConfigureAwait(false);

            if (verifiedDate != null)
            {
                _logService.Log(LogLevel.Info, $"Successfully verified restore point '{restorePointName}' (created: {verifiedDate.Value})");
                return BackupResult.CreateSuccess(
                    restorePointDate: verifiedDate.Value,
                    restorePointCreated: true);
            }
            else
            {
                _logService.Log(LogLevel.Error, $"Restore point '{restorePointName}' could not be verified after creation.");
                return BackupResult.CreateFailure("System restore point creation could not be verified");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error creating restore point: {ex.Message}");
            return BackupResult.CreateFailure(ex.Message);
        }
    }

    private async Task<DateTime?> VerifyRestorePointCreatedAsync(string description, CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= VerificationMaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(VerificationRetryDelay, _clock, cancellationToken).ConfigureAwait(false);

            var found = await _backend.FindAsync(description, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (found != null)
            {
                _logService.Log(LogLevel.Info, $"Restore point verified on attempt {attempt}/{VerificationMaxRetries}");
                return found;
            }

            _logService.Log(LogLevel.Info, $"Restore point not yet visible (attempt {attempt}/{VerificationMaxRetries}), retrying...");
        }

        return null;
    }

}
