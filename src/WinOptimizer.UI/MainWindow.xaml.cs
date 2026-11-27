using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI.ViewManagement;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Resources;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Utilities;
using WinOptimizer.UI.Features.Dashboard;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Features.Settings.ViewModels;
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
    private const float BackgroundTintOpacity = 0.98f;
    private MainWindowViewModel? _viewModel;
    private WindowSizeManager? _windowSizeManager;
    private Task? _windowPlacementInitialization;
    private UiZoomManager? _uiZoomManager;
    private IConfigReviewService? _configReviewService;
    private INavBadgeService? _navBadgeService;
    private ILogService? _logService;
    private bool _isStartupLoading = true;
    private bool _startupOperationsStarted;
    private string? _extensionDestinationAfterStartup;
    private readonly StartupLoadingView _loadingView;
    private bool _shellInitialized;
    internal bool IsClosed { get; private set; }

    // Helper classes (Phases 2-5)
    private NavigationRouter? _navigationRouter;
    private StartupUiCoordinator? _startupUiCoordinator;
    private PageFadeTransition? _pageFadeTransition;
    private UISettings? _uiSettings;
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

    /// <summary>Hosts workflow result pages in the shell's existing navigation journal.</summary>
    internal Frame NavigationFrame => ContentFrame;

    private Features.SoftwareApps.Services.UninstallWindowSuspension? _uninstallSuspension;

    /// <summary>Protects the shell and native close action until the external uninstaller has exited.</summary>
    internal IDisposable SuspendForUninstall()
    {
        if (_uninstallSuspension is { IsDisposed: false })
            throw new InvalidOperationException("A native uninstaller is already running.");
        return _uninstallSuspension = new(this);
    }

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
        // Use the final client-area geometry from the loader's first frame.
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        SetTitleBar(_loadingView.TitleBar);
        _loadingView.ActualThemeChanged += (_, _) => _titleBarManager?.ApplyThemeToCaptionButtons(_loadingView.ActualTheme);
        _loadingView.Loaded += (_, _) => _titleBarManager?.ApplyThemeToCaptionButtons(_loadingView.ActualTheme);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, AppBranding.IconRelativePath));
        Closed += (_, _) =>
        {
            IsClosed = true;
            _pageFadeTransition?.Dispose();
            ViewModel?.UpdateCheck.Dispose();
        };
    }

    /// <summary>Builds the shell in the existing window after storage and preferences are available.</summary>
    internal void InitializeShell(WindowBackgroundPreferencesViewModel backgroundPreferences, AppColorResources colors)
    {
        if (_shellInitialized || IsClosed) return;
        var requestedTheme = _loadingView.RequestedTheme;
        Content = null;
        this.InitializeComponent();
        TitleBarIcon.Source = new BitmapImage(new Uri(AppBranding.ImageUri));
        _pageFadeTransition = new PageFadeTransition(ContentFrame);
        RootGrid.RequestedTheme = requestedTheme;
        InitializeBackground(backgroundPreferences, colors);
        _loadingView.RequestedTheme = ElementTheme.Default;
        LoadingOverlay.Children.Add(_loadingView);
        _shellInitialized = true;
        StartupLogger.Log("MainWindow", "InitializeComponent completed");

        SetTitleBar(AppTitleBar);
        if (App.Services.GetService<ScanCancellationNotifications>() is { } scanNotifications)
        {
            var presentation = new ScanCancellationPresentation(scanNotifications, ContentFrame, ScanCancellationToast, UpdateNavigationState);
            Closed += (_, _) => presentation.Dispose();
        }
        TitleBarSearch.AttachResults(SearchResultsOverlay);
        TitleBarSearch.DestinationSelected += NavigateToSearchResult;
        UpdatesButton.Flyout.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;

        RootGrid.ActualThemeChanged += (_, _) => _titleBarManager?.ApplyThemeToCaptionButtons(RootGrid.ActualTheme);
        RootGrid.Loaded += (_, _) => _titleBarManager?.ApplyThemeToCaptionButtons(RootGrid.ActualTheme);

        // Wire closing after placement has been restored before activation.
        InitializeWindowLifetime();

        // Initialize app-local UI zoom (Ctrl +/-/0, Ctrl+MouseWheel)
        InitializeUiZoom();

        // Initialize DispatcherService - MUST be done before any service uses it
        InitializeDispatcherService();

        // Apply initial FlowDirection for RTL languages and subscribe to language changes
        InitializeFlowDirection();

        // Set up title bar after loaded
        AppTitleBar.Loaded += AppTitleBar_Loaded;
        NavSidebar.SizeChanged += (_, _) => UpdateSidebarLayout();

        // Set default navigation after sidebar is loaded
    }

    /// <summary>Updates the native background when Settings changes the shared preference.</summary>
    private void InitializeBackground(WindowBackgroundPreferencesViewModel preferences, AppColorResources colors)
    {
        if (!DesktopAcrylicController.IsSupported()) return;

        void ApplyBackground()
        {
            if (preferences.IsBlurEnabled)
            {
                SystemBackdrop ??= new TintedDesktopBackdrop(BackgroundTintOpacity, colors.GetCanvasColor);
                SolidCanvas.Visibility = Visibility.Collapsed;
            }
            else
            {
                SolidCanvas.Visibility = Visibility.Visible;
                SystemBackdrop = null;
            }
        }

        void OnBackgroundChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(WindowBackgroundPreferencesViewModel.IsBlurEnabled)) ApplyBackground();
        }

        ApplyBackground();
        preferences.PropertyChanged += OnBackgroundChanged;
        Closed += (_, _) => preferences.PropertyChanged -= OnBackgroundChanged;
    }

    /// <summary>Waits for the actual loading frame before constructing the full shell.</summary>
    internal Task WaitForLoadingFrameAsync() =>
        StartupUiCoordinator.RunAfterFirstRenderAsync(_loadingView, () => Task.CompletedTask);

    /// <summary>Populates the loader before activation so localization cannot move its visible content.</summary>
    internal void InitializeLoadingPresentation(ILocalizationService localization) => _loadingView.ApplyLocalization(localization);

    /// <summary>Restores placement once, before the loader's first visible frame.</summary>
    internal Task InitializeWindowPlacementAsync(IUserPreferencesService preferences, ILogService logService,
        UserPreferencesSnapshot? snapshot = null)
    {
        _logService = logService;
        _titleBarManager ??= new TitleBarManager(AppWindow, logService);
        _windowSizeManager ??= new WindowSizeManager(AppWindow, preferences, logService);
        return _windowPlacementInitialization ??= _windowSizeManager.InitializeAsync(snapshot);
    }

    #region Startup & Initialization

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
                _ = App.Services.GetRequiredService<NotchExtension>().LoadAsync();
                if (_extensionDestinationAfterStartup is { } destination)
                {
                    _extensionDestinationAfterStartup = null;
                    _navigationRouter?.NavigateToPage(ContentFrame, destination);
                }
            });
    }

    /// <summary>Activates the current shell and routes requests from a widget’s settings button.</summary>
    internal void ActivateFromLaunch(string? destination = null)
    {
        if (destination is not null)
        {
            if (_isStartupLoading) _extensionDestinationAfterStartup = destination;
            else _navigationRouter?.NavigateToPage(ContentFrame, destination);
        }
        if (_windowPlacementInitialization?.IsCompletedSuccessfully != true) return;
        StartStartupOperations();
        AppWindow.Show();
        Activate();
    }

    /// <summary>Refreshes settings edited directly in the desktop companion.</summary>
    internal void InitializeWidgetPreferenceWatcher()
    {
        try
        {
            var watcher = new WidgetPreferencesWatcher(App.Services.GetRequiredService<IInteractiveUserService>(), DispatcherQueue,
                async () =>
                {
                    var monitor = App.Services.GetRequiredService<ExtensionsViewModel>();
                    await Task.WhenAll(monitor.RefreshAsync(automaticLaunch: false, applyToWidget: false),
                        App.Services.GetRequiredService<NotchExtension>().RefreshAsync(automaticLaunch: false));
                    if (App.Services.GetRequiredService<ISystemMonitorWidgetHost>() is CompanionSystemMonitorHost host)
                        host.UpdateObservedState(monitor.Options.Enabled);
                }, _logService!);
            Closed += (_, _) => watcher.Dispose();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        { _logService?.LogError("Unable to watch desktop widget preferences.", ex); }
    }

    /// <summary>
    /// Wires up ApplicationCloseService to save placement and drain pending work.
    /// </summary>
    private void InitializeWindowLifetime()
    {
        try
        {
            _logService ??= App.Services.GetRequiredService<ILogService>();

            var applicationCloseService = App.Services.GetRequiredService<IApplicationCloseService>();
            if (applicationCloseService is ApplicationCloseService closeService)
                closeService.BeforeRestart = WinOptimizer.Infrastructure.Features.Extensions.DesktopExtensionProcess.StopForUpdateAsync;
            applicationCloseService.BeforeShutdown = async () =>
            {
                TitleBarSearch.DismissResults();
                if (_windowSizeManager != null)
                    await _windowSizeManager.SaveWindowSettingsAsync();
                await App.Services.GetRequiredService<WinOptimizer.UI.Features.Cleaner.ViewModels.CleanerViewModel>().FlushAreaSelectionAsync();
                await Task.WhenAll(
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Disks.DiskToolSessions>().WaitForWritesAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Utilities.ViewModels.EnvironmentEditorViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Customize.ViewModels.ContextMenuViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Optimize.ViewModels.NetworkOptimizationViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Optimize.ViewModels.FeatureOptimizationViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Utilities.ViewModels.HostsEditorViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Utilities.ViewModels.BatteryChargeViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.EventsCleanupViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Security.SecurityViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.WindowsStatusViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.AutorunsViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.ApplicationStartupViewModel>().WaitForWriteAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Management.ViewModels.ProcessMonitorViewModel>().DeactivateAsync(),
                    App.Services.GetRequiredService<ExtensionsViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<NotchExtension>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Cleaner.ViewModels.CleanerSettingsViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Drivers.ViewModels.DriverSettingsViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.UpdatePreferencesViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.SidebarPreferencesViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WinOptimizer.UI.Features.Settings.ViewModels.AppColorPreferencesViewModel>().FlushAsync(),
                    App.Services.GetRequiredService<WindowBackgroundPreferencesViewModel>().FlushAsync());
            };

            this.AppWindow.Closing += async (sender, args) =>
            {
                args.Cancel = true;
                if (_uninstallSuspension is { IsDisposed: false }) return;
                await applicationCloseService.CheckOperationsAndCloseAsync();
            };
        }
        catch (Exception ex)
        {
            _logService?.LogDebug($"Failed to initialize window lifetime: {ex.Message}");
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
            _uiZoomManager.FactorChanged += (_, _) => UpdateSidebarLayout();

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
            UpdateSidebarLayout();
            DispatcherQueue.TryEnqueue(UpdateTitleBarInputRegions);
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeDialogService();
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && TitleBarSearch.DismissResults(restoreFocus: true)) e.Handled = true;
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
        UpdateSidebarLayout();

        // Defer passthrough region setup to ensure all elements are laid out
        DispatcherQueue.TryEnqueue(UpdateTitleBarInputRegions);

        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarInputRegions();
        TitleBarLeadingContent.SizeChanged += (_, _) => UpdateTitleBarInputRegions();
        TitleBarSearch.SizeChanged += (_, _) => UpdateTitleBarInputRegions();
        TitleBarButtons.SizeChanged += (_, _) => UpdateTitleBarInputRegions();

        // Initialize ViewModel and wire up bindings
        InitializeViewModel();
    }

    /// <summary>Aligns the unscaled title bar and backdrop with the zoomed navigation pane.</summary>
    private void UpdateSidebarLayout()
    {
        var margin = NavSidebar.Margin;
        var width = (NavSidebar.ActualSidebarWidth + margin.Left + margin.Right) * (_uiZoomManager?.Factor ?? 1);
        SidebarColumn.Width = new GridLength(width);

        // The branding can use more space when caption buttons are on the sidebar side.
        TitleBarLeadingContent.MinWidth = Math.Max(0, width - LeftPaddingColumn.Width.Value);
    }

    /// <summary>Keeps the search field and buttons clickable within the window drag region.</summary>
    private void UpdateTitleBarInputRegions() =>
        _titleBarManager?.SetPassthroughRegions(AppTitleBar, TitleBarNavigation, TitleBarSearch, TitleBarButtons);

    /// <summary>Records search destinations in the same journal as the other shell routes.</summary>
    private void NavigateToSearchResult(ProgramSearchEntry entry) =>
        NavigationRouter.NavigateToDestination(ContentFrame, entry.PageType, entry.Parameter);

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

                // Apply mode restrictions that require code-behind.
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;

                // Deferred initialization: subscribes to events and sets initial state
                ViewModel.Initialize();

                // Pass ViewModel to NavSidebar for localized nav button text
                NavSidebar.ViewModel = ViewModel;

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

                // Return to Software & Apps when entering config review from Advanced Tools.
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
        TitleBarSearch.DismissResults();
        var tag = _navigationRouter?.GetTagForCurrentPage(e.SourcePageType);
        if (tag != null) NavSidebar.SelectedTag = tag;
        _navigationRouter?.OnNavigated(e.SourcePageType, ApplyNavBadges);
        UpdateTrialToolsState();
        UpdateNavigationState();
        if (!_isStartupLoading)
        {
            _uiSettings ??= new UISettings();
            _pageFadeTransition?.Play(TrialToolsOverlay.Visibility == Visibility.Collapsed && _uiSettings.AnimationsEnabled);
        }
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

    #endregion

    #region Event Handlers (XAML-referenced)

    private void OtsElevationInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel?.DismissOtsInfoBar();
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
