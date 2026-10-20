using WinOptimizer.Application.Preferences;
using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.UI.Features.Common.Services;

namespace WinOptimizer.UI.Features.Drivers.ViewModels;

/// <summary>Owns the saved driver scan policy; editing a toggle does not change an in-flight scan.</summary>
public partial class DriverSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IUserPreferencesService _preferences;
    private readonly ILocalizationService _localization;
    private Task<bool>? _loadTask;
    private bool _applyingOptions;
    private readonly PreferenceSaveQueue<DriverScanOptions> _saves;
    public DriverScanOptions Options { get; private set; } = new();
    public bool IsLoaded { get; private set; }
    [ObservableProperty] public partial bool OnlyWhqlUpdates { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";
    public bool CanEdit => IsLoaded && !IsBusy;
    public string WhqlLabel => _localization.GetString("Drivers_WhqlLabel");
    public string Description => _localization.GetString("Drivers_WhqlDescription");

    public DriverSettingsViewModel(IUserPreferencesService preferences, ILocalizationService localization)
    {
        _preferences = preferences;
        _localization = localization;
        _saves = new(options => _preferences.SetPreferenceAsync("Drivers", options), options =>
        {
            Options = options;
            OnPropertyChanged(nameof(Options));
        }, error => Status = error ?? "", "Unable to save driver preferences.");
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Coalesces explicit initialization from the Settings and Drivers pages.</summary>
    public Task<bool> LoadAsync() => IsLoaded ? Task.FromResult(true) :
        _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync();

    private async Task<bool> LoadCoreAsync()
    {
        IsBusy = true;
        try
        {
            Options = await _preferences.GetPreferenceAsync("Drivers", new DriverScanOptions());
            _applyingOptions = true;
            OnlyWhqlUpdates = Options.OnlyWhqlUpdates;
            _applyingOptions = false;
            IsLoaded = true;
            Status = "";
            OnPropertyChanged(nameof(Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status = ex.Message; return false; }
        finally { _applyingOptions = false; IsBusy = false; OnPropertyChanged(nameof(CanEdit)); }
    }

    partial void OnOnlyWhqlUpdatesChanged(bool value)
    {
        if (IsLoaded && !_applyingOptions) _saves.Enqueue(new() { OnlyWhqlUpdates = value });
    }

    public Task<bool> FlushAsync() => IsLoaded ? _saves.FlushAsync() : Task.FromResult(false);

    /// <summary>Restores the driver policy and persists it through the same ordered write queue.</summary>
    public async Task<bool> ResetAsync()
    {
        if (!await LoadAsync()) return false;
        _applyingOptions = true;
        try { OnlyWhqlUpdates = new DriverScanOptions().OnlyWhqlUpdates; }
        finally { _applyingOptions = false; }
        _saves.Enqueue(new());
        return await _saves.FlushAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}
