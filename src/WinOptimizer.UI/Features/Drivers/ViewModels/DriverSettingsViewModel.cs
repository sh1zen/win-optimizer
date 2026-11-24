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
    private DriverScanOptions _draft = new();
    public DriverScanOptions Options { get; private set; } = new();
    public bool IsLoaded { get; private set; }
    [ObservableProperty] public partial bool OnlyWhqlUpdates { get; set; } = true;
    [ObservableProperty] public partial bool GameReady { get; set; } = true;
    [ObservableProperty] public partial bool Studio { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";
    public bool CanEdit => IsLoaded && !IsBusy;
    public string WhqlLabel => _localization.GetString("Drivers_WhqlLabel");
    public string Description => _localization.GetString("Drivers_WhqlDescription");
    public string SourcesLabel => _localization.GetString("Drivers_Sources");
    public string ChannelsLabel => _localization.GetString("Drivers_Channels");
    public IReadOnlyList<DriverSourcePreference> SourceChoices { get; }

    public DriverSettingsViewModel(IUserPreferencesService preferences, ILocalizationService localization)
    {
        _preferences = preferences;
        _localization = localization;
        SourceChoices = Enum.GetValues<DriverSources>().Where(source => source != DriverSources.All)
            .Select(source => new DriverSourcePreference(source, ApplySource)).ToArray();
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
            Display(Options);
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
        Edit(_draft with { OnlyWhqlUpdates = value });
    }

    partial void OnGameReadyChanged(bool value) => ApplyChannel(NvidiaChannels.GameReady, value);
    partial void OnStudioChanged(bool value) => ApplyChannel(NvidiaChannels.Studio, value);
    private void ApplyChannel(NvidiaChannels channel, bool enabled) =>
        Edit(_draft with { NvidiaChannels = enabled ? _draft.NvidiaChannels | channel : _draft.NvidiaChannels & ~channel });
    private void ApplySource(DriverSources source, bool enabled) =>
        Edit(_draft with { Sources = enabled ? _draft.Sources | source : _draft.Sources & ~source });

    private void Edit(DriverScanOptions value)
    {
        if (!IsLoaded || _applyingOptions) return;
        _draft = value;
        _saves.Enqueue(value);
    }

    private void Display(DriverScanOptions options)
    {
        _draft = options;
        _applyingOptions = true;
        try
        {
            OnlyWhqlUpdates = options.OnlyWhqlUpdates;
            GameReady = options.NvidiaChannels.HasFlag(NvidiaChannels.GameReady);
            Studio = options.NvidiaChannels.HasFlag(NvidiaChannels.Studio);
            foreach (var item in SourceChoices) item.Enabled = options.Sources.HasFlag(item.Source);
        }
        finally { _applyingOptions = false; }
    }

    public Task<bool> FlushAsync() => IsLoaded ? _saves.FlushAsync() : Task.FromResult(false);

    /// <summary>Restores the driver policy and persists it through the same ordered write queue.</summary>
    public async Task<bool> ResetAsync()
    {
        if (!await LoadAsync()) return false;
        var options = new DriverScanOptions();
        Display(options);
        _saves.Enqueue(options);
        return await _saves.FlushAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}

/// <summary>A source preference updates the same immutable options snapshot as the other settings.</summary>
public sealed class DriverSourcePreference(DriverSources source, Action<DriverSources, bool> edit) : ObservableObject
{
    private bool _enabled = true;
    public DriverSources Source => source;
    public string Name => DriverSourceNames.Get(source);
    public bool Enabled
    {
        get => _enabled;
        set { if (SetProperty(ref _enabled, value)) edit(source, value); }
    }
}

internal static class DriverSourceNames
{
    internal static string Get(DriverSources source) => source switch
    {
        DriverSources.WindowsUpdate => "Windows Update", DriverSources.MicrosoftCatalog => "Microsoft Update Catalog",
        DriverSources.Nvidia => "NVIDIA", DriverSources.Amd => "AMD", DriverSources.Intel => "Intel",
        DriverSources.Realtek => "Realtek", DriverSources.Asus => "ASUS", DriverSources.Msi => "MSI",
        DriverSources.Gigabyte => "Gigabyte", DriverSources.AsRock => "ASRock", DriverSources.Dell => "Dell",
        DriverSources.Hp => "HP", DriverSources.Lenovo => "Lenovo", _ => source.ToString()
    };
}
