using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Owns scan snapshots, mode reservations and authorization of individual startup changes.</summary>
internal sealed class AutorunService(IAutorunBackend backend, IApplicationModeService mode,
    IInteractiveUserService user, IScheduledTaskService tasks) : IAutorunService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string> _startupExecutable = ApplicationStartupExecutable.FindProtectedInstallation;
    private Dictionary<string, AutorunEntry> _editable = new(StringComparer.Ordinal);

    /// <summary>Provides a test boundary for discovery of the installed executable.</summary>
    internal AutorunService(IAutorunBackend backend, IApplicationModeService mode, IInteractiveUserService user,
        IScheduledTaskService tasks, Func<string> startupExecutable) : this(backend, mode, user, tasks)
    {
        _startupExecutable = startupExecutable;
    }

    public async Task<AutorunInventory> ReadAsync(AutorunView view, bool acceptSysinternalsLicense, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(view)) throw new ArgumentOutOfRangeException(nameof(view));
        if (view == AutorunView.Advanced && !acceptSysinternalsLicense)
            throw new UtilityOperationException("Accept the Sysinternals license before requesting the advanced scan.");
        if (view == AutorunView.Startup && user.IsOtsElevation)
            throw new UtilityOperationException("Open this tool using your own Windows account. Startup controls cannot target a different administrator's profile. Advanced scans can inspect all accessible profiles.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A failed refresh invalidates old write authority rather than retaining stale controls.
            _editable.Clear();
            var inventory = await backend.ReadAsync(view, acceptSysinternalsLicense, cancellationToken).ConfigureAwait(false);
            if (inventory.View != view || inventory.Entries.Any(entry => string.IsNullOrEmpty(entry.Id)) ||
                inventory.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != inventory.Entries.Count)
                throw new UtilityOperationException("The startup inventory contains invalid or duplicate identities.");
            if (view == AutorunView.Startup)
                _editable = inventory.Entries.Where(entry => entry.CanToggle && entry.Enabled.HasValue)
                    .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            return inventory;
        }
        finally { _gate.Release(); }
    }

    public async Task SetEnabledAsync(string entryId, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var reservation = mode.BeginSystemOperation();
            if (mode.CurrentMode != WinOptimizerMode.Normal || user.IsOtsElevation)
                throw new InvalidOperationException("Startup changes require normal mode and the current user's own account.");
            if (!_editable.TryGetValue(entryId, out var entry))
                throw new UtilityOperationException("Refresh the startup list before changing this entry.");
            if (entry.Enabled == enabled) return;
            cancellationToken.ThrowIfCancellationRequested();
            // Native writes finish as one unit. Cancellation never kills a backup/restore halfway through.
            await backend.SetEnabledAsync(entry, enabled).ConfigureAwait(false);
            _editable.Remove(entryId);
        }
        finally { _gate.Release(); }
    }

    public Task OpenStartupSettingsAsync() => backend.OpenStartupSettingsAsync();

    /// <summary>Reads the scheduler directly so externally disabled tasks are reflected when refreshed.</summary>
    public async Task<bool?> ReadApplicationStartupAsync() => user.IsOtsElevation ? null :
        await tasks.IsUserLogonApplicationEnabledAsync(ApplicationTaskName(AppDataPaths.InteractiveUserSid(user)))
            .ConfigureAwait(false);

    /// <summary>Registers the normal app launch for the consenting administrator's Windows session.</summary>
    public async Task SetApplicationStartupAsync(bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = mode.BeginSystemOperation();
            if (mode.CurrentMode != WinOptimizerMode.Normal || user.IsOtsElevation)
                throw new InvalidOperationException("Startup changes require normal mode and the current user's own account.");
            var sid = AppDataPaths.InteractiveUserSid(user);
            var executable = enabled ? await Task.Run(_startupExecutable).ConfigureAwait(false) : "";
            var result = await tasks.ConfigureUserLogonApplicationAsync(ApplicationTaskName(sid),
                executable, "", sid, enabled).ConfigureAwait(false);
            if (!result.Success)
                throw new UtilityOperationException(result.ErrorMessage ?? "Unable to configure Windows startup.", result.Exception);
        }
        finally { _gate.Release(); }
    }

    private static string ApplicationTaskName(string sid) => $"WinOptimizer-{sid}";
}

/// <summary>Isolates Windows discovery, verified Sysinternals acquisition and reversible native writes.</summary>
internal interface IAutorunBackend
{
    Task<AutorunInventory> ReadAsync(AutorunView view, bool acceptLicense, CancellationToken cancellationToken);
    Task SetEnabledAsync(AutorunEntry entry, bool enabled);
    Task OpenStartupSettingsAsync();
}
