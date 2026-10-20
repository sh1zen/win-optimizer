using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Customize.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Customize.Services;

/// <summary>Isolates discovery and exact registration edits from application-mode and snapshot authority.</summary>
public interface IContextMenuStore
{
    IReadOnlyList<ContextMenuEntry> Read();
    ContextMenuEntry SetEnabled(ContextMenuEntry original, bool enabled);
}

/// <summary>Serializes inventory and edits, requiring a known, unchanged registration and normal mode.</summary>
public sealed class ContextMenuService(IContextMenuStore store, IApplicationModeService mode) : IContextMenuService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ContextMenuEntry> _entries = [];

    public async Task<IReadOnlyList<ContextMenuEntry>> ReadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return _entries = await Task.Run(store.Read).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<ContextMenuEntry> SetEnabledAsync(ContextMenuEntry entry, bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = mode.BeginSystemOperation();
            if (mode.CurrentMode != WinOptimizerMode.Normal)
                throw new InvalidOperationException("Context menu changes require normal application mode.");
            if (!_entries.Contains(entry))
                throw new InvalidOperationException("This registration is no longer in the loaded inventory. Refresh before changing it.");
            var result = await Task.Run(() => store.SetEnabled(entry, enabled)).ConfigureAwait(false);
            _entries = _entries.Select(item => item.Id == result.Id ? result : item).ToArray();
            return result;
        }
        finally { _gate.Release(); }
    }
}
