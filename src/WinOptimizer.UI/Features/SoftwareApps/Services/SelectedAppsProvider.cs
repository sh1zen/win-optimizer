using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.SoftwareApps.Services;

/// <summary>
/// Bridges the WIM feature to the SoftwareApps feature by providing
/// selected Windows apps without exposing the concrete ViewModel.
/// </summary>
public class SelectedAppsProvider : ISelectedAppsProvider
{
    private readonly WindowsAppsViewModel _windowsAppsViewModel;

    public SelectedAppsProvider(WindowsAppsViewModel windowsAppsViewModel)
    {
        _windowsAppsViewModel = windowsAppsViewModel;
    }

    public async Task<IReadOnlyList<ConfigurationItem>> GetSelectedWindowsAppsAsync()
    {
        if (!_windowsAppsViewModel.IsInitialized)
            await _windowsAppsViewModel.LoadItemsAsync();

        return _windowsAppsViewModel.Items
            .Where(item => item.IsSelected)
            .Select(WindowsAppConfigurationMapper.CreateItem).ToList();
    }
}
