using System;
using System.Globalization;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Native;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Isolates WMI, shadow-storage processes and restore-point native calls from orchestration.</summary>
internal interface ISystemBackupBackend
{
    bool IsEnabled();
    Task<bool> EnableAsync(CancellationToken cancellationToken);
    Task EnsureStorageAsync(CancellationToken cancellationToken);
    Task<(bool Success, int StatusCode)> CreateAsync(string description, CancellationToken cancellationToken);
    Task<DateTime?> FindAsync(string description, CancellationToken cancellationToken);
}

/// <summary>Owns native restore-point resources, including restoration of the frequency throttle.</summary>
internal sealed class WindowsSystemBackupBackend(ILogService logService, IProcessExecutor processExecutor,
    ISystemRestoreService systemRestoreService) : ISystemBackupBackend
{
    private readonly ILogService _logService = logService;
    private readonly IProcessExecutor _processExecutor = processExecutor;
    private const double MinFreeStoragePercent = 15.0;
    public bool IsEnabled() => systemRestoreService.IsEnabledForC();

    public async Task<DateTime?> FindAsync(string description, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logService.Log(LogLevel.Info, $"Querying for restore point: '{description}'");

                var escapedDescription = description.Replace("'", "\\'");
                using var searcher = new ManagementObjectSearcher(
                    @"root\default",
                    $"SELECT * FROM SystemRestore WHERE Description = '{escapedDescription}'");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    using (obj)
                    {
                        _logService.Log(LogLevel.Info, $"Found existing restore point: '{description}'");

                        var creationTimeStr = obj["CreationTime"]?.ToString();
                        if (creationTimeStr != null)
                        {
                            return (DateTime?)ManagementDateTimeConverter.ToDateTime(creationTimeStr);
                        }
                        return (DateTime?)DateTime.Now;
                    }
                }

                _logService.Log(LogLevel.Info, $"No restore point found with description: '{description}'");
                return (DateTime?)null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error querying restore point: {ex.Message}");
                return (DateTime?)null;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool Success, int StatusCode)> CreateAsync(string description, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Windows throttles restore point creation to once per 24 hours by default.
                // SRSetRestorePointW silently returns success without creating a restore point
                // if one was already created within the frequency window.
                // Temporarily set the frequency to 0 (no limit) so our call always creates one.
                DisableRestorePointFrequencyThrottle(out var previousValue);

                try
                {
                    var restorePointInfo = new SrClientApi.RESTOREPOINTINFO
                    {
                        dwEventType = SrClientApi.BEGIN_SYSTEM_CHANGE,
                        dwRestorePtType = SrClientApi.MODIFY_SETTINGS,
                        llSequenceNumber = 0,
                        szDescription = description
                    };

                    cancellationToken.ThrowIfCancellationRequested();
                    var success = SrClientApi.SRSetRestorePointW(ref restorePointInfo, out var status);
                    if (!success)
                    {
                        _logService.Log(LogLevel.Error, $"Failed to create restore point. Status: {status.nStatus} ({SrClientApi.GetStatusDescription(status.nStatus)})");
                    }
                    return (success, status.nStatus);
                }
                finally
                {
                    RestoreRestorePointFrequencyThrottle(previousValue);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Failed to create restore point: {ex.Message}");
                return (false, -1);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private const string SystemRestoreKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValueName = "SystemRestorePointCreationFrequency";

    private void DisableRestorePointFrequencyThrottle(out int? previousValue)
    {
        previousValue = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKeyPath, writable: true);
            if (key == null) return;

            var existing = key.GetValue(FrequencyValueName);
            if (existing is int intVal)
            {
                previousValue = intVal;
                if (intVal == 0) return; // Already disabled
            }

            key.SetValue(FrequencyValueName, 0, RegistryValueKind.DWord);
            _logService.Log(LogLevel.Info, "Temporarily disabled restore point creation frequency throttle");
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Could not disable restore point frequency throttle: {ex.Message}");
        }
    }

    private void RestoreRestorePointFrequencyThrottle(int? previousValue)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKeyPath, writable: true);
            if (key == null) return;

            if (previousValue.HasValue)
            {
                key.SetValue(FrequencyValueName, previousValue.Value, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(FrequencyValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Could not restore frequency throttle value: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks shadow storage usage and increases MaxSize if free space is below threshold.
    /// </summary>
    public async Task EnsureStorageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

            var result = await _processExecutor.ExecuteAsync(
                "vssadmin",
                $"list shadowstorage /For={systemDrive}\\", cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                _logService.Log(LogLevel.Warning, "Could not query shadow storage usage, skipping resize check");
                return;
            }

            var (usedBytes, maxBytes) = ParseShadowStorageOutput(result.StandardOutput);
            if (usedBytes < 0 || maxBytes <= 0)
            {
                _logService.Log(LogLevel.Warning, "Could not parse shadow storage values, skipping resize check");
                return;
            }

            var freePercent = (1.0 - (double)usedBytes / maxBytes) * 100.0;
            _logService.Log(LogLevel.Info, $"Shadow storage: used {usedBytes / (1024 * 1024)} MB / max {maxBytes / (1024 * 1024)} MB ({freePercent:F1}% free)");

            if (freePercent < MinFreeStoragePercent)
            {
                var newMaxBytes = maxBytes * 2;
                var newMaxGb = newMaxBytes / (1024L * 1024 * 1024);
                // Cap at a reasonable size and use at least 1 GB
                newMaxGb = Math.Clamp(newMaxGb, 1, 64);

                _logService.Log(LogLevel.Info, $"Shadow storage nearly full ({freePercent:F1}% free), resizing max to {newMaxGb} GB");

                cancellationToken.ThrowIfCancellationRequested();
                await _processExecutor.ExecuteAsync(
                    "vssadmin",
                    $"Resize ShadowStorage /For={systemDrive}\\ /On={systemDrive}\\ /MaxSize={newMaxGb}GB", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Warning, $"Shadow storage check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses vssadmin list shadowstorage output to extract Used and Maximum bytes.
    /// Returns (-1, -1) if parsing fails.
    /// </summary>
    private static (long UsedBytes, long MaxBytes) ParseShadowStorageOutput(string output)
    {
        long usedBytes = -1;
        long maxBytes = -1;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("Used Shadow Copy Storage space:", StringComparison.OrdinalIgnoreCase))
            {
                usedBytes = ParseByteValue(line);
            }
            else if (line.StartsWith("Maximum Shadow Copy Storage space:", StringComparison.OrdinalIgnoreCase))
            {
                maxBytes = ParseByteValue(line);
            }
        }

        return (usedBytes, maxBytes);
    }

    /// <summary>
    /// Parses a vssadmin line like "Used Shadow Copy Storage space: 9.25 GB (14%)"
    /// and returns the value in bytes. Returns -1 on failure.
    /// </summary>
    private static long ParseByteValue(string line)
    {
        // Extract the part after the colon, e.g. " 9.25 GB (14%)"
        var colonIndex = line.IndexOf(':');
        if (colonIndex < 0) return -1;

        var valuePart = line[(colonIndex + 1)..].Trim();

        // Remove the parenthetical percentage if present, e.g. "(14%)"
        var parenIndex = valuePart.IndexOf('(');
        if (parenIndex > 0)
            valuePart = valuePart[..parenIndex].Trim();

        // Split into number and unit, e.g. ["9.25", "GB"]
        var parts = valuePart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return -1;

        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return -1;

        var unit = parts[1].ToUpperInvariant();
        var multiplier = unit switch
        {
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => -1L
        };

        if (multiplier < 0) return -1;
        return (long)(number * multiplier);
    }

    public async Task<bool> EnableAsync(CancellationToken cancellationToken)
    {
        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

            // Enable System Restore via WMI (blocking COM call, run on thread pool)
            await Task.Run(() =>
            {
                using var restoreClass = new ManagementClass(
                    new ManagementScope(@"\\.\root\default"),
                    new ManagementPath("SystemRestore"),
                    new ObjectGetOptions());

                var inParams = restoreClass.GetMethodParameters("Enable");
                inParams["Drive"] = systemDrive + "\\";
                cancellationToken.ThrowIfCancellationRequested();
                restoreClass.InvokeMethod("Enable", inParams, null);
            }, cancellationToken).ConfigureAwait(false);

            _logService.Log(LogLevel.Info, "System Restore enabled via WMI");

            cancellationToken.ThrowIfCancellationRequested();
            // Resize shadow storage
            await _processExecutor.ExecuteAsync(
                "vssadmin",
                $"Resize ShadowStorage /For={systemDrive}\\ /On={systemDrive}\\ /MaxSize=20GB", cancellationToken)
                .ConfigureAwait(false);

            _logService.Log(LogLevel.Info, "Shadow storage resized");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Failed to enable System Restore: {ex.Message}");
            return false;
        }
    }
}
