using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

public class ExternalAppsService(
    ILogService logService,
    IWinGetBootstrapper winGetBootstrapper,
    IAppStatusDiscoveryService appStatusDiscoveryService,
    IExternalAppUninstallService externalAppUninstallService,
    ITaskProgressService taskProgressService,
    IChangeHistoryService changeHistory,
    IInstalledProgramSource installedPrograms) : IExternalAppsService
{
    public string DomainName => FeatureIds.ExternalApps;

    public event EventHandler? WinGetReady
    {
        add => winGetBootstrapper.WinGetInstalled += value;
        remove => winGetBootstrapper.WinGetInstalled -= value;
    }

    public void InvalidateStatusCache() => appStatusDiscoveryService.InvalidateCache();

    /// <summary>Merges registered installed programs with the app catalogue.</summary>
    public async Task<IEnumerable<ItemDefinition>> GetAppsAsync() =>
        MergeCatalogue(await installedPrograms.ReadAsync().ConfigureAwait(false));

    /// <summary>Uses the same registration snapshot for catalogue discovery and installation detection.</summary>
    public async Task<ExternalAppInventory> ReadInventoryAsync()
    {
        var registrations = await installedPrograms.ReadAsync().ConfigureAwait(false);
        var applications = MergeCatalogue(registrations);
        var registry = AppStatusDiscoveryService.BuildRegistrySnapshot(registrations);
        return new(applications, definitions => appStatusDiscoveryService.GetExternalAppsInstallationStatusAsync(definitions, registry))
        { DesktopRegistrations = registrations };
    }

    private static IReadOnlyList<ItemDefinition> MergeCatalogue(IReadOnlyList<InstalledProgram> registrations)
    {
        var catalogue = ExternalAppDefinitions.GetExternalApps().Items.ToList();
        if (registrations.Count == 0) return catalogue;
        var definitions = catalogue.Concat(WindowsAppDefinitions.GetWindowsApps().Items).ToArray();
        var knownPaths = definitions.Select(item => item.RegistryUninstallKeyPath).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = definitions.Where(item => item.RegistryUninstallKeyPath is null).ToArray();
        var knownNames = byName.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keyPatterns = byName.Select(item => item.RegistrySubKeyName).OfType<string>().Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.Ordinal).Select(AppStatusDiscoveryService.GetFingerprintMatcher).ToArray();
        var displayPatterns = byName.Select(item => item.RegistryDisplayName).OfType<string>().Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.Ordinal).Select(AppStatusDiscoveryService.GetFingerprintMatcher).ToArray();

        var discovered = registrations.Where(program =>
        {
            if (!program.IsVisible || knownPaths.Contains(program.KeyPath)) return false;
            var key = program.KeyName;
            return !knownNames.Contains(key) && !knownNames.Contains(program.Name) &&
                (key.Length == 0 || !keyPatterns.Any(pattern => pattern.IsMatch(key))) &&
                !displayPatterns.Any(pattern => pattern.IsMatch(program.Name));
        })
            .DistinctBy(program => program.KeyPath, StringComparer.OrdinalIgnoreCase)
            .Select(program => program.ToDefinition());
        return catalogue.Concat(discovered).ToList();
    }

    public async Task<OperationResult<bool>> UninstallAppAsync(ItemDefinition item, IProgress<TaskProgressDetail>? progress = null)
    {
        try
        {
            var cancellationToken = taskProgressService.GetCurrentCancellationToken();
            var result = await externalAppUninstallService.UninstallAsync(item, progress, cancellationToken).ConfigureAwait(false);

            if (result is { Success: true, Result: true, InfoMessage: null })
            {
                changeHistory.LogAppChange(item.Name, AppChangeKind.Removed);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            logService.LogInformation($"Uninstall of {item.Name} was cancelled by user");
            return OperationResult<bool>.Cancelled("Uninstall cancelled by user");
        }
        catch (Exception ex)
        {
            logService.LogError($"Failed to uninstall {item.Name}: {ex.Message}");
            return OperationResult<bool>.Failed(ex.Message);
        }
    }

    public async Task<Dictionary<string, bool>> CheckBatchInstalledAsync(IEnumerable<ItemDefinition> definitions)
    {
        return await appStatusDiscoveryService.GetExternalAppsInstallationStatusAsync(definitions).ConfigureAwait(false);
    }
}
