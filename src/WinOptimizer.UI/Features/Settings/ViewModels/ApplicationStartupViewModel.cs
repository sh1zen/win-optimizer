using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>Presents and changes the confirmed sign-in task for the current account.</summary>
public sealed partial class ApplicationStartupViewModel(IAutorunService autoruns, ILocalizationService localization,
    IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
    : UtilityViewModel(localization, mode, dispatcher, tasks, "Settings_Title")
{
    protected override string TitleKey => "Autoruns_ApplicationStartup";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(OffLabel), nameof(CanChange))]
    public partial bool? IsEnabled { get; private set; }

    public bool IsOn => IsEnabled == true;
    public string OnLabel => Text("Common_On");
    public string OffLabel => Text(IsEnabled.HasValue ? "Common_Off" : "Autoruns_Unknown");
    public bool CanChange => CanWrite && IsEnabled.HasValue;

    /// <summary>Refreshes the confirmed sign-in task state when General opens.</summary>
    public Task RefreshAsync() => ExecuteAsync(ReadStateAsync);

    private async Task ReadStateAsync()
    {
        IsEnabled = null;
        IsEnabled = await autoruns.ReadApplicationStartupAsync();
    }

    /// <summary>Applies the requested change and reads the scheduler state back.</summary>
    public Task SetEnabledAsync(bool enabled) => !CanChange || enabled == IsOn ? Task.CompletedTask : ExecuteAsync(async () =>
    {
        await autoruns.SetApplicationStartupAsync(enabled);
        await ReadStateAsync();
    }, write: true);

    protected override void NotifyActions() => OnPropertyChanged(nameof(CanChange));
}
