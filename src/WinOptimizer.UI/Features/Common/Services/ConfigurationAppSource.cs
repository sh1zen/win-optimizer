using WinOptimizer.Application.Configuration;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.Services;

namespace WinOptimizer.UI.Features.Common.Services;

/// <summary>Maps shared app view models to configuration records after their explicit initialization.</summary>
public sealed class ConfigurationAppSource(
    IWindowsAppsItemsProvider windowsApps,
    IExternalAppsItemsProvider externalApps) : IConfigurationAppSource
{
    public async Task<ConfigSection> GetWindowsAppsAsync(bool useInstalledStatus)
    {
        var section = new ConfigSection();
        if (!windowsApps.IsInitialized)
            await windowsApps.LoadItemsAsync();

        section.IsIncluded = true;
        section.Items = windowsApps.Items
            .Where(item => useInstalledStatus ? item.IsInstalled : item.IsSelected)
            .Select(WindowsAppConfigurationMapper.CreateItem).ToList();

        return section;
    }

    public async Task<ConfigSection> GetExternalAppsAsync()
    {
        var section = new ConfigSection();
        if (!externalApps.IsInitialized)
            await externalApps.LoadItemsAsync();

        section.IsIncluded = true;
        section.Items = externalApps.Items
            .Where(item => item.IsSelected)
            .Select(item =>
            {
                var configItem = new ConfigurationItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    IsSelected = true,
                    InputType = InputType.Toggle
                };

                if (item.Definition.WinGetPackageId != null && item.Definition.WinGetPackageId.Any())
                    configItem.WinGetPackageId = item.Definition.WinGetPackageId[0];

                return configItem;
            }).ToList();

        return section;
    }
}
