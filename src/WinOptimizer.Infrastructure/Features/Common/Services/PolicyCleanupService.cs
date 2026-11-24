using System.IO;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Clears only policy values owned by the settings catalogue.</summary>
public class PolicyCleanupService(
    ICompatibleSettingsRegistry compatibleSettingsRegistry,
    IWindowsRegistryService registryService,
    ILogService logService) : IPolicyCleanupService
{
    public int CleanupPolicyValues()
    {
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var cleared = 0;
        foreach (var setting in compatibleSettingsRegistry.GetAllBypassedSettings().Values.SelectMany(group => group))
        {
            foreach (var value in setting.RegistrySettings)
            {
                if (!value.IsGroupPolicy || string.IsNullOrWhiteSpace(value.KeyPath) || value.ValueName is not { } name)
                    continue;
                if (!seen.TryGetValue(value.KeyPath, out var names))
                    seen[value.KeyPath] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!names.Add(name)) continue;

                if (registryService.DeleteValue(value.KeyPath, name))
                {
                    cleared++;
                    continue;
                }

                var target = $@"{value.KeyPath}\{name}";
                failures.Add(target);
                logService.Log(LogLevel.Warning, $"[PolicyCleanup] Could not clear managed policy '{target}'");
            }
        }

        if (failures.Count > 0)
            throw new IOException($"Could not clear the following managed policies: {string.Join(", ", failures)}.");

        logService.Log(LogLevel.Info, $"[PolicyCleanup] Verified cleanup of {cleared} managed policy values.");
        return cleared;
    }
}
