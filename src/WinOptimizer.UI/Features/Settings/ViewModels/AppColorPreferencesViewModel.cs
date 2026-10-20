using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Application.Preferences;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Resources;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>Owns the live application accent, explicit restoration and ordered preference writes.</summary>
public partial class AppColorPreferencesViewModel : ObservableObject, IDisposable
{
    private readonly IUserPreferencesService _preferences;
    private readonly ILocalizationService _localization;
    private readonly AppColorResources _resources;
    private readonly PreferenceSaveQueue<string> _saves;
    private Task<bool>? _loadTask;
    private bool _applyingPreference;
    public bool IsLoaded { get; private set; }
    public bool CanEdit => IsLoaded && !IsBusy;
    public string Label => _localization.GetString("Settings_AppColor");
    public string Description => _localization.GetString("Settings_AppColorDescription");
    public IReadOnlyList<AppColorOption> Colors { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SelectedOption))]
    public partial AppColor SelectedColor { get; set; } = AppColor.Blue;
    public AppColorOption? SelectedOption
    {
        get => Colors.First(option => option.Value == SelectedColor);
        set { if (value is not null) SelectedColor = value.Value; }
    }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";

    public AppColorPreferencesViewModel(IUserPreferencesService preferences, ILocalizationService localization,
        AppColorResources resources)
    {
        _preferences = preferences;
        _localization = localization;
        _resources = resources;
        Colors = Enum.GetValues<AppColor>().Select(color => new AppColorOption(color, localization)).ToArray();
        _saves = new(color => _preferences.SetPreferenceAsync(UserPreferenceKeys.AppColor, color), _ => { },
            error => Status = error ?? "", "Unable to save app color.");
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Shares concurrent startup/settings reads; failed reads can be retried when Settings reopens.</summary>
    public Task<bool> LoadAsync() => IsLoaded ? Task.FromResult(true) :
        _loadTask is { IsCompleted: false } ? _loadTask : _loadTask = LoadCoreAsync();

    private async Task<bool> LoadCoreAsync()
    {
        IsBusy = true;
        try
        {
            var saved = await _preferences.GetPreferenceAsync(UserPreferenceKeys.AppColor, nameof(AppColor.Blue));
            if (!Enum.TryParse<AppColor>(saved, out var color) || !Enum.IsDefined(color))
                throw new InvalidOperationException(_localization.GetString("Settings_AppColorInvalid"));
            ApplyPreference(color);
            IsLoaded = true;
            Status = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status = ex.Message; return false; }
        finally { IsBusy = false; OnPropertyChanged(nameof(CanEdit)); }
    }

    partial void OnSelectedColorChanging(AppColor value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }

    partial void OnSelectedColorChanged(AppColor value)
    {
        _resources.Apply(value);
        if (IsLoaded && !_applyingPreference) _saves.Enqueue(value.ToString());
    }

    /// <summary>Applies restoration even when the saved color equals the initial selection, without writing it.</summary>
    private void ApplyPreference(AppColor color)
    {
        _applyingPreference = true;
        try
        {
            if (SelectedColor == color) _resources.Apply(color);
            else SelectedColor = color;
        }
        finally { _applyingPreference = false; }
    }

    /// <summary>Drains pending accent writes before shutdown.</summary>
    public Task<bool> FlushAsync() => IsLoaded ? _saves.FlushAsync() : Task.FromResult(false);

    /// <summary>Restores and persists the default blue accent, including recovery from an invalid saved choice.</summary>
    public async Task<bool> ResetAsync()
    {
        if (_loadTask is { IsCompleted: false }) await _loadTask;
        ApplyPreference(AppColor.Blue);
        _saves.Enqueue(nameof(AppColor.Blue));
        var saved = await _saves.FlushAsync();
        if (saved) { IsLoaded = true; OnPropertyChanged(nameof(CanEdit)); }
        return saved;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var option in Colors) option.RefreshLabel();
        OnPropertyChanged(string.Empty);
    }

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;
}

/// <summary>A localized accent choice with a color swatch for the appearance selector.</summary>
public sealed class AppColorOption(AppColor value, ILocalizationService localization) : ObservableObject
{
    public AppColor Value { get; } = value;
    public string DisplayText => localization.GetString($"Settings_AppColor_{Value}");
    public SolidColorBrush Swatch { get; } = new(AppColorResources.ColorsFor(value).Light);
    public void RefreshLabel() => OnPropertyChanged(nameof(DisplayText));
}
