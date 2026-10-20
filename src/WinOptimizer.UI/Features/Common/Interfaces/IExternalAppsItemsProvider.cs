using System.Collections.ObjectModel;
using System.Threading.Tasks;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.Common.Interfaces;

/// <summary>
/// Provider for External Apps items and selected uninstallation.
/// Decouples config services from the concrete ViewModel type.
/// </summary>
public interface IExternalAppsItemsProvider
{
    bool IsInitialized { get; }
    Task LoadItemsAsync();
    ObservableCollection<AppItemViewModel> Items { get; }
    Task UninstallAppsAsync();
}
