using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Core.Features.Common.Enums;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Isolates the Windows registry and WM_SETTINGCHANGE boundary.</summary>
public interface IEnvironmentVariableStore
{
    IReadOnlyList<EnvironmentVariableEntry> Read(EnvironmentScope scope);
    bool Write(EnvironmentScope scope, EnvironmentVariableEntry? original, EnvironmentVariableEntry? replacement);
}

/// <summary>Serializes edits, validates names/types and rejects stale or duplicate variables.</summary>
public sealed class EnvironmentEditorService(IEnvironmentVariableStore store, IApplicationModeService mode) : IEnvironmentEditorService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IReadOnlyList<EnvironmentVariableEntry>> ReadAsync(EnvironmentScope scope)
    {
        ValidateScope(scope);
        return Task.Run(() => store.Read(scope));
    }

    public Task<EnvironmentEditResult> SaveAsync(EnvironmentScope scope, EnvironmentVariableEntry? original, EnvironmentVariableEntry value)
    {
        value.Validate();
        if (original != null && !string.Equals(original.Name, value.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Create a new variable to use a different name.");
        return EditAsync(scope, original, value);
    }

    public Task<EnvironmentEditResult> DeleteAsync(EnvironmentScope scope, EnvironmentVariableEntry original) => EditAsync(scope, original, null);

    private async Task<EnvironmentEditResult> EditAsync(EnvironmentScope scope, EnvironmentVariableEntry? original, EnvironmentVariableEntry? replacement)
    {
        ValidateScope(scope);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var reservation = mode.BeginSystemOperation();
            if (mode.CurrentMode != WinOptimizerMode.Normal) throw new InvalidOperationException("Environment edits require normal application mode.");
            return await Task.Run(() =>
            {
                var name = original?.Name ?? replacement!.Name;
                var current = store.Read(scope).SingleOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (current != original)
                    throw new UtilityOperationException("The variable changed outside this editor or already exists. Reload before saving.");
                return new EnvironmentEditResult(store.Write(scope, original, replacement));
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static void ValidateScope(EnvironmentScope scope)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
    }
}
