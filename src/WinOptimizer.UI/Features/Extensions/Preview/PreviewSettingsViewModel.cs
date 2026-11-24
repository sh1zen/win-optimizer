using System.ComponentModel;
using System.Security;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Preview;

/// <summary>Shares confirmed Preview settings and serializes requests from the overview and detail pages.</summary>
public sealed partial class PreviewSettingsViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly Func<Task<PreviewSettingsSnapshot>> _load;
    private readonly Func<bool, string, Task<PreviewSettingsSnapshot>> _setShortcut;
    private readonly Func<bool, Task<PreviewSettingsSnapshot>> _setStartup;
    private readonly Func<int, Task<PreviewSettingsSnapshot>> _setOpacity;

    public PreviewSettingsSnapshot? Settings { get; private set; }
    public bool ShortcutEnabled => Settings?.ShortcutEnabled ?? false;
    public int BackgroundOpacity => Settings?.BackgroundOpacity ?? PreviewExtensionSettings.DefaultOpacity;
    public bool CanEdit => Settings is not null && !IsBusy;
    public bool HasError => Error.Length != 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = "";

    public PreviewSettingsViewModel(ILocalizationService localization) : this(localization,
        PreviewExtensionSettings.LoadAsync, PreviewExtensionSettings.SetShortcutAsync, PreviewExtensionSettings.SetStartupAsync,
        PreviewExtensionSettings.SetBackgroundOpacityAsync) { }

    internal PreviewSettingsViewModel(ILocalizationService localization, Func<Task<PreviewSettingsSnapshot>> load,
        Func<bool, string, Task<PreviewSettingsSnapshot>> setShortcut, Func<bool, Task<PreviewSettingsSnapshot>> setStartup,
        Func<int, Task<PreviewSettingsSnapshot>> setOpacity)
    {
        _localization = localization;
        _load = load;
        _setShortcut = setShortcut;
        _setStartup = setStartup;
        _setOpacity = setOpacity;
    }

    public Task LoadAsync() => UpdateAsync(_load);

    public Task SetShortcutAsync(bool enabled) => UpdateAsync(() => _setShortcut(enabled, _localization.CurrentLanguage));

    public Task SetStartupAsync(bool enabled) => UpdateAsync(() => _setStartup(enabled));

    public Task SetBackgroundOpacityAsync(int opacity) => UpdateAsync(() => _setOpacity(opacity));

    /// <summary>Publishes the confirmed settings after each request, restoring controls on failure.</summary>
    private async Task UpdateAsync(Func<Task<PreviewSettingsSnapshot>> update)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = "";
        try { Settings = await update(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception or ArgumentException or TimeoutException
            or InvalidOperationException or UnauthorizedAccessException or JsonException or SecurityException)
        { Error = ex.Message; }
        finally
        {
            OnPropertyChanged(nameof(Settings));
            OnPropertyChanged(nameof(ShortcutEnabled));
            IsBusy = false;
        }
    }
}
