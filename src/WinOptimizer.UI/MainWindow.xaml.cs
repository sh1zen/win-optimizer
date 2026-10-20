using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Utilities;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.ViewModels;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Foundation;

namespace WinOptimizer.UI;

/// <summary>
/// Main application window with custom NavSidebar navigation.
/// Delegates task progress, navigation, startup, and title bar management to helper classes.
/// </summary>
public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private MainWindowViewModel? _viewModel;
    private WindowSizeManager? _windowSizeManager;
    private UiZoomManager? _uiZoomManager;
    private IConfigReviewService? _configReviewService;
    private INavBadgeService? _navBadgeService;
    private ILogService? _logService;
    private bool _isStartupLoading = true;
    private bool _startupOperationsStarted;
    private bool _openExtensionSettingsAfterStartup;
    private ExtensionApplicationLifetime? _extensionLifetime;
    private readonly StartupLoadingView _loadingView;
    private bool _shellInitialized;
    internal bool IsClosed { get; private set; }
    internal bool IsManualActivationRequested { get; private set; }

    // Helper classes (Phases 2-5)
    private TaskProgressCoordinator? _taskProgressCoordinator;
    private NavigationRouter? _navigationRouter;
    private StartupUiCoordinator? _startupUiCoordinator;
    private TitleBarManager? _titleBarManager;

    /// <summary>
    /// ViewModel exposed for x:Bind in XAML. Raises PropertyChanged so bindings update
    /// when the ViewModel is assigned after construction.
    /// </summary>
    public MainWindowViewModel? ViewModel
    {
        get => _viewModel;
        private set
        {
            if (_viewModel != value)
            {
                _viewModel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ViewModel)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Reads the native journal without creating history during startup or for repeated pages.</summary>
    public bool CanNavigateBack => !_isStartupLoading && ContentFrame.CanGoBack &&
        _navigationRouter?.CanNavigateTo(ContentFrame.BackStack[ContentFrame.BackStack.Count - 1].SourcePageType) == true;

    public bool CanNavigateForward => !_isStartupLoading && ContentFrame.CanGoForward &&
        _navigationRouter?.CanNavigateTo(ContentFrame.ForwardStack[ContentFrame.ForwardStack.Count - 1].SourcePageType) == true;

    /// <summary>Creates only the loading surface so activation can precede service initialization.</summary>
    public MainWindow()
    {
        StartupLogger.Log("MainWindow", "Constructor starting...");
        Title = "win-optimizer";
        _loadingView = new StartupLoadingView();
        Content = _loadingView;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, AppBranding.IconRelativePath));
        Closed += (_, _) => { IsClosed = true; ViewModel?.UpdateCheck.Dispose(); };
    }

    /// <summary>Builds the shell in the existing window after storage and preferences are available.</summary>
    internal void InitializeShell()
    {
        if (_shellInitialized || IsClosed) return;
        _loadingView.ApplyLocalization(App.Services.GetRequiredService<ILocalizationService>());
        Content = null;
        this.InitializeComponent();
        LoadingOverlay.Children.Add(_loadingView);
        _shellInitialized = true;
        StartupLogger.Log("MainWindow", "InitializeComponent completed");

        // Extend content into title bar for custom title bar experience
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set tall title bar mode so caption buttons fill the full height
        this.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;

        // Create TitleBarManager and apply theme-aware caption button colors
        _titleBarManager = new TitleBarManager(this.AppWindow, _logService);
        RootGrid.ActualThemeChanged += (_, _) => _titleBarManager.ApplyThemeToCaptionButtons(RootGrid.ActualTheme);
        RootGrid.Loaded += (_, _) => _titleBarManager.ApplyThemeToCaptionButtons(RootGrid.ActualTheme);

        // Initialize window size manager for position/size persistence
        InitializeWindowSizeManager();

        // Initialize app-local UI zoom (Ctrl +/-/0, Ctrl+MouseWheel)
        InitializeUiZoom();

        // Apply Mica backdrop (Windows 11) with fallback to DesktopAcrylic (Windows 10)
        TrySetMicaBackdrop();

        // Initialize DispatcherService - MUST be done before any service uses it
        InitializeDispatcherService();

        App.Services.GetRequiredService<ISystemMonitorWidgetHost>().SettingsRequested += OpenExtensionSettings;

        // Apply initial FlowDirection for RTL languages and subscribe to language changes
        InitializeFlowDirection();

        // Set up title bar after loaded
        AppTitleBar.Loaded += AppTitleBar_Loaded;

        // Set default navigation after sidebar is loaded
        NavSidebar.Loaded += NavSidebar_Loaded;
    }

    /// <summary>Waits for the actual loading frame before starting synchronous host construction.</summary>
    internal Task WaitForLoadingFrameAsync() =>
        StartupUiCoordinator.RunAfterFirstRenderAsync(_loadingView, () => Task.CompletedTask);

    #region Startup & Initialization

    /// <summary>
    /// Sets the default navigation item after the NavSidebar is loaded.
    /// </summary>
    private void NavSidebar_Loaded(object sender, RoutedEventArgs e)
    {
        StartupLogger.Log("MainWindow", "NavSidebar_Loaded");

        // Skip auto-navigation during startup -- CompleteStartup() will trigger it
        if (_isStartupLoading)
        {
            StartupLogger.Log("MainWindow", "Startup loading in progress, deferring navigation");
            return;
        }

        NavSidebar.SelectedTag = "SoftwareApps";
        _navigationRouter?.NavigateToPage(ContentFrame, "SoftwareApps");
        StartupLogger.Log("MainWindow", "SoftwareApps selected");
    }

    /// <summary>
    /// Kicks off the async startup sequence. Called by App.xaml.cs after Activate + InitializeTheme.
    /// </summary>
    public void StartStartupOperations()
    {
        if (!_shellInitialized || _startupOperationsStarted) return;
        _startupOperationsStarted = true;
        StartupLogger.Log("MainWindow", "StartStartupOperations called");

        _startupUiCoordinator = new StartupUiCoordinator(this.DispatcherQueue, _logService);
        _ = _startupUiCoordinator.RunStartupAndCompleteAsync(
            _loadingView.StatusText, ContentFrame, NavSidebar, LoadingOverlay, RootGrid, () => ViewModel,
            markStartupComplete: () =>
            {
                _isStartupLoading = false;
                UpdateNavigationState();
                _ = App.Services.GetRequiredService<ExtensionsViewModel>().LoadAsync();
                if (_openExtensionSettingsAfterStartup)
                {
                    _openExtensionSettingsAfterStartup = false;
                    _navigationRouter?.NavigateToPage(ContentFrame, "SysMonitor");
                }
            });
    }

    /// <summary>Loads only widget preferences and telemetry during the scheduled desktop launch.</summary>
    public Task StartWidgetStartupAsync()
    {
        if (_extensionLifetime is { } lifetime) return lifetime.StartInBackgroundAsync();
        RestoreFromBackground();
        return Task.CompletedTask;
    }

    /// <summary>Opens the widget's configuration within the Features navigation context.</summary>
    private void OpenExtensionSettings(object? sender, EventArgs e)
    {
        if (_isStartupLoading) _openExtensionSettingsAfterStartup = true;
        else _navigationRouter?.NavigateToPage(ContentFrame, "SysMonitor");
        RestoreFromBackground();
    }

    /// <summary>Restores the existing main window without interrupting a background widget.</summary>
    public void RestoreFromBackground()
    {
        IsManualActivationRequested = true;
        if (!_shellInitialized) { AppWindow.Show(); Activate(); return; }
        StartStartupOperations();
        if (_extensionLifetime is { } lifetime) lifetime.ShowMainWindow();
        else { AppWindow.Show(); Activate(); }
    }

    /// <summary>
    /// Initializes the WindowSizeManager for window position/size persistence
    /// and wires up ApplicationCloseService to save placement and drain pending work.
    /// </summary>
    private void InitializeWindowSizeManager()
    {
        try
        {
            var userPreferencesService = App.Services.GetRequiredService<IUserPreferencesService>();
            _logService = App.Services.GetRequiredService<ILogService>();
            _windowSizeManager = new WindowSizeManager(this.AppWindow, userPreferencesService, _logService);
            _windowSizeManager.InitializeAsync().FireAndForget(_logService!);

            var applicationCloseService = App.Services.GetRequiredService<IApplicationCloseService>();
            if (applicationCloseService is ApplicationCloseService closeService)
            {
                var shutdown = closeService.ShutdownAction;
                _extensionLifetime = new ExtensionApplicationLifetime(
                    App.Services.GetRequiredService<ISystemMonitorWidgetHost>(),
                    App.Services.GetRequiredService<ExtensionsViewModel>(),
                    () => AppWindow.Hide(), () => { StartStartupOperations(); AppWindow.Show(); Activate(); }, shutdown, _logService!);
                closeService.ShutdownAction = () => _extensionLifetime.CloseAsync().FireAndForget(_logService!);
            }
            applicationCloseService.BeforeShutdown = async () =>
            {
                if (_windowSizeManager != null)
                    await _windowSizeManager.SaveWindowSettingsAsync();
                await App.Services.GetRequiredService<WinOptimizer.UI.Features.Cleaner.ViewModels.CleanerViewModel>().FlushAreaSelectionAsync();
                await Task.WhenAll(
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Utilities.ViewModels.EnvironmentEditorViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Customize.ViewModels.ContextMenuViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Optimize.ViewModels.NetworkOptimizationViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Optimize.ViewModels.FeatureOptimizationViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Utilities.ViewModels.HostsEditorViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Utilities.ViewModels.BatteryChargeViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Utilities.ViewModels.ConnectionViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.EventsCleanupViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Security.SecurityViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.WindowsStatusViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.AutorunsViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.ProcessMonitorViewModel>().DeactivateAsync(),
                    App.Services.GetRequiredService<ExtensionsViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Cleaner.ViewModels.CleanerSettingsViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Drivers.ViewModels.DriverSettingsViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.UpdatePreferencesViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.SidebarPreferencesViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.AppColorPreferencesViewModel>().FlushAsync());
            };

            this.AppWindow.Closing += async (sender, args) =>
            {
                args.Cancel = true;
                await applicationCloseService.CheckOperationsAndCloseAsync();
            };
        }
        catch (Exception ex)
        {
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
            _logService?.LogDebug($"Failed to initialize WindowSizeManager: {ex.Message}");
        }
    }

    /// <summary>
    /// Wires up app-local UI zoom: the UiZoomManager, the OemPlus/OemMinus accelerators
    /// that XAML can't express, and Ctrl+MouseWheel.
    /// </summary>
    private void InitializeUiZoom()
    {
        try
        {
            var prefs = App.Services.GetRequiredService<IUserPreferencesService>();
            _logService ??= App.Services.GetRequiredService<ILogService>();
            _uiZoomManager = new UiZoomManager(ZoomViewport, ZoomHost, prefs, _logService);

            // Main-row +/- keys (OemPlus=187, OemMinus=189) — not expressible in XAML.
            RootGrid.KeyboardAccelerators.Add(MakeZoomAccelerator((VirtualKey)187, ZoomInAccelerator_Invoked));
            RootGrid.KeyboardAccelerators.Add(MakeZoomAccelerator((VirtualKey)189, ZoomOutAccelerator_Invoked));

            // Ctrl+MouseWheel, caught even if an inner ScrollViewer already handled the wheel.
            ZoomViewport.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(ZoomViewport_PointerWheelChanged),
                handledEventsToo: true);

            _uiZoomManager.Initialize();
        }
        catch (Exception ex)
        {
            _logService?.LogDebug($"Failed to initialize UI zoom: {ex.Message}");
        }
    }

    private KeyboardAccelerator MakeZoomAccelerator(
        VirtualKey key,
        TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += handler;
        return accelerator;
    }

    /// <summary>
    /// Attempts to set Mica backdrop for Windows 11, falls back to DesktopAcrylic for Windows 10.
    /// </summary>
    private void TrySetMicaBackdrop()
    {
        try
        {
            if (MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
                return;
            }
            if (DesktopAcrylicController.IsSupported())
            {
                SystemBackdrop = new DesktopAcrylicBackdrop();
            }
        }
        catch (Exception ex)
        {
            _logService?.LogDebug($"Failed to set backdrop: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets the initial FlowDirection based on the current language and subscribes to language changes.
    /// </summary>
    private void InitializeFlowDirection()
    {
        try
        {
            var localizationService = App.Services.GetService<ILocalizationService>();
            if (localizationService != null)
            {
                ApplyFlowDirection(localizationService.IsRightToLeft);
                localizationService.LanguageChanged += (_, _) =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ApplyFlowDirection(localizationService.IsRightToLeft);

                        // Refresh locked nav button tooltip in the new language
                        var advancedToolsButton = NavSidebar.GetButton("AdvancedTools");
                        if (advancedToolsButton?.IsLocked == true)
                        {
                            ToolTipService.SetToolTip(advancedToolsButton,
                                localizationService.GetString("Nav_AdvancedTools_Locked_Tooltip") ?? "Unavailable during config review");
                        }
                    });
                };
            }
        }
        catch (Exception ex)
        {
            _logService?.LogDebug($"Failed to initialize FlowDirection: {ex.Message}");
        }
    }

    private void ApplyFlowDirection(bool isRightToLeft)
    {
        RootGrid.FlowDirection = isRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

        if (AppTitleBar.IsLoaded)
        {
            _titleBarManager?.SetTitleBarPadding(
                LeftPaddingColumn, RightPaddingColumn, AppTitleBar, RootGrid.FlowDirection);
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeDialogService();
    }

    private void InitializeDispatcherService()
    {
        try
        {
            var dispatcherService = App.Services.GetRequiredService<IDispatcherService>();
            if (dispatcherService is DispatcherService concreteService)
            {
                concreteService.Initialize(this.DispatcherQueue);
            }
        }
        catch (Exception ex)
        {
            _logService?.LogDebug($"Failed to initialize DispatcherService: {ex.Message}");
        }
    }

    private void InitializeDialogService()
    {
        try
        {
            var dialogService = App.Services.GetRequiredService<IDialogService>();
            if (dialogService is DialogService concreteService)
            {
                concreteService.XamlRoot = RootGrid.XamlRoot;
            }
        }
        catch (Exception ex)
        {
            _logService?.LogDebug($"Failed to initialize DialogService: {ex.Message}");
        }
    }

    #endregion

    #region Title Bar

    /// <summary>
    /// Called when the title bar is loaded. Sets up ViewModel bindings and padding.
    /// </summary>
    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e)
    {
        _titleBarManager?.SetTitleBarPadding(
            LeftPaddingColumn, RightPaddingColumn, AppTitleBar, RootGrid.FlowDirection);

        // Defer passthrough region setup to ensure all elements are laid out
        DispatcherQueue.TryEnqueue(() =>
            _titleBarManager?.SetPassthroughRegions(AppTitleBar, TitleBarNavigation, TitleBarButtons));

        AppTitleBar.SizeChanged += (_, _) =>
            _titleBarManager?.SetPassthroughRegions(AppTitleBar, TitleBarNavigation, TitleBarButtons);
        TitleBarButtons.SizeChanged += (_, _) =>
            _titleBarManager?.SetPassthroughRegions(AppTitleBar, TitleBarNavigation, TitleBarButtons);

        // Initialize ViewModel and wire up bindings
        InitializeViewModel();
    }

    /// <summary>
    /// Initializes the ViewModel and wires up button commands and helper classes.
    /// </summary>
    private void InitializeViewModel()
    {
        try
        {
            ViewModel = App.Services.GetService<MainWindowViewModel>();

            if (ViewModel != null)
            {
                // Wire up button commands
                BugReportButton.Command = ViewModel.BugReportCommand;
                DocsButton.Command = ViewModel.DocsCommand;

                // Set initial filter button icon

                // Subscribe to property changes that require code-behind
                // (mode restrictions and update actions)
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                ViewModel.UpdateCheck.PropertyChanged += UpdateCheck_PropertyChanged;

                // Deferred initialization: subscribes to events and sets initial state
                ViewModel.Initialize();
                ViewModel.SidebarPreferences.LoadAsync().FireAndForget(_logService!);
                App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.AppColorPreferencesViewModel>()
                    .LoadAsync().FireAndForget(_logService!);

                // Pass ViewModel to NavSidebar for localized nav button text
                NavSidebar.ViewModel = ViewModel;

                // Wire up Task Progress Coordinator (Phase 2)
                _taskProgressCoordinator = new TaskProgressCoordinator(
                    TaskProgressControl, TaskProgressControl2, TaskProgressControl3,
                    _logService!, this.DispatcherQueue);

                TaskProgressControl.CancelCommand = ViewModel.TaskProgress.CancelCommand;
                TaskProgressControl.CancelText = ViewModel.TaskProgress.CancelButtonLabel;
                TaskProgressControl.ShowDetailsCommand = ViewModel.TaskProgress.ShowDetailsCommand;
                TaskProgressControl2.ShowDetailsCommand = ViewModel.TaskProgress.ShowDetailsCommand;
                TaskProgressControl3.ShowDetailsCommand = ViewModel.TaskProgress.ShowDetailsCommand;

                ViewModel.TaskProgress.PropertyChanged += (s, e) =>
                    _taskProgressCoordinator?.HandlePropertyChanged(ViewModel.TaskProgress, e.PropertyName);
                ViewModel.TaskProgress.ScriptProgressReceived +=
                    (slotIndex, detail) => _taskProgressCoordinator?.HandleScriptProgressReceived(slotIndex, detail);

                // Wire up Navigation Router (Phase 3)
                _configReviewService = App.Services.GetService<IConfigReviewService>();
                _navBadgeService = App.Services.GetService<INavBadgeService>();
                _navigationRouter = new NavigationRouter(
                    _configReviewService, _navBadgeService, App.Services.GetRequiredService<IDispatcherService>());

                // Subscribe to review mode badge events
                if (_configReviewService != null)
                {
                    _configReviewService.ReviewModeChanged += OnReviewModeBadgeChanged;
                    _configReviewService.BadgeStateChanged += OnBadgeStateChanged;
                }

                // Notify x:Bind that ViewModel is now available
                Bindings.Update();
            }
        }
        catch (Exception ex)
        {
            _logService?.LogDebug($"Failed to initialize ViewModel: {ex.Message}");
        }
    }

    #endregion

    #region Navigation

    private void NavSidebar_ItemClicked(object sender, NavButtonClickedEventArgs e)
    {
        var tag = e.NavigationTag?.ToString();
        StartupLogger.Log("MainWindow", $"NavSidebar_ItemClicked - Tag: {tag}");

        _navigationRouter?.NavigateToPage(ContentFrame, tag);
    }

    #endregion

    #region Review Mode Badges

    private void OnReviewModeBadgeChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_configReviewService?.IsInReviewMode == true)
            {
                _navBadgeService?.SubscribeToSoftwareAppsChanges(() =>
                    DispatcherQueue.TryEnqueue(ApplyNavBadges));
                ApplyNavBadges();

                // Lock Advanced Tools during review mode
                var localizationService = App.Services.GetService<ILocalizationService>();
                NavSidebar.SetButtonLocked("AdvancedTools", true,
                    localizationService?.GetString("Nav_AdvancedTools_Locked_Tooltip") ?? "Unavailable during config review");

                // If currently on Advanced Tools, navigate away to Software & Apps
                var currentTag = _navigationRouter?.GetTagForCurrentPage(ContentFrame.CurrentSourcePageType);
                if (currentTag == "AdvancedTools")
                {
                    _navigationRouter?.NavigateToPage(ContentFrame, "SoftwareApps");
                    NavSidebar.SelectedTag = "SoftwareApps";
                }
            }
            else
            {
                NavSidebar.ClearAllBadges();
                _navBadgeService?.UnsubscribeFromSoftwareAppsChanges();

                // Unlock Advanced Tools
                NavSidebar.SetButtonLocked("AdvancedTools", false);
            }
            UpdateNavigationState();
        });
    }

    private void OnBadgeStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(ApplyNavBadges);
    }

    private void ApplyNavBadges()
    {
        if (_navBadgeService == null) return;
        var badges = _navBadgeService.ComputeNavBadges();
        foreach (var badge in badges)
        {
            NavSidebar.SetButtonBadge(badge.Tag, badge.Count, badge.Style);
        }
    }

    #endregion

    #region PropertyChanged Handlers (code-behind only)

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsTrialMode)) UpdateTrialToolsState();
    }

    /// <summary>Keeps review restrictions in effect for all native Frame navigation paths.</summary>
    private void ContentFrame_Navigating(object sender, Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
    {
        if (_navigationRouter != null && !_navigationRouter.CanNavigateTo(e.SourcePageType)) e.Cancel = true;
    }

    /// <summary>Synchronizes the sidebar and controls after normal, backward or forward navigation.</summary>
    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        var tag = _navigationRouter?.GetTagForCurrentPage(e.SourcePageType);
        if (tag != null) NavSidebar.SelectedTag = tag;
        _navigationRouter?.OnNavigated(e.SourcePageType, ApplyNavBadges);
        UpdateTrialToolsState();
        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNavigateBack)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanNavigateForward)));
    }

    private void NavigateBack_Click(object sender, RoutedEventArgs e)
    {
        if (CanNavigateBack) ContentFrame.GoBack();
    }

    private void NavigateForward_Click(object sender, RoutedEventArgs e)
    {
        if (CanNavigateForward) ContentFrame.GoForward();
    }

    /// <summary>Offers the same history controls through the standard Alt+Left/Right shortcuts.</summary>
    private void HistoryAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (sender.Key == VirtualKey.Left && CanNavigateBack) { ContentFrame.GoBack(); args.Handled = true; }
        if (sender.Key == VirtualKey.Right && CanNavigateForward) { ContentFrame.GoForward(); args.Handled = true; }
    }

    /// <summary>System maintenance tools have no configuration-only operation.</summary>
    private void UpdateTrialToolsState()
    {
        var blocked = ViewModel?.IsTrialMode == true &&
            ContentFrame.CurrentSourcePageType == typeof(WinOptimizer.UI.Features.AdvancedTools.AdvancedToolsPage);
        ContentFrame.IsEnabled = !blocked;
        TrialToolsOverlay.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Handles UpdateCheckViewModel property changes that require code-behind:
    /// dynamic action button creation (cannot be done in XAML).
    /// IsOpen, Title, Message, Severity are now handled by XAML x:Bind.
    /// </summary>
    private void UpdateCheck_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel == null) return;

        if (e.PropertyName == nameof(UpdateCheckViewModel.IsUpdateActionButtonVisible)
            || e.PropertyName == nameof(UpdateCheckViewModel.InstallNowButtonText)
            || e.PropertyName == nameof(UpdateCheckViewModel.IsRelaunchButtonVisible)
            || e.PropertyName == nameof(UpdateCheckViewModel.RelaunchButtonText))
        {
            DispatcherQueue.TryEnqueue(UpdateInfoBarActionButton);
        }
    }

    #endregion

    #region UI Update Helpers

    private void UpdateInfoBarActionButton()
    {
        if (ViewModel == null) return;
        var uc = ViewModel.UpdateCheck;

        if (uc.IsRelaunchButtonVisible)
        {
            var button = new Button
            {
                Content = uc.RelaunchButtonText,
                Command = uc.RelaunchCommand,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"],
            };
            UpdateInfoBar.ActionButton = button;
        }
        else if (uc.IsUpdateActionButtonVisible)
        {
            var button = new Button
            {
                Content = uc.InstallNowButtonText,
                Command = uc.InstallUpdateCommand,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"],
            };
            UpdateInfoBar.ActionButton = button;
        }
        else
        {
            UpdateInfoBar.ActionButton = null;
        }
    }

    #endregion

    #region Event Handlers (XAML-referenced)

    private void OtsElevationInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel?.DismissOtsInfoBar();
    }

    private void UpdateInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel?.UpdateCheck.DismissUpdateInfoBar();
    }

    #endregion

    #region Keyboard Accelerators

    private void NavigateAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var tag = sender.Key switch
        {
            VirtualKey.Number1 => "SoftwareApps",
            VirtualKey.Number2 => "Optimize",
            VirtualKey.Number3 => "Customize",
            VirtualKey.Number4 => "AdvancedTools",
            VirtualKey.Number5 => "Settings",
            _ => null
        };

        if (tag != null)
        {
            NavSidebar.SelectedTag = tag;
            _navigationRouter?.NavigateToPage(ContentFrame, tag);

            // Focus the NavButton so Narrator announces the page name
            var navButton = NavSidebar.GetButton(tag);
            navButton?.Focus(FocusState.Keyboard);

            args.Handled = true;
        }
    }

    private void ZoomInAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _uiZoomManager?.StepUp();
        args.Handled = true;
    }

    private void ZoomOutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _uiZoomManager?.StepDown();
        args.Handled = true;
    }

    private void ZoomResetAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _uiZoomManager?.Reset();
        args.Handled = true;
    }

    private void ZoomViewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            return;

        var delta = e.GetCurrentPoint((UIElement)sender).Properties.MouseWheelDelta;
        if (delta > 0)
            _uiZoomManager?.StepUp();
        else if (delta < 0)
            _uiZoomManager?.StepDown();

        e.Handled = true;
    }

    private void TitleBarAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var button = sender.Key switch
        {
            VirtualKey.Number2 => DonateButton,
            VirtualKey.Number3 => BugReportButton,
            VirtualKey.Number4 => DocsButton,
            _ => (Button?)null
        };

        if (button == null)
        {
            args.Handled = true;
            return;
        }

        if (button.Command?.CanExecute(null) == true)
        {
            button.Command.Execute(null);
        }

        // Announce after a short delay so async state changes are reflected
        DispatcherQueue.TryEnqueue(() =>
        {
            var name = AutomationProperties.GetName(button);
            if (!string.IsNullOrEmpty(name))
            {
                var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(button)
                           ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(button);
                peer?.RaiseNotificationEvent(
                    Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
                    Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
                    name,
                    "TitleBarAction");
            }
        });

        args.Handled = true;
    }

    #endregion
}
