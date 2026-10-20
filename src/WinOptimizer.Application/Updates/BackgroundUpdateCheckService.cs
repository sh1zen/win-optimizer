using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Updates;

/// <summary>Owns deferred, persistent daily checks for application and removal-script updates.</summary>
public sealed class BackgroundUpdateCheckService(
    IVersionService versions,
    IRemovalScriptUpdateService scripts,
    IUserPreferencesService preferences,
    TimeProvider clock,
    ILogService log,
    IApplicationModeService mode) : IBackgroundUpdateCheckService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private readonly object _startLock = new();
    private Task? _run;

    /// <summary>Starts one loop explicitly; construction and repeated starts perform no extra work.</summary>
    public Task RunAsync(Action<VersionInfo> updateAvailable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updateAvailable);
        lock (_startLock)
            return _run ??= RunCoreAsync(updateAvailable, cancellationToken);
    }

    /// <summary>Persists each attempt before running checks, including failed or interrupted attempts.</summary>
    private async Task RunCoreAsync(Action<VersionInfo> updateAvailable, CancellationToken cancellationToken)
    {
        using var logTopic = log.BeginTopic(LogTopic.Updates);
        try
        {
            // This await also moves synchronous script maintenance off the UI thread.
            await Task.Delay(StartupDelay, clock, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startedAt = clock.GetUtcNow();
                try
                {
                    var lastAttempt = await preferences.GetPreferenceAsync(
                        UserPreferenceKeys.LastAutomaticUpdateCheckUtc, DateTimeOffset.MinValue).ConfigureAwait(false);
                    startedAt = clock.GetUtcNow();
                    if (lastAttempt <= startedAt && startedAt - lastAttempt < CheckInterval)
                    {
                        await Task.Delay(CheckInterval - (startedAt - lastAttempt), clock, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (lastAttempt > startedAt)
                        log.LogWarning("Automatic update check timestamp is in the future; starting a new daily interval.");

                    var options = await preferences.GetPreferenceAsync(
                        UserPreferenceKeys.Updates, new UpdateOptions()).ConfigureAwait(false);
                    if (!options.CheckApplicationUpdatesAutomatically && !options.UpdateRemovalScriptsAutomatically)
                    {
                        await Task.Delay(CheckInterval, clock, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var saved = await preferences.SetPreferenceAsync(
                        UserPreferenceKeys.LastAutomaticUpdateCheckUtc, startedAt).ConfigureAwait(false);
                    if (!saved.Success)
                        throw new IOException($"Cannot persist the automatic update check timestamp: {saved.ErrorMessage}", saved.Exception);

                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.UpdateRemovalScriptsAutomatically)
                    {
                        try
                        {
                            // Script updates can run scheduled tasks, so they use the system-operation guard.
                            using var operation = mode.BeginSystemOperation();
                            await scripts.CheckAndUpdateScriptsAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex) { log.LogError("Background script update check failed.", ex); }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.CheckApplicationUpdatesAutomatically)
                    {
                        try
                        {
                            var latest = await versions.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);
                            if (!cancellationToken.IsCancellationRequested && latest is { IsUpdateAvailable: true })
                                updateAvailable(latest);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex) { log.LogError("Background application update check failed.", ex); }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { log.LogError("Cannot run the automatic update checks.", ex); }

                var remaining = CheckInterval - (clock.GetUtcNow() - startedAt);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown stops delays and cancellable version requests.
        }
    }
}
