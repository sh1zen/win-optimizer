using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Application.Preferences;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>Shares the live sidebar layout between the shell and Settings and saves each choice in order.</summary>
public partial class SidebarPreferencesViewModel : ObservableObject, IDisposable
{
    private readonly IUserPreferencesService _preferences;
    private readonly ILocalizationService _localization;
    private readonly PreferenceSaveQueue<bool> _saves;
    private Task<bool>? _loadTask;
    private bool _applyingPreference;
    public bool IsLoaded { get; private set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";
    public bool CanEdit => IsLoaded && !IsBusy;
    public string ExpandedLabel => _localization.GetString("Settings_ExpandNavigation");
    public string ExpandedDescription => _localization.GetString("Settings_ExpandNavigationDescription");

    public SidebarPreferencesViewModel(IUserPreferencesService preferences, ILocalizationService localization)
    {
        _preferences = preferences;
        _localization = localization;
        _saves = new(expanded => _preferences.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, expanded), _ => { },
            error => Status = error ?? "", "Unable to save navigation preferences.");
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Restores the saved layout explicitly, sharing concurrent loads and allowing failed reads to be retried.</summary>
    public Task<bool> LoadAsync() => IsLoaded ? Task.FromResult(true) :
        _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync();

    private async Task<bool> LoadCoreAsync()
    {
        IsBusy = true;
        try
        {
            var expanded = await _preferences.GetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, true);
            ApplyPreference(expanded);
            IsLoaded = true;
            Status = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status = ex.Message; return false; }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanEdit)); }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (IsLoaded && !_applyingPreference) _saves.Enqueue(value);
    }

    /// <summary>Applies a load or reset without producing an intermediate write.</summary>
    private void ApplyPreference(bool expanded)
    {
        _applyingPreference = true;
        try { IsExpanded = expanded; }
        finally { _applyingPreference = false; }
    }

    /// <summary>Drains pending layout writes before shutdown.</summary>
    public Task<bool> FlushAsync() => IsLoaded ? _saves.FlushAsync() : Task.FromResult(false);

    /// <summary>Restores the expanded layout through the normal persistence queue.</summary>
    public async Task<bool> ResetAsync()
    {
        if (!await LoadAsync()) return false;
        ApplyPreference(true);
        _saves.Enqueue(true);
        return await _saves.FlushAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}
