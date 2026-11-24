using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

public class WindowsAppsService(
    IWinGetBootstrapper winGetBootstrapper,
    IAppStatusDiscoveryService appStatusDiscoveryService,
    IAppxPackageSource appxPackageSource) : IWindowsAppsService
{
    public string DomainName => FeatureIds.WindowsApps;

    public event EventHandler? WinGetReady
    {
        add => winGetBootstrapper.WinGetInstalled += value;
        remove => winGetBootstrapper.WinGetInstalled -= value;
    }

    public void InvalidateStatusCache() => appStatusDiscoveryService.InvalidateCache();

    public Task<HashSet<string>> GetDevicePackageNamesAsync() => appxPackageSource.GetDevicePackageNamesAsync();

    public Task<IEnumerable<ItemDefinition>> GetAppsAsync()
    {
        var allItems = new List<ItemDefinition>();
        allItems.AddRange(WindowsAppDefinitions.GetWindowsApps().Items);
        allItems.AddRange(CapabilityDefinitions.GetWindowsCapabilities().Items);
        allItems.AddRange(OptionalFeatureDefinitions.GetWindowsOptionalFeatures().Items);
        return Task.FromResult<IEnumerable<ItemDefinition>>(allItems);
    }

    public async Task<Dictionary<string, bool>> CheckBatchInstalledAsync(IEnumerable<ItemDefinition> definitions)
    {
        return await appStatusDiscoveryService.GetInstallationStatusBatchAsync(definitions).ConfigureAwait(false);
    }
}
