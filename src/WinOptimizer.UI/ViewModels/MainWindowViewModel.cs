using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.UI.Features.Settings.ViewModels;

namespace WinOptimizer.UI.ViewModels;

/// <summary>
/// ViewModel for the MainWindow, handling title bar commands and state.
/// Child ViewModels handle task progress and update checking.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    public SidebarPreferencesViewModel SidebarPreferences { get; }
    private readonly ILocalizationService _localizationService;
    private readonly IVersionService _versionService;
    private readonly ILogService _logService;
    private readonly IInteractiveUserService _interactiveUserService;
    private readonly IDialogService _dialogService;
    private readonly IApplicationModeService _applicationModeService;
    private readonly IDispatcherService _dispatcher;
    private readonly IConfigExportService _configExportService;
    private readonly IConfigApplicationExecutionService _configExecutionService;
    private bool _isResolvingTrialMode;

    /// <summary>Authors configuration choices without applying them to Windows.</summary>
    public bool IsTrialMode
    {
        get => _applicationModeService.CurrentMode == WinOptimizerMode.Builder;
        set
        {
            if (value == IsTrialMode) return;
            if (!CanChangeTrialMode) { OnPropertyChanged(); return; }
            if (value) _applicationModeService.EnterBuilderMode(BuilderTarget.Config);
            else ExitTestModeCommand.Execute(null);
            OnPropertyChanged();
        }
    }
    public bool CanChangeTrialMode => !_isResolvingTrialMode && !TaskProgress.IsTaskRunning && !_applicationModeService.IsSystemOperationRunning;
    public string TrialModeLabel => _localizationService.GetString("TrialMode_Label") ?? "Test mode";
    public string TrialModeDescription => _localizationService.GetString("TrialMode_Description")
        ?? "Edit a separate draft. On exit, export, apply or discard your changes.";
    public string TrialModeBlockedMessage => _localizationService.GetString("TrialMode_Blocked")
        ?? "This operation changes the system and is unavailable in test mode.";

    /// <summary>Resolves the draft before restoring normal interaction; only Apply can write settings.</summary>
    [RelayCommand]
    private async Task ExitTestModeAsync()
    {
        if (!IsTrialMode || !CanChangeTrialMode) return;
        _isResolvingTrialMode = true;
        OnPropertyChanged(nameof(CanChangeTrialMode));
        try
        {
            var action = await _dialogService.ShowTestModeExitDialogAsync();
            if (action == TestModeExitAction.KeepEditing) return;
            if (action == TestModeExitAction.Export && !await _configExportService.ExportBuilderConfigAsync()) return;
            UnifiedConfigurationFile? changes = action == TestModeExitAction.Apply
                ? await _configExportService.CreateConfigurationFromBuilderEditsAsync() : null;

            // Capture the independent draft before the mode owner discards its edits.
            _applicationModeService.EnterNormalMode();
            if (changes is not null)
            {
                var items = changes.Optimize.Features.Values.Concat(changes.Customize.Features.Values)
                    .SelectMany(section => section.Items).ToArray();
                if (items.Length == 0) return;
                var actions = items.Where(item => item.IsSelected == true).Select(item => item.Id).ToHashSet();
                await _configExecutionService.ExecuteConfigImportAsync(changes, new ImportOptions
                {
                    ApplyThemeWallpaper = actions.Contains(SettingIds.ThemeModeWindows),
                    ApplyCleanTaskbar = actions.Contains("taskbar-clean"),
                    ApplyCleanStartMenu = actions.Contains("start-menu-clean-10") || actions.Contains("start-menu-clean-11")
                });
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"Resolving test configuration: {ex.Message}", ex);
            await _dialogService.ShowErrorAsync(ex.Message, TrialModeLabel);
        }
        finally
        {
            _isResolvingTrialMode = false;
            OnPropertyChanged(nameof(IsTrialMode));
            OnPropertyChanged(nameof(CanChangeTrialMode));
        }
    }

    /// <summary>Child ViewModel for task progress display.</summary>
    public TaskProgressViewModel TaskProgress { get; }

    /// <summary>Child ViewModel for update checking.</summary>
    public UpdateCheckViewModel UpdateCheck { get; }

    [ObservableProperty]
    public partial string VersionInfo { get; set; }

    // OTS Elevation InfoBar properties
    [ObservableProperty]
    public partial bool IsOtsInfoBarOpen { get; set; }

    [ObservableProperty]
    public partial string OtsInfoBarTitle { get; set; }

    [ObservableProperty]
    public partial string OtsInfoBarMessage { get; set; }

    public MainWindowViewModel(
        IConfigurationService configurationService,
        ILocalizationService localizationService,
        IVersionService versionService,
        ILogService logService,
        IInteractiveUserService interactiveUserService,
        TaskProgressViewModel taskProgress,
        UpdateCheckViewModel updateCheck,
        IDialogService dialogService,
        IApplicationModeService applicationModeService,
        IDispatcherService dispatcher,
        IConfigExportService configExportService,
        IConfigApplicationExecutionService configExecutionService,
        SidebarPreferencesViewModel sidebarPreferences)
    {
        _localizationService = localizationService;
        _versionService = versionService;
        _logService = logService;
        _interactiveUserService = interactiveUserService;
        _dialogService = dialogService;
        _applicationModeService = applicationModeService;
        _dispatcher = dispatcher;
        _configExportService = configExportService;
        _configExecutionService = configExecutionService;
        SidebarPreferences = sidebarPreferences;

        TaskProgress = taskProgress;
        UpdateCheck = updateCheck;

        // Initialize partial property defaults
        VersionInfo = "win-optimizer";
        OtsInfoBarTitle = string.Empty;
        OtsInfoBarMessage = string.Empty;
        _applicationModeService.ModeChanged += OnApplicationModeChanged;
        _applicationModeService.SystemOperationChanged += OnSystemOperationChanged;
        TaskProgress.PropertyChanged += OnTaskProgressChanged;
    }

    /// <summary>
    /// Performs deferred initialization: subscribes to events and sets initial state.
    /// Must be called after construction, typically after the caller has subscribed
    /// to PropertyChanged so that initial state changes are observed.
    /// </summary>
    public void Initialize()
    {
        // Subscribe to language changes
        _localizationService.LanguageChanged += OnLanguageChanged;

        // Initialize version info
        InitializeVersionInfo();

        // Show OTS elevation InfoBar if needed
        InitializeOtsInfoBar();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        _applicationModeService.ModeChanged -= OnApplicationModeChanged;
        _applicationModeService.SystemOperationChanged -= OnSystemOperationChanged;
        TaskProgress.PropertyChanged -= OnTaskProgressChanged;
    }

    private void OnApplicationModeChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(IsTrialMode));
    private void OnSystemOperationChanged(object? sender, EventArgs e) =>
        _dispatcher.RunOnUIThread(() => OnPropertyChanged(nameof(CanChangeTrialMode)));

    private void OnTaskProgressChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskProgress.IsTaskRunning)) OnPropertyChanged(nameof(CanChangeTrialMode));
    }

    /// <summary>
    /// Handles language changes to update localized strings.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Notify all localized string properties
        OnPropertyChanged(nameof(NavigationBackTooltip));
        OnPropertyChanged(nameof(NavigationForwardTooltip));
        OnPropertyChanged(nameof(DonateTooltip));
        OnPropertyChanged(nameof(BugReportTooltip));
        OnPropertyChanged(nameof(DocsTooltip));
        OnPropertyChanged(nameof(TrialModeLabel));
        OnPropertyChanged(nameof(TrialModeDescription));
        OnPropertyChanged(nameof(TrialModeBlockedMessage));

        // Nav bar text
        OnPropertyChanged(nameof(NavSoftwareAppsText));
        OnPropertyChanged(nameof(NavOptimizeText));
        OnPropertyChanged(nameof(NavCleanerText));
        OnPropertyChanged(nameof(NavDriversText));
        OnPropertyChanged(nameof(NavFunctionalitiesText));
        OnPropertyChanged(nameof(NavManagementText));
        OnPropertyChanged(nameof(NavSecurityText));
        OnPropertyChanged(nameof(NavCustomizeText));
        OnPropertyChanged(nameof(NavAdvancedToolsText));
        OnPropertyChanged(nameof(NavSettingsText));

        // OTS InfoBar
        if (IsOtsInfoBarOpen)
        {
            RefreshOtsInfoBarText();
        }
    }

    /// <summary>
    /// Initializes the version info string from the version service.
    /// </summary>
    private void InitializeVersionInfo()
    {
        try
        {
            var versionInfo = _versionService.GetCurrentVersion();
            VersionInfo = $"win-optimizer {versionInfo.Version}";
        }
        catch (Exception ex)
        {
            _logService.LogDebug($"[MainWindowViewModel] Failed to get version info: {ex.Message}");
            VersionInfo = "win-optimizer";
        }
    }

    #region OTS Elevation InfoBar

    /// <summary>
    /// Initializes the OTS InfoBar if the app is running under OTS elevation.
    /// </summary>
    private void InitializeOtsInfoBar()
    {
        if (_interactiveUserService.IsOtsElevation)
        {
            RefreshOtsInfoBarText();
            IsOtsInfoBarOpen = true;
        }
    }

    /// <summary>
    /// Refreshes the OTS InfoBar text from localization.
    /// </summary>
    private void RefreshOtsInfoBarText()
    {
        OtsInfoBarTitle = _localizationService.GetString("InfoBar_OtsElevation_Title")
            ?? "Running as a different user";
        var messageTemplate = _localizationService.GetString("InfoBar_OtsElevation_Message")
            ?? "This app was elevated with a different account's credentials. Settings will still be applied to the logged-in user ({0}). This message is informational only.";
        OtsInfoBarMessage = string.Format(messageTemplate, _interactiveUserService.InteractiveUserName);
    }

    /// <summary>
    /// Dismisses the OTS InfoBar.
    /// </summary>
    public void DismissOtsInfoBar()
    {
        IsOtsInfoBarOpen = false;
    }

    #endregion

    #region Localized Strings

    // Tooltips
    public string NavigationBackTooltip => _localizationService.GetString("Navigation_Back");
    public string NavigationForwardTooltip => _localizationService.GetString("Navigation_Forward");

    public string DonateTooltip =>
        _localizationService.GetString("Menu_SupportWinOptimizer") ?? "Support win-optimizer";

    public string BugReportTooltip =>
        _localizationService.GetString("Tooltip_ReportBug") ?? "Report a Bug";

    public string DocsTooltip =>
        _localizationService.GetString("Tooltip_Documentation") ?? "Documentation";

    // Nav bar text
    public string NavSoftwareAppsText =>
        _localizationService.GetString("Nav_SoftwareAndApps") ?? "Software & Apps";

    public string NavOptimizeText =>
        _localizationService.GetString("Nav_Optimize") ?? "Optimize";

    public string NavCleanerText =>
        _localizationService.GetString("Nav_Cleaner") ?? "Cleaner";

    public string NavDriversText => _localizationService.GetString("Nav_Drivers") ?? "Drivers";
    public string NavFunctionalitiesText => _localizationService.GetString("Nav_Functionalities");
    public string NavManagementText => _localizationService.GetString("Nav_Management");
    public string NavSecurityText => _localizationService.GetString("Security_Title");

    public string NavCustomizeText =>
        _localizationService.GetString("Nav_Customize") ?? "Customize";

    public string NavAdvancedToolsText =>
        _localizationService.GetString("Nav_AdvancedTools") ?? "Advanced Tools";

    public string NavSettingsText =>
        _localizationService.GetString("Nav_Settings") ?? "Settings";

    #endregion

    #region Commands

    /// <summary>
    /// Command to open the in-app sponsors dialog.
    /// </summary>
    [RelayCommand]
    private async Task DonateAsync()
    {
        try
        {
            await _dialogService.ShowSponsorsDialogAsync(SponsorsDialogMode.Normal);
        }
        catch (Exception ex)
        {
            _logService.LogDebug($"Failed to open sponsors dialog: {ex.Message}");
        }
    }

    /// <summary>
    /// Command to open the bug report page.
    /// </summary>
    [RelayCommand]
    private async Task BugReportAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/sh1zen/win-optimizer/issues"));
        }
        catch (Exception ex)
        {
            _logService.LogDebug($"Failed to open bug report page: {ex.Message}");
        }
    }

    /// <summary>
    /// Command to open the documentation page.
    /// </summary>
    [RelayCommand]
    private async Task DocsAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/sh1zen/win-optimizer#readme"));
        }
        catch (Exception ex)
        {
            _logService.LogDebug($"Failed to open documentation page: {ex.Message}");
        }
    }

    #endregion

}
