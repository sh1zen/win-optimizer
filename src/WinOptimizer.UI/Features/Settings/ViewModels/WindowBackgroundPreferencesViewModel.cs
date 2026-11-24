using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Application.Preferences;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>Shares the main window's background choice with Settings and writes preferences in edit order.</summary>
public partial class WindowBackgroundPreferencesViewModel : ObservableObject, IDisposable
{
    private const bool DefaultBlurEnabled = false;
    private readonly IUserPreferencesService _preferences;
    private readonly ILocalizationService _localization;
    private readonly PreferenceSaveQueue<bool> _saves;
    private Task<bool>? _loadTask;

    [ObservableProperty] public partial bool IsBlurEnabled { get; set; } = DefaultBlurEnabled;
    [ObservableProperty] public partial bool CanEdit { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";
    public string Label => _localization.GetString("Settings_BlurWindowBackground");
    public string Description => _localization.GetString("Settings_BlurWindowBackgroundDescription");

    public WindowBackgroundPreferencesViewModel(IUserPreferencesService preferences, ILocalizationService localization)
    {
        _preferences = preferences;
        _localization = localization;
        _saves = new(enabled => _preferences.SetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, enabled),
            _ => { }, error => Status = error ?? "", "Unable to save window background preferences.");
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Restores the saved choice without writing it back during startup or Settings initialization.</summary>
    internal Task<bool> LoadAsync(UserPreferencesSnapshot? snapshot = null) => CanEdit ? Task.FromResult(true) :
        _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync(snapshot);

    private async Task<bool> LoadCoreAsync(UserPreferencesSnapshot? snapshot)
    {
        try
        {
            IsBlurEnabled = snapshot?.Get(UserPreferenceKeys.BlurWindowBackground, DefaultBlurEnabled) ??
                await _preferences.GetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, DefaultBlurEnabled);
            CanEdit = true;
            Status = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = ex.Message;
            return false;
        }
    }

    partial void OnIsBlurEnabledChanged(bool value)
    {
        if (CanEdit) _saves.Enqueue(value);
    }

    /// <summary>Waits for pending writes before shutdown.</summary>
    internal Task<bool> FlushAsync() => _saves.FlushAsync();

    /// <summary>Restores and saves the default background even if it is already selected.</summary>
    internal async Task<bool> ResetAsync()
    {
        if (!await LoadAsync()) return false;
        if (IsBlurEnabled == DefaultBlurEnabled) _saves.Enqueue(DefaultBlurEnabled);
        else IsBlurEnabled = DefaultBlurEnabled;
        return await FlushAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Description));
    }

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}
