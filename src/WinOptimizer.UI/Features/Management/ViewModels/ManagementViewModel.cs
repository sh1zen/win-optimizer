using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Management.ViewModels;

/// <summary>Provides localized management overview labels without loading tool data.</summary>
public sealed class ManagementViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly IDispatcherService _dispatcher;

    public string Heading => _localization.GetString("Nav_Management");
    public string Description => _localization.GetString("Management_Description");
    public string EnvironmentDisplayName => _localization.GetString("Utilities_Environment");
    public string EnvironmentDescription => _localization.GetString("Utilities_EnvironmentDescription");
    public string HostsDisplayName => _localization.GetString("Utilities_Hosts");
    public string HostsDescription => _localization.GetString("Utilities_HostsDescription");
    public string BatteryDisplayName => _localization.GetString("Utilities_Battery");
    public string BatteryDescription => _localization.GetString("Utilities_BatteryDescription");
    public string ConnectionDisplayName => _localization.GetString("Utilities_Connection");
    public string ConnectionDescription => _localization.GetString("Utilities_ConnectionDescription");
    public string WindowsStatusDisplayName => _localization.GetString("WindowsStatus_Title");
    public string WindowsStatusDescription => _localization.GetString("WindowsStatus_TitleDescription");
    public string AutorunsDisplayName => _localization.GetString("Autoruns_Title");
    public string AutorunsDescription => _localization.GetString("Autoruns_TitleDescription");
    public string ProcessMonitorDisplayName => _localization.GetString("ProcessMonitor_Title");
    public string ProcessMonitorDescription => _localization.GetString("ProcessMonitor_TitleDescription");

    public ManagementViewModel(ILocalizationService localization, IDispatcherService dispatcher)
    {
        _localization = localization;
        _dispatcher = dispatcher;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Refreshes the overview labels when the application's language changes.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) =>
        _dispatcher.RunOnUIThread(() => OnPropertyChanged(string.Empty));

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}
