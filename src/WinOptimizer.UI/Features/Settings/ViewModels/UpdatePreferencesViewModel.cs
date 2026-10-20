using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Application.Preferences;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>Loads and saves automatic update preferences without starting update checks.</summary>
public partial class UpdatePreferencesViewModel : ObservableObject, IDisposable
{
    private readonly IUserPreferencesService _preferences;
    private readonly ILocalizationService _localization;
    private readonly PreferenceSaveQueue<UpdateOptions> _saves;
    private Task<bool>? _loadTask;
    private bool _applyingOptions;
    public UpdateOptions Options { get; private set; } = new();
    public bool IsLoaded { get; private set; }
    [ObservableProperty] public partial bool CheckApplicationUpdatesAutomatically { get; set; }
    [ObservableProperty] public partial bool UpdateRemovalScriptsAutomatically { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";
    public bool CanEdit => IsLoaded && !IsBusy;
    public string ApplicationUpdatesLabel => _localization.GetString("Settings_AutomaticAppUpdates");
    public string ScriptUpdatesLabel => _localization.GetString("Settings_AutomaticScriptUpdates");
    public string ScheduleDescription => _localization.GetString("Settings_UpdateScheduleDescription");

    public UpdatePreferencesViewModel(IUserPreferencesService preferences, ILocalizationService localization)
    {
        _preferences = preferences;
        _localization = localization;
        _saves = new(options => _preferences.SetPreferenceAsync(UserPreferenceKeys.Updates, options), options =>
        {
            Options = options;
            OnPropertyChanged(nameof(Options));
        }, error => Status = error ?? "", "Unable to save update preferences.");
        ApplyOptions(Options);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Coalesces explicit loads and permits retry after a failed read.</summary>
    public Task<bool> LoadAsync() => IsLoaded ? Task.FromResult(true) :
        _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync();

    private async Task<bool> LoadCoreAsync()
    {
        IsBusy = true;
        try
        {
            var options = await _preferences.GetPreferenceAsync(UserPreferenceKeys.Updates, new UpdateOptions());
            ApplyOptions(options);
            Options = options;
            IsLoaded = true;
            Status = "";
            OnPropertyChanged(nameof(Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status = ex.Message; return false; }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanEdit)); }
    }

    partial void OnCheckApplicationUpdatesAutomaticallyChanged(bool value) => SaveOptions();
    partial void OnUpdateRemovalScriptsAutomaticallyChanged(bool value) => SaveOptions();

    /// <summary>Captures both controls together so queued edits preserve their order.</summary>
    private void SaveOptions()
    {
        if (IsLoaded && !_applyingOptions)
            _saves.Enqueue(new()
            {
                CheckApplicationUpdatesAutomatically = CheckApplicationUpdatesAutomatically,
                UpdateRemovalScriptsAutomatically = UpdateRemovalScriptsAutomatically
            });
    }

    /// <summary>Reflects a loaded or reset snapshot without saving intermediate toggle values.</summary>
    private void ApplyOptions(UpdateOptions options)
    {
        _applyingOptions = true;
        try
        {
            CheckApplicationUpdatesAutomatically = options.CheckApplicationUpdatesAutomatically;
            UpdateRemovalScriptsAutomatically = options.UpdateRemovalScriptsAutomatically;
        }
        finally { _applyingOptions = false; }
    }

    /// <summary>Drains pending preference writes before application shutdown.</summary>
    public Task<bool> FlushAsync() => IsLoaded ? _saves.FlushAsync() : Task.FromResult(false);

    /// <summary>Restores both automatic update choices through the normal save queue.</summary>
    public async Task<bool> ResetAsync()
    {
        if (!await LoadAsync()) return false;
        ApplyOptions(new());
        _saves.Enqueue(new());
        return await _saves.FlushAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}
