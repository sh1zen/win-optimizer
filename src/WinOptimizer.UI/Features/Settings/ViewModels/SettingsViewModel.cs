using WinOptimizer.Application.Preferences;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Cleaner.ViewModels;
using WinOptimizer.UI.Features.Drivers.ViewModels;
using WinOptimizer.UI.Features.Common.ViewModels;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>
/// ViewModel for the Settings page.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly ILocalizationService _localizationService;
    private readonly IThemeService _themeService;
    private readonly IUserPreferencesService _preferencesService;
    private readonly ILogService _logService;
    public CleanerSettingsViewModel Cleaner { get; }
    public DriverSettingsViewModel Drivers { get; }
    public LogsAndHistoryViewModel Logs { get; }
    public ApplicationInfoViewModel Info { get; }
    public UpdatePreferencesViewModel UpdatePreferences { get; }
    public SidebarPreferencesViewModel SidebarPreferences { get; }
    public AppColorPreferencesViewModel AppColorPreferences { get; }
    private readonly PreferenceSaveQueue<string> _languageSaves;
    private Task? _initialization;
    public IReadOnlyList<SettingsCategoryItem> Categories { get; }
    public ObservableCollection<SettingsCategoryGroup> VisibleGroups { get; } = [];
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string Status { get; private set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanReset))] public partial bool IsResetting { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowCategories), nameof(ShowPlaceholder))]
    public partial bool IsWideLayout { get; set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(ShowCategories), nameof(ShowDetails), nameof(ShowPlaceholder),
        nameof(SelectedTitle), nameof(SelectedDescription), nameof(SelectedIcon), nameof(ShowAppearance), nameof(ShowCleaner),
        nameof(ShowDrivers), nameof(ShowRegistryAccounts), nameof(ShowCleanerAccounts), nameof(CleanerAccountChoices),
        nameof(CleanerAccountsDescription), nameof(ShowResetAll), nameof(ShowLogsAndHistory), nameof(ShowPreferenceDetails), nameof(ShowInfo), nameof(ShowReset),
        nameof(SelectedCleanerSection), nameof(ResetLabel), nameof(CanReset), nameof(ShowCategoryReset))]
    public partial SettingsCategoryItem? SelectedCategory { get; set; }
    public bool HasSelection => SelectedCategory is not null;
    public bool ShowCategories => IsWideLayout || !HasSelection;
    public bool ShowDetails => HasSelection;
    public bool ShowPlaceholder => IsWideLayout && !HasSelection;
    public bool NoMatches => VisibleGroups.Count == 0;
    public bool ShowReset => HasSelection && !ShowLogsAndHistory;
    /// <summary>Keeps category reset actions in the footer while the full reset uses its own card.</summary>
    public bool ShowCategoryReset => ShowReset && !ShowResetAll;
    public bool CanReset => ShowReset && !IsResetting;
    public string SelectedTitle => SelectedCategory?.Title ?? "";
    public string SelectedDescription => SelectedCategory?.Description ?? "";
    public Icon SelectedIcon => SelectedCategory?.Icon ?? Icon.Settings;
    public bool ShowAppearance => SelectedCategory?.Category == SettingsCategory.Appearance;
    public bool ShowDrivers => SelectedCategory?.Category == SettingsCategory.Drivers;
    public bool ShowLogsAndHistory => SelectedCategory?.Category == SettingsCategory.LogsAndHistory;
    public bool ShowPreferenceDetails => !ShowLogsAndHistory;
    public bool ShowInfo => SelectedCategory?.Category == SettingsCategory.Info;
    public bool ShowRegistryAccounts => SelectedCategory?.Category == SettingsCategory.Registry;
    public bool ShowCleanerAccounts => CleanerAccountChoices is not null;
    public ObservableCollection<CleanerChoice<string>>? CleanerAccountChoices => Cleaner.AccountChoices(SelectedCleanerSection);
    public string CleanerAccountsDescription => Cleaner.AccountsDescription(SelectedCleanerSection);
    public bool ShowResetAll => SelectedCategory?.Category == SettingsCategory.ResetAll;
    public bool ShowCleaner => SelectedCategory?.Category is SettingsCategory.Privacy or SettingsCategory.Junk or
        SettingsCategory.Registry or SettingsCategory.Shortcuts or SettingsCategory.Disk or SettingsCategory.Backups;
    public CleanerSettingsSection SelectedCleanerSection => SelectedCategory?.Category switch
    {
        SettingsCategory.Privacy => CleanerSettingsSection.Privacy,
        SettingsCategory.Junk => CleanerSettingsSection.Junk,
        SettingsCategory.Registry => CleanerSettingsSection.Registry,
        SettingsCategory.Shortcuts => CleanerSettingsSection.Shortcuts,
        SettingsCategory.Disk => CleanerSettingsSection.Disk,
        SettingsCategory.Backups => CleanerSettingsSection.Backups,
        _ => CleanerSettingsSection.All
    };
    public string SearchLabel => _localizationService.GetString("Settings_Search");
    public string NoMatchesLabel => _localizationService.GetString("Settings_NoMatches");
    public string SelectCategoryLabel => _localizationService.GetString("Settings_SelectCategory");
    public string FullResetDetails => _localizationService.GetString("Settings_ResetAllDetails");
    public string ResetLabel => _localizationService.GetString(ShowResetAll ? "Settings_ResetAll" : "Settings_ResetCategory");

    private ObservableCollection<ComboBoxDisplayOption> _languages = new();
    public ObservableCollection<ComboBoxDisplayOption> Languages
    {
        get => _languages;
        set => SetProperty(ref _languages, value);
    }

    private string _selectedLanguage = "en";
    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                OnSelectedLanguageChanged(value);
            }
        }
    }

    private ObservableCollection<ThemeOption> _themes = new();
    public ObservableCollection<ThemeOption> Themes
    {
        get => _themes;
        set => SetProperty(ref _themes, value);
    }

    private WinOptimizerTheme _selectedTheme;
    public WinOptimizerTheme SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                OnSelectedThemeChanged(value);
            }
        }
    }

    private ThemeOption? _selectedThemeOption;
    public ThemeOption? SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (SetProperty(ref _selectedThemeOption, value) && value != null)
            {
                SelectedTheme = value.Theme;
            }
        }
    }

    /// <summary>
    /// Creates a new instance of the SettingsViewModel.
    /// </summary>
    public SettingsViewModel(
        ILocalizationService localizationService,
        IThemeService themeService,
        IUserPreferencesService preferencesService,
        ILogService logService, CleanerSettingsViewModel cleaner, DriverSettingsViewModel drivers,
        LogsAndHistoryViewModel logs,
        ApplicationInfoViewModel info, UpdatePreferencesViewModel updatePreferences,
        SidebarPreferencesViewModel sidebarPreferences, AppColorPreferencesViewModel appColorPreferences)
    {
        _localizationService = localizationService;
        _themeService = themeService;
        _preferencesService = preferencesService;
        _logService = logService;
        Cleaner = cleaner;
        Drivers = drivers;
        Logs = logs;
        Info = info;
        UpdatePreferences = updatePreferences;
        SidebarPreferences = sidebarPreferences;
        AppColorPreferences = appColorPreferences;
        _languageSaves = new(language => _preferencesService.SetPreferenceAsync("Language", language), _ => { },
            error =>
            {
                Status = error ?? "";
                if (error is not null) _logService.LogError($"Settings preference save: {error}");
            }, "Unable to save language preferences.");

        // Initialize languages from the localization service
        InitializeLanguages();

        // Initialize themes
        InitializeThemes();

        // Load current selections
        _selectedLanguage = _localizationService.CurrentLanguage ?? "en";
        _selectedTheme = _themeService.CurrentTheme;
        _selectedThemeOption = Themes.FirstOrDefault(t => t.Theme == _selectedTheme);
        Categories = CreateCategories();
        RebuildGroups();

        // Subscribe to language changes to update theme display names
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        Logs.Dispose();
    }

    /// <summary>Loads preference owners once on entry, then indexes discovered setting names for search.</summary>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        await Task.WhenAll(Cleaner.LoadAsync(), Drivers.LoadAsync(), UpdatePreferences.LoadAsync(), SidebarPreferences.LoadAsync(), AppColorPreferences.LoadAsync());
        if (!_disposed) RebuildGroups();
    }

    private IReadOnlyList<SettingsCategoryItem> CreateCategories()
    {
        IEnumerable<string> Labels(params string[] keys) => keys.Select(_localizationService.GetString);
        return
        [
            new(SettingsCategory.Appearance, "Settings_Appearance", "Settings_AppearanceDescription", "Settings_GroupApplication", Icon.PaintBrushSparkle, _localizationService,
                () => Labels("Settings_Menu_Language", "Settings_Theme_Title", "Settings_ExpandNavigation", "Settings_ExpandNavigationDescription", "Settings_AppColor", "Settings_AppColorDescription")
                    .Concat(Languages.Select(item => item.DisplayText)).Concat(Themes.Select(item => item.DisplayText))
                    .Concat(AppColorPreferences.Colors.Select(item => item.DisplayText))),
            new(SettingsCategory.LogsAndHistory, "Settings_LogsAndHistory", "Settings_LogsAndHistoryDescription", "Settings_GroupApplication", Icon.DocumentText, _localizationService,
                () => Labels("Menu_WinOptimizerLogs", "Menu_ChangeHistory")),
            new(SettingsCategory.Info, "Settings_Info", "Settings_InfoDescription", "Settings_GroupApplication", Icon.Info, _localizationService,
                () => Labels("Dialog_Update_CurrentVersion", "Menu_CheckForUpdates", "Menu_SupportWinOptimizer", "Tooltip_Documentation", "Tooltip_ReportBug",
                    "Settings_AutomaticAppUpdates", "Settings_AutomaticScriptUpdates").Append(Info.VersionInfo)),
            new(SettingsCategory.Privacy, "Cleaner_Privacy", "Settings_PrivacyDescription", "Cleaner_Settings", Icon.ShieldLock, _localizationService,
                () => Labels("Cleaner_RecentFiles", "Cleaner_BrowserCache", "Cleaner_BrowserHistory", "Cleaner_RegistryAccounts").Concat(Cleaner.PrivacyAccounts.Select(item => item.Title))),
            new(SettingsCategory.Junk, "Cleaner_JunkFiles", "Settings_JunkDescription", "Cleaner_Settings", Icon.Delete, _localizationService,
                () => Labels("Cleaner_UserTemp", "Cleaner_WindowsTemp", "Cleaner_WindowsLogs", "Cleaner_ShaderCache", "Cleaner_ThumbnailCache", "Cleaner_ErrorReports", "Cleaner_CrashDumps", "Cleaner_InternetCache", "Cleaner_MinimumAge", "Cleaner_ApplicationCaches", "Cleaner_RegistryAccounts").Concat(Cleaner.Applications.Select(item => item.Title)).Concat(Cleaner.JunkAccounts.Select(item => item.Title))),
            new(SettingsCategory.Registry, "Cleaner_Registry", "Cleaner_RegistryDescription", "Cleaner_Settings", Icon.Database, _localizationService,
                () => Labels("Cleaner_DeepClean", "Cleaner_RegistryAccounts").Concat(Cleaner.RegistryCategories.Select(item => item.Title)).Concat(Cleaner.RegistryAccounts.Select(item => item.Title))),
            new(SettingsCategory.Shortcuts, "Cleaner_Shortcuts", "Settings_ShortcutsDescription", "Cleaner_Settings", Icon.Link, _localizationService,
                () => Labels("Cleaner_Desktop", "Cleaner_StartMenu", "Cleaner_RecentFiles", "Cleaner_RegistryAccounts").Concat(Cleaner.ShortcutAccounts.Select(item => item.Title))),
            new(SettingsCategory.Disk, "Cleaner_Disk", "Settings_DiskDescription", "Cleaner_Settings", Icon.HardDrive, _localizationService, () => Cleaner.Drives),
            new(SettingsCategory.Backups, "Cleaner_Backups", "Cleaner_BackupDescription", "Cleaner_Settings", Icon.History, _localizationService,
                () => Labels("Cleaner_BackupBeforeCleaning", "Cleaner_BackupBeforeRegistryDefrag", "Cleaner_RestoreBackup", "Cleaner_ReloadBackups")),
            new(SettingsCategory.Drivers, "Nav_Drivers", "Settings_DriversDescription", "Settings_GroupTools", Icon.PlugConnected, _localizationService,
                () => Labels("Drivers_WhqlLabel", "Drivers_WhqlDescription")),
            new(SettingsCategory.ResetAll, "Settings_ResetAllTitle", "Settings_ResetAllDescription", "Settings_GroupConfiguration", Icon.ArrowReset, _localizationService,
                () => Labels("Settings_ResetAll"))
        ];
    }

    partial void OnQueryChanged(string value) { SelectedCategory = null; RebuildGroups(); }

    private void RebuildGroups()
    {
        var selected = SelectedCategory;
        var matches = Categories.Where(category => category.Matches(Query.Trim())).ToArray();
        VisibleGroups.Clear();
        foreach (var group in matches.GroupBy(category => category.GroupKey))
            VisibleGroups.Add(new(_localizationService.GetString(group.Key), group.ToArray()));
        SelectedCategory = matches.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(NoMatches));
    }

    partial void OnSelectedCategoryChanged(SettingsCategoryItem? value)
    {
        Logs.CancelRead();
        if (value?.Category == SettingsCategory.LogsAndHistory) Logs.RefreshCommand.Execute(null);
    }

    /// <summary>Resets the opened category; the full-reset destination explicitly covers all settings on this page.</summary>
    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!CanReset || SelectedCategory is null) return;
        var category = SelectedCategory.Category;
        var cleanerSection = SelectedCleanerSection;
        IsResetting = true;
        Status = "";
        try
        {
            await InitializeAsync();
            bool reset;
            if (category == SettingsCategory.Appearance) reset = await ResetAppearanceAsync();
            else if (category == SettingsCategory.Drivers) reset = await Drivers.ResetAsync();
            else if (category == SettingsCategory.Info) reset = await UpdatePreferences.ResetAsync();
            else if (category == SettingsCategory.ResetAll)
            {
                var appearance = await ResetAppearanceAsync();
                var cleaner = await Cleaner.ResetAllAsync();
                var drivers = await Drivers.ResetAsync();
                var updates = await UpdatePreferences.ResetAsync();
                reset = appearance && cleaner && drivers && updates;
            }
            else reset = await Cleaner.ResetSectionAsync(cleanerSection);
            if (!reset && Status.Length == 0) Status = string.Join(Environment.NewLine,
                new[] { Cleaner.Status, Drivers.Status, UpdatePreferences.Status, SidebarPreferences.Status, AppColorPreferences.Status }.Where(message => message.Length > 0));
        }
        finally { IsResetting = false; }
    }

    private async Task<bool> ResetAppearanceAsync()
    {
        var languageChanged = SelectedLanguage != "en";
        SelectedLanguage = "en";
        if (!languageChanged)
        {
            if (_localizationService.CurrentLanguage != "en" && !_localizationService.SetLanguage("en"))
            { _languageSaves.Reject(_localizationService.GetString("Settings_LanguageFailed")); return false; }
            _languageSaves.Enqueue("en");
        }
        var themeAlreadyApplied = _themeService.CurrentTheme == WinOptimizerTheme.System;
        SelectedTheme = WinOptimizerTheme.System;
        SelectedThemeOption = Themes.First(theme => theme.Theme == WinOptimizerTheme.System);
        if (themeAlreadyApplied) _themeService.SetTheme(WinOptimizerTheme.System);
        var languageSaved = await _languageSaves.FlushAsync();
        var sidebarSaved = await SidebarPreferences.ResetAsync();
        var colorSaved = await AppColorPreferences.ResetAsync();
        return languageSaved && sidebarSaved && colorSaved;
    }

    /// <summary>
    /// Initializes the language options from the available locale files via the localization service.
    /// </summary>
    private void InitializeLanguages()
    {
        Languages.Clear();
        foreach (var opt in _localizationService.GetAvailableLanguages())
        {
            Languages.Add(new ComboBoxDisplayOption(opt.DisplayName, opt.Code));
        }
    }

    /// <summary>
    /// Initializes the theme options.
    /// </summary>
    private void InitializeThemes()
    {
        Themes.Clear();
        Themes.Add(new ThemeOption(WinOptimizerTheme.System, GetThemeDisplayName(WinOptimizerTheme.System)));
        Themes.Add(new ThemeOption(WinOptimizerTheme.LightNative, GetThemeDisplayName(WinOptimizerTheme.LightNative)));
        Themes.Add(new ThemeOption(WinOptimizerTheme.DarkNative, GetThemeDisplayName(WinOptimizerTheme.DarkNative)));
    }

    /// <summary>
    /// Gets the localized display name for a theme.
    /// </summary>
    private string GetThemeDisplayName(WinOptimizerTheme theme) => theme switch
    {
        WinOptimizerTheme.System => _localizationService.GetString("Theme_System") ?? "System",
        WinOptimizerTheme.LightNative => _localizationService.GetString("Theme_LightNative") ?? "Light",
        WinOptimizerTheme.DarkNative => _localizationService.GetString("Theme_DarkNative") ?? "Dark",
        _ => theme.ToString()
    };

    /// <summary>
    /// Called when the language changes to update all localized strings.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Logs.RefreshLabels();
        // Update theme display names
        foreach (var theme in Themes)
        {
            theme.DisplayText = GetThemeDisplayName(theme.Theme);
        }
        RebuildGroups();
        OnPropertyChanged(string.Empty);

    }

    // Localized string properties for x:Bind
    public string PageTitle => _localizationService.GetString("Settings_Title") ?? "Settings";
    public string PageDescription => _localizationService.GetString("Settings_Description");
    public string LanguageHeader => _localizationService.GetString("Settings_Menu_Language") ?? "Language";
    public string LanguageDescription => _localizationService.GetString("Settings_Language_Description") ?? "Select your preferred language";
    public string ThemeHeader => _localizationService.GetString("Settings_Theme_Title") ?? "Theme";
    public string ThemeDescription => _localizationService.GetString("Tooltip_ToggleTheme") ?? "Choose your preferred theme";

    /// <summary>
    /// Called when the selected theme changes.
    /// </summary>
    private void OnSelectedThemeChanged(WinOptimizerTheme value)
    {
        if (_themeService.CurrentTheme != value)
        {
            _themeService.SetTheme(value);
        }
    }

    /// <summary>
    /// Called when the selected language changes.
    /// </summary>
    private void OnSelectedLanguageChanged(string value)
    {
        if (string.IsNullOrEmpty(value) || value == _localizationService.CurrentLanguage)
            return;

        if (_localizationService.SetLanguage(value))
        {
            _languageSaves.Enqueue(value);
        }
        else _languageSaves.Reject(_localizationService.GetString("Settings_LanguageFailed"));
    }

}

/// <summary>
/// Represents a theme option for the ComboBox.
/// </summary>
public partial class ThemeOption : ObservableObject
{
    private string _displayText = string.Empty;
    public string DisplayText
    {
        get => _displayText;
        set => SetProperty(ref _displayText, value);
    }

    public WinOptimizerTheme Theme { get; }

    public ThemeOption(WinOptimizerTheme theme, string displayText)
    {
        Theme = theme;
        _displayText = displayText;
    }
}
