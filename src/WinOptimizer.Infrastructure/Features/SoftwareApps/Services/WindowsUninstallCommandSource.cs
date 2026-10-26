using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Reads the selected product's registered native uninstall command in the interactive account.</summary>
public interface IUninstallCommandSource
{
    Task<(bool Found, string UninstallString)> GetAsync(ItemDefinition item);
}

/// <summary>Resolves one matching registration through the shared machine and interactive-user inventory.</summary>
public sealed class WindowsUninstallCommandSource(ILogService logService,
    IInstalledProgramSource installedPrograms) : IUninstallCommandSource
{
    /// <summary>Requires an exact registration or an unambiguous catalogue match, then rereads its command.</summary>
    public async Task<(bool Found, string UninstallString)> GetAsync(ItemDefinition item)
    {
        var keyPath = item.RegistryUninstallKeyPath;
        if (keyPath is null)
        {
            var registrations = await installedPrograms.ReadAsync().ConfigureAwait(false);
            var matches = registrations.Where(program => program.Matches(item)).Take(2).ToArray();
            if (matches.Length != 1)
            {
                if (matches.Length > 1)
                    logService.LogWarning($"Native uninstall registration is ambiguous for {item.Name}; use a package-specific removal method.");
                return (false, string.Empty);
            }
            keyPath = matches[0].KeyPath;
        }

        var command = await installedPrograms.GetUninstallCommandAsync(keyPath).ConfigureAwait(false);
        return (!string.IsNullOrWhiteSpace(command), command ?? string.Empty);
    }
}
