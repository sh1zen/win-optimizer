using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Extensions;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinRT.Interop;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Display and accessibility text for one reported compact battery level.</summary>

/// <summary>Hosts the compact desktop grip and the selected expanded Notch modules.</summary>
public sealed partial class NotchWindow : Window, INotchWindow
{
    private const double CompactWidth = 152;
    private const double CompactHeight = 30;
    private const double CompactShoulder = 7;
    private const double CompactBatteryWidth = 83;
    private const double ExpandedWidth = 536;
    private const double ExpandedHeight = 220;
    private const double WeatherHeight = 312;
    private const double CalendarHeight = 424;
    private const double OverviewHeight = 426;
    private const double AnimationMilliseconds = 180;
    private const int WheelStep = 120;
    private readonly ILocalizationService _localization;
    private readonly NotchExtension _extension;
    private readonly ILogService _log;
    private readonly NotchFrame _frame;
    private readonly DispatcherQueueTimer _animation;
    private readonly DispatcherQueueTimer _updates;
    private readonly FocusTimer _focus = new(TimeProvider.System);
    private readonly IDesktopMediaSession _media;
    private readonly NotchReadings _readings;
    private readonly Action<string> _openShortcut;
    private readonly Func<Window, string, string?> _pickShortcutFile;
    private readonly double[] _peaks = new double[12];
    private readonly (NotchView View, RadioButton Tab, RadioButton Dot, FrameworkElement? Page)[] _views;
    private DisplayAreaWatcher? _displays;
    private XamlRoot? _xamlRoot;
    private double _scale;
    private long _animationStarted;
    private double _animationFrom;
    private double _expansion;
    private double _height = ExpandedHeight;
    private double _heightFrom;
    private double _heightTarget = ExpandedHeight;
    private Storyboard? _pageAnimation;
    private int _wheelDelta;
    private long? _lastWheelChange;
    private DateTime _calendarDate;
    private bool _expanded;
    private NotchModule _modules = NotchExtension.DefaultModules;
    private NotchView _view = NotchView.Media;
    private bool _changingView;
    private bool _overviewLayout;
    private bool _closed;
    private bool _reading;
    private bool _commandPending;
    private bool _mediaUnavailable;
    private long? _commandRejected;
    private IRandomAccessStreamReference? _thumbnail;
    private NotchContent _content = NotchContent.Empty;
    private NotchReadingSnapshot _lastReadings = new();
    private (NotchWeatherIconKind Kind, bool IsDay)? _weatherIcon;
    private (DesktopWeatherReading? Reading, string? Language) _weatherForecast;
    private string? _shortcutErrorKey;
    private string? _shortcutSaveError;
    private bool _editingShortcuts;
    private bool _pickingShortcutFile;

    public event EventHandler? WindowClosed;
    public event EventHandler? OpenOptimizerRequested;

    internal NotchWindow(NotchExtension extension, ILocalizationService localization, ILogService log, IDesktopMediaSession media,
        NotchReadings? readings = null, DesktopWeather? weather = null, Action<string>? openShortcut = null,
        Func<Window, string, string?>? pickShortcutFile = null, Func<string, Task>? shareFile = null,
        IDesktopApplications? applications = null, IDesktopQuickSettings? quickSettings = null, IConnectionTrafficService? traffic = null)
    {
        _extension = extension;
        _localization = localization;
        _log = log;
        _media = media;
        _openShortcut = openShortcut ?? DesktopExtensionProcess.OpenDesktopShortcut;
        _applications = applications ?? new WindowsDesktopApplications();
        _pickShortcutFile = pickShortcutFile ?? ((owner, label) => Win32FileDialogHelper.ShowOpenFilePicker(owner, label, label, "*.*"));
        _readings = readings ?? new(log, WindowsSystemMonitorService.CreateCompactMonitor,
            token => WindowsDesktopBatteries.ReadAsync(log, token),
            weather is null ? null : weather.ReadAsync, WindowsDesktopAudio.ReadMicrophone, WindowsDesktopAudio.ReadOutputPeak,
            new(WindowsDesktopStatus.ReadPower, WindowsDesktopAudio.ReadOutputVolume, WindowsDesktopStatus.ReadBrightness,
                WindowsDesktopStatus.ReadConnectedDevicesAsync, WindowsDesktopStatus.ReadConnectedLocalDevicesAsync,
                WindowsDesktopFrameRate.WatchAsync, WindowsDesktopStatus.ReadConnectedBatteryAsync),
            traffic is null ? null : traffic.ReadTodayAsync);
        InitializeComponent();
        DashboardPanel.ReadingHeightChanged += (_, _) =>
        {
            if (!_closed && ShowsDashboard && _xamlRoot is not null && _heightTarget != ViewHeight()) StartFrameTransition();
        };
        QuickSettingsPanel.Configure(quickSettings ?? new WindowsDesktopQuickSettings(log), localization, log);
        _compactSlots = new()
        {
            [NotchModule.Cpu] = CompactCpu,
            [NotchModule.Gpu] = CompactGpu,
            [NotchModule.Memory] = CompactMemory,
            [NotchModule.Traffic] = CompactTraffic,
            [NotchModule.Batteries] = CompactBatteries,
            [NotchModule.AudioVisualizer] = CompactAudioVisualizer,
            [NotchModule.FrameRate] = CompactFpsStrip,
            [NotchModule.Clock] = CompactTime
        };
        _compactHold = DispatcherQueue.CreateTimer();
        _compactHold.Interval = TimeSpan.FromMilliseconds(350);
        _compactHold.IsRepeating = false;
        _compactHold.Tick += OnCompactHold;
        var handle = WindowNative.GetWindowHandle(this);
        if (shareFile is null)
        {
            _fileShare = new(handle);
            shareFile = _fileShare.ShowAsync;
        }
        _shareFile = shareFile;
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(2300);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => HideStatus();
        var waveformColor = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);
        foreach (var _ in _peaks)
        {
            AudioWave.Children.Add(new Border { Width = 2, Height = 2, CornerRadius = new CornerRadius(1),
                Background = waveformColor, VerticalAlignment = VerticalAlignment.Center });
            MediaWave.Children.Add(new Border { Width = 2, Height = 2, CornerRadius = new CornerRadius(1),
                Background = waveformColor, VerticalAlignment = VerticalAlignment.Center });
        }
        _seekTimer = DispatcherQueue.CreateTimer();
        _seekTimer.Interval = TimeSpan.FromMilliseconds(160);
        _seekTimer.IsRepeating = false;
        _seekTimer.Tick += CommitMediaSeek;
        TrackSeek.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnMediaSeekPressed), true);
        TrackSeek.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnMediaSeekFinished), true);
        TrackSeek.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnMediaSeekFinished), true);
        TrackSeek.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnMediaSeekCanceled), true);
        Panel.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPageWheel), handledEventsToo: true);
        _views =
        [
            (NotchView.Overview, OverviewTab, OverviewDot, null),
            (NotchView.Media, MediaTab, MediaDot, MediaPanel),
            (NotchView.Focus, FocusTab, FocusDot, FocusPanel),
            (NotchView.Calendar, CalendarTab, CalendarDot, CalendarPage),
            (NotchView.Shortcuts, ShortcutsTab, ShortcutsDot, ShortcutsCard),
            (NotchView.Weather, WeatherTab, WeatherDot, WeatherPage),
            (NotchView.Dashboard, DashboardTab, DashboardDot, DashboardPanel),
            (NotchView.Batteries, BatteriesTab, BatteriesDot, BatteriesPanel),
            (NotchView.QuickSettings, QuickSettingsTab, QuickSettingsDot, QuickSettingsPanel)
        ];
        _overviewDropTargets = CreateOverviewDropTargets();
        RegisterOverviewDrag();
        _frame = new(handle);
        _animation = DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(16);
        _animation.Tick += OnAnimationTick;
        _updates = DispatcherQueue.CreateTimer();
        _updates.Tick += OnUpdateTick;
        Closed += OnClosed;
        Activated += OnActivated;
        Root.Loaded += (_, _) =>
        {
            _xamlRoot = Root.XamlRoot;
            _scale = _xamlRoot.RasterizationScale;
            _xamlRoot.Changed += OnXamlRootChanged;
            RefreshFrame();
            UpdateReadings();
        };
        RefreshLabels();
        RenderView();
        ShowMedia(null);
    }

    private NotchModule CompactModules => _modules & _extension.PinnedModules;

    /// <summary>Applies the enabled modules and confirmed compact pins to the current views.</summary>
    public void ApplyModules(NotchModule modules)
    {
        bool showSysInfo = (_modules & NotchExtension.DashboardModules) == 0 && (modules & NotchExtension.DashboardModules) != 0;
        EndCompactDrag(restoreOrder: false);
        ApplyCompactOrder(_extension.CompactOrder);
        bool wasShowingMedia = ShowsMedia;
        if (_modules.HasFlag(NotchModule.Focus) && !modules.HasFlag(NotchModule.Focus)) _focus.Reset();
        _modules = modules;
        if (modules.HasFlag(NotchModule.StatusAlerts)) _statusChanges ??= new();
        else if (_statusChanges is not null)
        {
            _statusChanges = null;
            HideStatus();
        }
        if (!CompactModules.HasFlag(NotchModule.FrameRate)) _fpsUnavailableReported = false;
        if (_xamlRoot is null && HasView(NotchView.Overview))
        {
            _view = NotchView.Overview;
        }
        else if (showSysInfo && !_expanded) _view = NotchView.Dashboard;
        else if (!HasView(_view))
        {
            _view = _views.FirstOrDefault(page => HasView(page.View)).View;
        }
        if (!wasShowingMedia) PrepareMediaView();
        DashboardPanel.ApplyModules(modules, _localization);
        var compact = CompactModules;
        CompactCpu.Visibility = VisibilityFor(compact.HasFlag(NotchModule.Cpu));
        CompactGpu.Visibility = VisibilityFor(compact.HasFlag(NotchModule.Gpu));
        CompactMemory.Visibility = VisibilityFor(compact.HasFlag(NotchModule.Memory));
        CompactTraffic.Visibility = VisibilityFor(compact.HasFlag(NotchModule.Traffic));
        if (!compact.HasFlag(NotchModule.AudioVisualizer)) ResetAudioWave();
        RenderCompactStatus();
        RenderView();
        RenderTimer();
        if (_xamlRoot is not null)
        {
            if (_expanded && _heightTarget != ViewHeight()) StartFrameTransition();
            else RefreshFrame();
        }
        UpdateReadings();
        ScheduleUpdates();
        UpdateMediaAsync();
    }

    void INotchWindow.ApplyContent(NotchContent content)
    {
        bool locationChanged = _content.WeatherLocation != content.WeatherLocation;
        bool shortcutsChanged = _content.Shortcuts != content.Shortcuts;
        bool overviewChanged = _content.OverviewEnabled != content.OverviewEnabled ||
            _content.OverviewTiles != content.OverviewTiles;
        bool wasShowingMedia = ShowsMedia;
        if (locationChanged) _lastReadings = _lastReadings with { Weather = null, WeatherAttempted = false };
        _content = content;
        if (overviewChanged)
        {
            if (_xamlRoot is null && HasView(NotchView.Overview)) _view = NotchView.Overview;
            else if (!HasView(_view)) _view = _views.FirstOrDefault(page => HasView(page.View)).View;
            RenderView();
            if (wasShowingMedia != ShowsMedia) PrepareMediaView();
            ScheduleUpdates();
            UpdateMediaAsync();
        }
        if (shortcutsChanged)
        {
            _shortcutErrorKey = null;
            _shortcutSaveError = null;
            if (content.Shortcuts.Length == 0) _editingShortcuts = false;
            RenderShortcutItems();
            RenderShortcutsStatus();
        }
        RenderWeather();
        if (_expanded && (overviewChanged || _view == NotchView.Shortcuts) && _heightTarget != ViewHeight()) StartFrameTransition();
        if (overviewChanged || locationChanged) UpdateReadings();
    }

    Task INotchWindow.StopReadingsAsync() => _readings.StopAsync();

    void INotchWindow.ApplyPlacement(NotchPlacement placement)
    {
        _frame.ApplyPlacement(placement);
        if (_xamlRoot is not null) RefreshFrame();
    }

    /// <summary>Pins the window without moving keyboard focus from the app the user is using.</summary>
    public void Show()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        _frame.ConfigureChrome();
        _displays = DisplayArea.CreateWatcher();
        _displays.Added += OnDisplayUpdated;
        _displays.Updated += OnDisplayUpdated;
        _displays.Removed += OnDisplayUpdated;
        _displays.Start();
        _localization.LanguageChanged += OnLanguageChanged;
        ApplyFrame();
        AppWindow.Show(activateWindow: false);
        ScheduleUpdates();
    }

    private bool ShowsMedia => _expanded && IsModuleVisible(NotchView.Media);
    private bool ShowsWeather => _expanded && IsModuleVisible(NotchView.Weather);
    private bool HasWeatherForecast => _lastReadings.Weather is { } weather && (weather.Hours.Count > 0 || weather.Days.Count > 0);
    private bool ShowsCalendar => _expanded && IsModuleVisible(NotchView.Calendar);
    private bool ShowsDashboard => _expanded && IsModuleVisible(NotchView.Dashboard);
    private bool ShowsBatteries => _expanded && IsModuleVisible(NotchView.Batteries);

    /// <summary>Hides the interior during resizes so it never reflows through the compact width.</summary>
    private void SetExpanded(bool expanded)
    {
        if (_closed || expanded == _expanded) return;
        EndCompactDrag();
        _expanded = expanded;
        if (!expanded)
        {
            EndOverviewDrag();
            CloseAppPicker();
            SetEditingShortcuts(false);
        }
        _pageAnimation?.Stop();
        _wheelDelta = 0;
        Grip.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        Panel.Visibility = Visibility.Collapsed;
        UpdateStatusLayout();
        PrepareMediaView();
        StartFrameTransition();
        UpdateReadings();
        ScheduleUpdates();
    }

    /// <summary>Resize the native panel from its current dimensions, including when a transition is interrupted.</summary>
    private void StartFrameTransition()
    {
        _animation.Stop();
        _animationFrom = _expansion;
        _heightFrom = _height;
        _heightTarget = ViewHeight();
        if (new UISettings().AnimationsEnabled)
        {
            _animationStarted = TimeProvider.System.GetTimestamp();
            _animation.Start();
        }
        else CompleteTransition();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var progress = Math.Min(1, TimeProvider.System.GetElapsedTime(_animationStarted).TotalMilliseconds / AnimationMilliseconds);
        if (progress >= 1) { CompleteTransition(); return; }
        var eased = 1 - Math.Pow(1 - progress, 3);
        _expansion = _animationFrom + ((_expanded ? 1 : 0) - _animationFrom) * eased;
        _height = _heightFrom + (_heightTarget - _heightFrom) * eased;
        RefreshFrame();
    }

    private void CompleteTransition()
    {
        _animation.Stop();
        _expansion = _expanded ? 1 : 0;
        _height = _heightTarget;
        RefreshFrame();
        if (_closed || !_expanded) return;
        if (Panel.Visibility != Visibility.Visible)
        {
            Panel.Visibility = Visibility.Visible;
            (_views.FirstOrDefault(page => page.View == _view).Tab as Control ?? SettingsButton).Focus(FocusState.Programmatic);
        }
        UpdateMediaAsync();
    }

    private double ViewHeight() => _view switch
    {
        NotchView.Overview => OverviewHeight,
        NotchView.Calendar => CalendarHeight,
        NotchView.Shortcuts => _content.Shortcuts.Length > 5 ? 348 : 250,
        NotchView.Weather when HasWeatherForecast => WeatherHeight,
        NotchView.Dashboard => ReadingViewHeight(DashboardPanel.PreferredHeight),
        NotchView.Batteries => ReadingViewHeight(BatteriesPanel.PreferredHeight),
        NotchView.QuickSettings => 232,
        _ => ExpandedHeight
    };

    /// <summary>Fits the reading rows and the visible navigation dots below the header.</summary>
    private double ReadingViewHeight(double contentHeight)
    {
        var railHeight = PageRail.Visibility == Visibility.Visible
            ? PageRail.Children.OfType<RadioButton>().Where(dot => dot.Visibility == Visibility.Visible)
                .Sum(dot => dot.Height + PageRail.Spacing) - PageRail.Spacing
            : 0;
        var margin = ViewPanelMargin;
        return margin.Top + NavigationHeader.Height + Panel.RowSpacing +
            Math.Max(contentHeight, railHeight) + margin.Bottom;
    }

    private void ApplyFrame()
    {
        var compact = CompactModules;
        var metrics = BitOperations.PopCount((uint)(compact & (NotchModule.Cpu | NotchModule.Gpu | NotchModule.Memory)));
        bool statusVisible = StatusBanner.Visibility == Visibility.Visible;
        // In the attached outline, the shoulders sit outside the banner's side margins.
        var compactWidth = statusVisible ? StatusBanner.Width + StatusBanner.Margin.Left + StatusBanner.Margin.Right +
            (_extension.Placement.Floating ? 0 : 2 * CompactShoulder) :
            Math.Max(CompactWidth, 88 + 111 * metrics + (compact.HasFlag(NotchModule.Traffic) ? 100 : 0)
            + CompactBatteryWidth * CompactBatteries.Items.Count
            + (CompactMicBadge.Visibility == Visibility.Visible ? 117 : 0)
            + (CompactAudioVisualizer.Visibility == Visibility.Visible ? 57 : 0)
            + (CompactFpsStrip.Visibility == Visibility.Visible ? 82 : 0));
        var compactHeight = statusVisible ? StatusBannerSpace : CompactHeight;
        var expandedHeight = _height + (statusVisible ? StatusBannerSpace - DefaultPanelMargin.Top : 0);
        _frame.Update(compactWidth + (ExpandedWidth - compactWidth) * _expansion,
            compactHeight + (expandedHeight - compactHeight) * _expansion, 13 + 18 * _expansion,
            CompactShoulder + 6 * _expansion);
    }

    private void RefreshFrame(bool refreshDisplay = false)
    {
        if (_closed) return;
        try
        {
            if (refreshDisplay) _frame.RefreshDisplay();
            ApplyFrame();
        }
        catch (Exception ex) when (ex is Win32Exception or COMException or ArgumentException)
        {
            _log.LogError("Could not position the Notch window.", ex);
            Close();
        }
    }

    private void OnDisplayUpdated(DisplayAreaWatcher sender, DisplayArea area) =>
        DispatcherQueue.TryEnqueue(() => RefreshFrame(refreshDisplay: true));

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_scale == sender.RasterizationScale) return;
        _scale = sender.RasterizationScale;
        RefreshFrame();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // The owned native picker temporarily takes focus from the widget.
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        EndCompactDrag();
        if (!_pickingShortcutFile) SetExpanded(false);
    }

    private void OnExpand(object sender, RoutedEventArgs args) => SetExpanded(true);
    private void OnCollapse(object sender, RoutedEventArgs args) => SetExpanded(false);

    private void OnOpenOptimizer(object sender, RoutedEventArgs args)
    {
        SetExpanded(false);
        OpenOptimizerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnViewChecked(object sender, RoutedEventArgs args)
    {
        if (_changingView || sender is not RadioButton radio) return;
        SelectView(_views.FirstOrDefault(page => page.Tab == radio || page.Dot == radio).View);
    }

    private void SelectView(NotchView view)
    {
        if (view == _view || !HasView(view)) return;
        var direction = view > _view ? 1 : -1;
        bool wasShowingMedia = ShowsMedia;
        EndOverviewDrag();
        _view = view;
        if (!wasShowingMedia) PrepareMediaView();
        RenderView();
        if (_expanded)
        {
            if (_heightTarget != ViewHeight()) StartFrameTransition();
            AnimatePage(direction);
        }
        UpdateReadings();
        ScheduleUpdates();
        UpdateMediaAsync();
    }

    private bool StepView(int direction)
    {
        var candidate = _views.Where(page => HasView(page.View) &&
            (direction < 0 ? page.View < _view : page.View > _view));
        var next = (direction < 0 ? candidate.LastOrDefault() : candidate.FirstOrDefault()).View;
        if (next == NotchView.None) return false;
        SelectView(next);
        return true;
    }

    private void OnPageWheel(object sender, PointerRoutedEventArgs args)
    {
        var pointer = args.GetCurrentPoint(Panel).Properties;
        if (pointer.IsHorizontalMouseWheel || IsWithin(args.OriginalSource, CalendarPanel) ||
            IsWithin(args.OriginalSource, Phase) || IsWithin(args.OriginalSource, TimerMinutes) || IsWithin(args.OriginalSource, AppPicker) ||
            IsWithin(args.OriginalSource, QuickSettingsPanel) ||
            (DashboardPanel.CanScroll && IsWithin(args.OriginalSource, DashboardPanel)) ||
            (BatteriesPanel.CanScroll && IsWithin(args.OriginalSource, BatteriesPanel)) ||
            (ShortcutsPage.ScrollableHeight > 0 && IsWithin(args.OriginalSource, ShortcutsPage)))
            return;
        args.Handled |= HandlePageWheel(pointer.MouseWheelDelta);
    }

    private static bool IsWithin(object? source, DependencyObject control)
    {
        for (var element = source as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element == control) return true;
        return false;
    }

    /// <summary>Combine small touchpad deltas and limit each burst to one page change.</summary>
    internal bool HandlePageWheel(int delta)
    {
        if (!_expanded || delta == 0 || PageRail.Visibility != Visibility.Visible) return false;
        if (_lastWheelChange is long last && TimeProvider.System.GetElapsedTime(last).TotalMilliseconds < 220) return true;
        if (Math.Sign(delta) != Math.Sign(_wheelDelta)) _wheelDelta = 0;
        _wheelDelta += delta;
        if (Math.Abs(_wheelDelta) < WheelStep) return true;
        var direction = _wheelDelta > 0 ? -1 : 1;
        _wheelDelta = 0;
        if (!StepView(direction)) return false;
        _lastWheelChange = TimeProvider.System.GetTimestamp();
        return true;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args) =>
        PageClip.Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);

    /// <summary>Bring the next page in from its direction of travel within the clipped viewport.</summary>
    private void AnimatePage(int direction)
    {
        _pageAnimation?.Stop();
        if (Panel.Visibility != Visibility.Visible || !new UISettings().AnimationsEnabled) return;
        _pageAnimation = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(210));
        var slide = new DoubleAnimation { From = direction * 19, To = 0, Duration = duration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        Storyboard.SetTarget(slide, PageMotion);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.Y));
        var fade = new DoubleAnimation { From = 0.18, To = 1, Duration = duration, FillBehavior = FillBehavior.Stop };
        Storyboard.SetTarget(fade, PageContent);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        _pageAnimation.Children.Add(slide);
        _pageAnimation.Children.Add(fade);
        _pageAnimation.Begin();
    }

    private bool HasView(NotchView view) => view == NotchView.Overview
        ? _content.OverviewEnabled == true : NotchExtension.HasView(view, _modules);

    private bool IsModuleVisible(NotchView view) => _view == view ||
        (_view == NotchView.Overview && _content.OverviewTiles.Contains(view) && HasView(view));

    /// <summary>Synchronizes tabs and content, including the dashboard-only and empty configurations.</summary>
    private void RenderView()
    {
        if (_overviewPress is { } press && (_view != NotchView.Overview || !HasView(press.View))) EndOverviewDrag();
        if (!IsModuleVisible(NotchView.Shortcuts))
        {
            CloseAppPicker();
            SetEditingShortcuts(false);
        }
        _changingView = true;
        try
        {
            int count = 0;
            foreach (var page in _views)
            {
                bool visible = HasView(page.View);
                bool selected = page.View == _view;
                page.Tab.Visibility = page.Dot.Visibility = VisibilityFor(visible);
                page.Tab.IsChecked = page.Dot.IsChecked = selected;
                if (page.Page is { } content) content.Visibility = VisibilityFor(IsModuleVisible(page.View));
                if (visible) count++;
            }
            PageRail.Visibility = VisibilityFor(count > 1);
            EmptyPanel.Visibility = VisibilityFor(_view == NotchView.None);
            ArrangeOverview();
            UpdatePanelMargin();
        }
        finally { _changingView = false; }
    }

    /// <summary>Reflows the existing interactive pages into a quad without creating another set of widgets.</summary>
    private void ArrangeOverview()
    {
        bool overview = _view == NotchView.Overview;
        if (!overview && !_overviewLayout) return;
        bool dropping = overview && _overviewDragging;
        var slots = _content.OverviewTiles;
        int top = dropping ? 2 : slots.Take(NotchOverviewLayout.Columns).Count(HasView);
        int bottom = dropping ? 2 : slots.Skip(NotchOverviewLayout.Columns).Count(HasView);
        PageContent.RowSpacing = overview && top > 0 && bottom > 0 ? 6 : 0;
        PageContent.ColumnSpacing = overview ? 6 : 0;
        LeadRow.Height = new GridLength(overview ? (top > 0 ? 3 : 0) : 1, GridUnitType.Star);
        DetailRow.Height = new GridLength(overview && bottom > 0 ? 4 : 0, GridUnitType.Star);
        DetailColumn.Width = new GridLength(overview ? 1 : 0, GridUnitType.Star);
        foreach (var page in _views)
        {
            if (page.Page is not { } content) continue;
            int slot = overview ? Array.IndexOf(slots, page.View) : -1;
            int row = slot < 0 ? 0 : slot / NotchOverviewLayout.Columns;
            bool spansRow = slot >= 0 && (row == 0 ? top : bottom) == 1;
            Grid.SetRow(content, row);
            Grid.SetColumn(content, slot < 0 || spansRow ? 0 : slot % NotchOverviewLayout.Columns);
            Grid.SetColumnSpan(content, spansRow ? NotchOverviewLayout.Columns : 1);
        }
        OverviewHint.Visibility = VisibilityFor(overview && top == 0 && bottom == 0);
        if (!overview) OverviewStatus.Visibility = Visibility.Collapsed;
        foreach (var target in _overviewDropTargets) target.Visibility = VisibilityFor(dropping);
        if (overview == _overviewLayout) return;
        _overviewLayout = overview;

        var tileBrush = overview ? (Brush)Root.Resources["NotchOverviewSurface"] : null;
        var normalBrush = (Brush)Root.Resources["NotchSurface"];
        MediaPanel.Background = CalendarPage.Background = tileBrush;
        FocusPanel.Background = QuickSettingsPanel.Background = tileBrush;
        FocusPanel.VerticalAlignment = overview ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        FocusPanel.Margin = overview ? new Thickness(0) : new Thickness(0, 0, 9, 0);
        FocusPanel.Padding = overview ? new Thickness(8) : new Thickness(0);
        FocusPanel.ColumnSpacing = overview ? 5 : 17;
        FocusControls.Spacing = overview ? 4 : 10;
        FocusButtons.Orientation = overview ? Orientation.Vertical : Orientation.Horizontal;
        Remaining.FontSize = overview ? 28 : 45;
        if (overview)
        {
            Phase.MinWidth = 0;
            TimerMinutes.MinWidth = 0;
        }
        else
        {
            Phase.ClearValue(FrameworkElement.MinWidthProperty);
            TimerMinutes.ClearValue(FrameworkElement.MinWidthProperty);
        }
        QuickSettingsPanel.SetOverviewLayout(overview);
        WeatherPage.Background = CalendarPanel.Background = ArtworkFrame.Background = ShortcutsCard.Background = tileBrush ?? normalBrush;
        MediaPanel.VerticalAlignment = overview ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        MediaPanel.Margin = overview ? new Thickness(0) : new Thickness(0, 0, 9, 0);
        MediaPanel.Padding = overview ? new Thickness(14, 10, 14, 12) : new Thickness(0);
        MediaPanel.RowDefinitions[0].Height = overview ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        MediaPanel.ColumnSpacing = overview ? 10 : 17;
        MediaPanel.RowSpacing = overview ? 7 : 10;
        ArtworkFrame.Width = ArtworkFrame.Height = overview ? 58 : 91;
        TrackTitle.FontSize = overview ? 15 : 21;
        TrackArtist.FontSize = overview ? 11 : 13;
        MediaTransport.Spacing = overview ? 5 : 10;
        PreviousButton.Width = NextButton.Width = overview ? 27 : 32;
        PlaybackButton.Width = overview ? 34 : 41;
        if (overview)
        {
            PlaybackButton.Background = (Brush)Root.Resources["NotchTransparent"];
            PlaybackButton.Foreground = (Brush)Root.Resources["NotchInk"];
        }
        else
        {
            PlaybackButton.ClearValue(Control.BackgroundProperty);
            PlaybackButton.ClearValue(Control.ForegroundProperty);
        }

        ShortcutsCard.Padding = overview ? new Thickness(12, 6, 12, 8) : (Thickness)Root.Resources["FullShortcutsPadding"];
        ShortcutsHeading.FontSize = overview ? 13 : 16;
        ShortcutItems.ItemsPanel = (ItemsPanelTemplate)Root.Resources[overview ? "OverviewShortcutsLayout" : "FullShortcutsLayout"];
        RenderShortcutItems();
        RenderShortcutsStatus();
        WeatherPage.Padding = overview ? new Thickness(15, 15, 15, 13) : new Thickness(18, 8, 18, 8);
        WeatherDetails.VerticalAlignment = overview ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        WeatherReadingRow.Height = overview ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        WeatherPlace.FontSize = overview ? 14 : 16;
        WeatherTemperature.FontSize = overview ? 42 : 39;
        UpdateWeatherForecastVisibility();
        ArrangeCalendar(overview);
    }

    /// <summary>Shows the Today card beside the calendar in its dedicated view.</summary>
    private void ArrangeCalendar(bool overview)
    {
        CalendarDateColumn.Width = new GridLength(overview ? 0 : 110);
        CalendarPage.ColumnSpacing = overview ? 0 : 12;
        TodayButton.Visibility = VisibilityFor(!overview);
    }

    private static Visibility VisibilityFor(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private void PrepareMediaView()
    {
        CancelMediaSeek();
        _seekPointer = null;
        if (!ShowsMedia) return;
        _mediaUnavailable = false;
        _commandRejected = null;
        ShowMedia(null);
    }

    private void OnPhaseChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Phase.SelectedIndex < 0) return;
        _focus.Select((FocusPhase)Phase.SelectedIndex);
        RenderTimer();
        ScheduleUpdates();
    }

    private void OnTimerToggle(object sender, RoutedEventArgs args)
    {
        _focus.Toggle();
        RenderTimer();
        ScheduleUpdates();
    }

    private void OnTimerDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updates is null || !double.IsFinite(args.NewValue)) return;
        _focus.SetCustomDuration((int)Math.Round(args.NewValue));
        RenderTimer();
    }

    private void OnTimerReset(object sender, RoutedEventArgs args)
    {
        _focus.Reset();
        RenderTimer();
        ScheduleUpdates();
    }

    private void OnUpdateTick(DispatcherQueueTimer sender, object args)
    {
        if (_focus.IsRunning)
        {
            _focus.Update();
            RenderTimer();
        }
        else UpdateCompactTime();
        if (ShowsCalendar && _calendarDate != DateTime.Today) RefreshCalendarDate();
        if (ShowsDashboard) DashboardPanel.RefreshUptime(_localization);
        UpdateMediaAsync();
        ScheduleUpdates();
    }

    private void ScheduleUpdates()
    {
        if (_closed) return;
        bool frequent = _focus.IsRunning || (ShowsMedia && !_mediaUnavailable);
        if (frequent || CompactModules.HasFlag(NotchModule.Clock) || ShowsCalendar ||
            (ShowsDashboard && _modules.HasFlag(NotchModule.Uptime)))
        {
            var interval = TimeSpan.FromMilliseconds(frequent ? 600 : 1000);
            if (_updates.Interval != interval) _updates.Interval = interval;
            if (!_updates.IsRunning) _updates.Start();
        }
        else _updates.Stop();
    }

    private void UpdateReadings()
    {
        if (_closed || _xamlRoot is null) return;
        DashboardPanel.SetActive(ShowsDashboard, _localization);
        if (ShowsBatteries) RenderBatteries();
        QuickSettingsPanel.SetActive(_expanded && IsModuleVisible(NotchView.QuickSettings));
        var active = (_modules & NotchReadings.BackgroundModules) | (CompactModules & NotchModule.FrameRate);
        if (!_expanded) active |= CompactModules & NotchModule.AudioVisualizer;
        if (ShowsWeather) active |= NotchModule.Weather;
        bool showMediaAudio = ShowsMedia && _mediaSnapshot?.IsPlaying == true;
        if (!showMediaAudio) ResetAudioWave(MediaWave);
        if (_expanded) ResetAudioWave(AudioWave);
        _readings.Apply(active, showMediaAudio, _content.WeatherLocation, PublishViewReadings, PublishSystemReadings,
            PublishBatteryReadings, PublishAudioPeak,
            usage => DashboardPanel.PublishToday(usage, _localization));
    }

    /// <summary>Merges the current view readings into the separately sampled background telemetry.</summary>
    private void PublishViewReadings(NotchReadingSnapshot snapshot)
    {
        _lastReadings = snapshot with { System = _lastReadings.System, Batteries = _lastReadings.Batteries };
        if (_statusChanges?.Observe(snapshot) is { } notice) ShowStatus(notice);
        if (snapshot.FrameRateUnavailable && !_fpsUnavailableReported)
        {
            _fpsUnavailableReported = true;
            ShowStatus(new("Notch_FrameRateUnavailable", NotchNoticeIcon.Fps));
        }
        if (ShowsWeather) RenderWeather();
        RenderCompactStatus(updateBatteries: false);
    }

    /// <summary>Records every system sample, including genuine gaps, even when the graphs are hidden.</summary>
    private void PublishSystemReadings(SystemMonitorSnapshot? system)
    {
        var snapshot = _lastReadings = _lastReadings with { System = system };
        DashboardPanel.PublishSystem(snapshot, _localization);
        RenderCompactPercent(CpuMeter, CpuValue, system?.CpuUsagePercent);
        RenderCompactPercent(GpuMeter, GpuValue, snapshot.GpuPercent);
        RenderCompactPercent(MemoryMeter, MemoryValue, snapshot.MemoryPercent);
        CompactTraffic.Text = $"↓ {SystemMonitorReadingsViewModel.Rate(system?.DownloadBytesPerSecond)}";
    }

    /// <summary>Refreshes the battery view and any compact badges sharing the same sample.</summary>
    private void PublishBatteryReadings(IReadOnlyList<DesktopBattery>? batteries)
    {
        if (ReferenceEquals(_lastReadings.Batteries, batteries)) return;
        _lastReadings = _lastReadings with { Batteries = batteries };
        if (ShowsBatteries) RenderBatteries();
        RenderCompactStatus();
    }

    /// <summary>Refits the battery view when cards arrive or disappear.</summary>
    private void RenderBatteries()
    {
        BatteriesPanel.Publish(_lastReadings.Batteries, _localization);
        if (_xamlRoot is not null && _heightTarget != ViewHeight()) StartFrameTransition();
    }

    private void RenderCompactStatus(bool updateBatteries = true)
    {
        bool batteryWidthChanged = updateBatteries && RenderCompactBatteries();
        bool showAudio = CompactModules.HasFlag(NotchModule.AudioVisualizer);
        var audioVisibility = VisibilityFor(showAudio);
        var microphone = showAudio ? _lastReadings.Microphone : null;
        var microphoneVisibility = VisibilityFor(microphone?.IsInUse == true);
        var fps = CompactModules.HasFlag(NotchModule.FrameRate) ? _lastReadings.FrameRate : null;
        var fpsVisibility = VisibilityFor(fps.HasValue);
        bool widthChanged = batteryWidthChanged || CompactAudioVisualizer.Visibility != audioVisibility || CompactMicBadge.Visibility != microphoneVisibility ||
            CompactFpsStrip.Visibility != fpsVisibility;
        CompactAudioVisualizer.Visibility = audioVisibility;
        CompactMicBadge.Visibility = microphoneVisibility;
        CompactFpsStrip.Visibility = fpsVisibility;
        if (fps is int rate) CompactFps.Text = _localization.GetString("Notch_FpsValue", rate.ToString("N0", CultureInfo.CurrentCulture));
        if (microphone?.IsInUse == true)
        {
            var inUse = Text("Notch_MicrophoneActive");
            CompactMicrophone.Text = microphone.Application ?? inUse;
            UpdateCompactTooltip(CompactMicBadge, microphone.Application is { } app ? $"{inUse} · {app}" : inUse);
        }
        UpdateCompactHandle();
        if (widthChanged && !_expanded && _xamlRoot is not null) RefreshFrame();
    }

    /// <summary>Retains the visible badges while the battery readings and labels are unchanged.</summary>
    private bool RenderCompactBatteries()
    {
        var batteries = CompactModules.HasFlag(NotchModule.Batteries) ? _lastReadings.Batteries : null;
        var badges = NotchBatteryBadge.Create(batteries, _localization, compact: true);
        if (CompactBatteries.ItemsSource is NotchBatteryBadge[] current && current.SequenceEqual(badges)) return false;
        bool widthChanged = CompactBatteries.Items.Count != badges.Length;
        CompactBatteries.ItemsSource = badges;
        CompactBatteries.Visibility = VisibilityFor(badges.Length > 0);
        return widthChanged;
    }

    /// <summary>Keep the active tooltip attached while telemetry refreshes with the same label.</summary>
    private static void UpdateCompactTooltip(FrameworkElement badge, string label)
    {
        if (ToolTipService.GetToolTip(badge) is not string current || current != label)
            ToolTipService.SetToolTip(badge, label);
    }

    private void PublishAudioPeak(double? peak)
    {
        var wave = _expanded ? MediaWave : AudioWave;
        Array.Copy(_peaks, 1, _peaks, 0, _peaks.Length - 1);
        _peaks[^1] = peak is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        for (int i = 0; i < _peaks.Length; i++)
            ((Border)wave.Children[i]).Height = 2 + 14 * Math.Sqrt(_peaks[i]);
    }

    private void ResetAudioWave()
    {
        Array.Clear(_peaks);
        ResetAudioWave(AudioWave);
    }

    private static void ResetAudioWave(StackPanel wave)
    {
        foreach (Border bar in wave.Children) bar.Height = 2;
    }

    /// <summary>Updates the shared current conditions and fits the dedicated view to its forecast.</summary>
    private void RenderWeather()
    {
        var weather = _lastReadings.Weather;
        WeatherPlace.Text = weather?.Place ?? _content.WeatherLocation?.Label ?? Text("Notch_Weather");
        WeatherTemperature.Text = weather is null ? "" : $"{weather.Celsius:0}°C";
        WeatherSource.Visibility = VisibilityFor(weather is not null);
        RenderWeatherForecasts(weather);
        UpdateWeatherForecastVisibility();
        if (ShowsWeather && !_overviewLayout && _xamlRoot is not null && _heightTarget != ViewHeight()) StartFrameTransition();
        if (weather is null)
        {
            WeatherSymbol.Content = null;
            _weatherIcon = null;
            WeatherCondition.Text = Text(_content.WeatherLocation is null ? "Notch_WeatherChooseCity" :
                _lastReadings.WeatherAttempted ? "Notch_WeatherUnavailable" : "Notch_WeatherLoading");
            return;
        }
        var (key, kind) = WeatherConditionFor(weather.Code);
        WeatherCondition.Text = Text(key);
        if (_weatherIcon != (kind, weather.IsDay))
        {
            WeatherSymbol.Content = NotchWeatherIcon.Create(kind, weather.IsDay);
            _weatherIcon = (kind, weather.IsDay);
        }
    }

    /// <summary>Fits the forecast to the dedicated view while the Overview shows current conditions.</summary>
    private void UpdateWeatherForecastVisibility()
    {
        var weather = _lastReadings.Weather;
        WeatherHourly.Visibility = VisibilityFor(!_overviewLayout && weather is { Hours.Count: > 0 });
        WeatherWeekly.Visibility = VisibilityFor(!_overviewLayout && weather is { Days.Count: > 0 });
    }

    /// <summary>Builds forecast cells only when the weather sample or display language changes.</summary>
    private void RenderWeatherForecasts(DesktopWeatherReading? weather)
    {
        var language = _localization.CurrentLanguage;
        if (ReferenceEquals(_weatherForecast.Reading, weather) && _weatherForecast.Language == language) return;
        _weatherForecast = (weather, language);
        var culture = language is { Length: > 0 } ? CultureInfo.GetCultureInfo(language) : CultureInfo.CurrentUICulture;
        WeatherHourlyHeading.Text = Text("Notch_WeatherNextHours");
        WeatherWeeklyHeading.Text = Text("Notch_WeatherWeek");
        var hours = weather?.Hours ?? [];
        var days = weather?.Days ?? [];
        FillForecast(WeatherHours, hours, hour => ForecastCell(hour.LocalTime.ToString("t", culture), hour.Code, hour.IsDay,
            TemperatureLabel(hour.Celsius, culture)));
        FillForecast(WeatherDays, days, day => ForecastCell(day.Date.ToString("ddd", culture), day.Code, true,
            TemperatureLabel(day.HighCelsius, culture), TemperatureLabel(day.LowCelsius, culture)));
    }

    private static void FillForecast<T>(Grid grid, IReadOnlyList<T> forecast, Func<T, FrameworkElement> createCell)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        foreach (var period in forecast)
        {
            var cell = createCell(period);
            Grid.SetColumn(cell, grid.ColumnDefinitions.Count);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(cell);
        }
    }

    private StackPanel ForecastCell(string label, int? code, bool? isDay, string temperature, string? low = null)
    {
        var muted = (Brush)Root.Resources["NotchMuted"];
        var cell = new StackPanel { Spacing = 1 };
        var time = new TextBlock { Text = label, FontSize = 11, Foreground = muted, TextAlignment = TextAlignment.Center };
        var symbol = new ContentControl { Height = 22, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAccessibilityView(symbol, AccessibilityView.Raw);
        if (code is int condition)
        {
            var (key, kind) = WeatherConditionFor(condition);
            AutomationProperties.SetName(time, $"{label}, {Text(key)}");
            if (isDay is bool daylight) symbol.Content = NotchWeatherIcon.Create(kind, daylight, 22);
        }
        var temperatures = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Center };
        temperatures.Inlines.Add(new Run { Text = temperature });
        if (low is not null) temperatures.Inlines.Add(new Run { Text = $" / {low}", Foreground = muted });
        cell.Children.Add(time);
        cell.Children.Add(symbol);
        cell.Children.Add(temperatures);
        return cell;
    }

    private static string TemperatureLabel(double? celsius, CultureInfo culture) =>
        celsius is double value ? $"{value.ToString("0", culture)}°" : "—";

    private static (string Key, NotchWeatherIconKind Kind) WeatherConditionFor(int code) => code switch
    {
        0 => ("Notch_WeatherClear", NotchWeatherIconKind.Sun),
        1 or 2 => ("Notch_WeatherPartlyCloudy", NotchWeatherIconKind.Cloud),
        3 => ("Notch_WeatherCloudy", NotchWeatherIconKind.Cloud),
        45 or 48 => ("Notch_WeatherFog", NotchWeatherIconKind.Fog),
        51 or 53 or 55 or 56 or 57 => ("Notch_WeatherDrizzle", NotchWeatherIconKind.Drizzle),
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => ("Notch_WeatherRain", NotchWeatherIconKind.Rain),
        71 or 73 or 75 or 77 or 85 or 86 => ("Notch_WeatherSnow", NotchWeatherIconKind.Snow),
        95 or 96 or 99 => ("Notch_WeatherStorm", NotchWeatherIconKind.Storm),
        _ => ("Notch_WeatherUnknown", NotchWeatherIconKind.Unknown)
    };

    private static void RenderCompactPercent(ProgressBar meter, TextBlock label, double? percent)
    {
        meter.Value = percent ?? 0;
        label.Text = percent is { } value ? $"{value:0}%" : "—";
    }

    private void RenderTimer()
    {
        var seconds = (int)Math.Ceiling(_focus.Remaining.TotalSeconds);
        Remaining.Text = FormatTime(TimeSpan.FromSeconds(seconds));
        FocusProgress.Maximum = _focus.PhaseDuration.TotalSeconds;
        FocusProgress.Value = _focus.PhaseDuration.TotalSeconds - _focus.Remaining.TotalSeconds;
        TimerMinutes.Visibility = VisibilityFor(_focus.Phase == FocusPhase.Custom);
        TimerMinutes.IsEnabled = !_focus.IsRunning;
        UpdateCompactTime();
        TimerToggle.Content = Text(_focus.IsRunning ? "Notch_Pause" : "Notch_Start");
        Sessions.Text = string.Format(CultureInfo.CurrentCulture, Text("Notch_Sessions"), _focus.CompletedSessions);
    }

    private void UpdateCompactTime()
    {
        var compact = CompactModules;
        var showTimer = compact.HasFlag(NotchModule.Focus) && (_focus.IsRunning || _focus.Remaining == TimeSpan.Zero);
        var showClock = compact.HasFlag(NotchModule.Clock);
        CompactTime.Text = showTimer ? Remaining.Text : showClock ? DateTime.Now.ToString("t", CultureInfo.CurrentUICulture) : "";
        CompactTime.Visibility = VisibilityFor(showTimer || showClock);
        UpdateCompactHandle();
    }

    /// <summary>Keeps an empty compact Notch discoverable when no pinned reading is visible.</summary>
    private void UpdateCompactHandle() =>
        CompactHandle.Visibility = VisibilityFor((CompactModules & NotchReadings.SystemModules) == 0 &&
            CompactBatteries.Visibility == Visibility.Collapsed && CompactAudioVisualizer.Visibility == Visibility.Collapsed &&
            CompactTime.Visibility == Visibility.Collapsed && CompactFpsStrip.Visibility == Visibility.Collapsed);

    private async void UpdateMediaAsync()
    {
        if (_closed || !ShowsMedia || _reading || _commandPending || _mediaUnavailable) return;
        _reading = true;
        try
        {
            var state = await _media.ReadAsync();
            if (!_closed && ShowsMedia) ShowMedia(state);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.LogError("Could not read the current Windows media session.", ex);
            _mediaUnavailable = true;
            if (!_closed && ShowsMedia) ShowMedia(null);
            ScheduleUpdates();
        }
        finally { _reading = false; }
    }

    private void ShowMedia(DesktopMediaState? state)
    {
        bool wasPlaying = _mediaSnapshot?.IsPlaying == true;
        var playing = state?.IsPlaying == true;
        _mediaSnapshot = state;
        TrackTitle.Text = state?.Title is { Length: > 0 } title ? title :
            Text(state is null ? (_mediaUnavailable ? "Notch_MediaUnavailable" : "Notch_NoMedia") : "Notch_MediaTab");
        bool rejectionVisible = _commandRejected is long rejected && TimeProvider.System.GetElapsedTime(rejected).TotalSeconds < 3;
        TrackArtist.Text = rejectionVisible ? Text("Notch_MediaCommandFailed") : state?.Artist ?? "";
        RenderMediaTimeline(state);
        PreviousButton.IsEnabled = state?.CanGoPrevious == true;
        PlaybackButton.IsEnabled = state?.CanPlayPause == true;
        NextButton.IsEnabled = state?.CanGoNext == true;
        PlaybackIcon.Symbol = playing ? Symbol.Pause : Symbol.Play;
        MediaWaveFrame.Visibility = VisibilityFor(playing);
        AutomationProperties.SetName(PlaybackButton, Text(playing ? "Notch_Pause" : "Notch_Play"));
        UpdateArtwork(state?.Thumbnail);
        if (wasPlaying != playing && ShowsMedia)
        {
            Array.Clear(_peaks);
            UpdateReadings();
        }
    }

    private async void UpdateArtwork(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail == _thumbnail) return;
        _thumbnail = thumbnail;
        Artwork.Source = null;
        if (thumbnail is null) return;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (_closed || thumbnail != _thumbnail) return;
            var image = new BitmapImage { DecodePixelWidth = 128 };
            await image.SetSourceAsync(stream);
            if (!_closed && thumbnail == _thumbnail) Artwork.Source = image;
        }
        catch (Exception ex) when (ex is COMException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { _log.LogDebug($"Could not display Notch media artwork: {ex.Message}"); }
    }

    private async void OnPrevious(object sender, RoutedEventArgs args) => await SendMediaCommandAsync(DesktopMediaCommand.Previous);
    private async void OnPlayback(object sender, RoutedEventArgs args) => await SendMediaCommandAsync(DesktopMediaCommand.PlayPause);
    private async void OnNext(object sender, RoutedEventArgs args) => await SendMediaCommandAsync(DesktopMediaCommand.Next);

    private Task SendMediaCommandAsync(DesktopMediaCommand command)
    {
        CancelMediaSeek();
        return ExecuteMediaCommandAsync(() => _media.ControlAsync(command));
    }

    private async Task ExecuteMediaCommandAsync(Func<Task<bool>> command)
    {
        if (_commandPending || _reading || _closed || !ShowsMedia) return;
        _commandPending = true;
        _commandRejected = null;
        try
        {
            if (!await command()) _commandRejected = TimeProvider.System.GetTimestamp();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.LogError("Could not control the current Windows media session.", ex);
            _commandRejected = TimeProvider.System.GetTimestamp();
        }
        finally { _commandPending = false; }
        UpdateMediaAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        RefreshLabels();
        if (_mediaUnavailable) ShowMedia(null);
        else UpdateMediaAsync();
    });

    private void RefreshLabels()
    {
        LabelNavigation(OverviewTab, OverviewDot, "Notch_Overview");
        LabelNavigation(MediaTab, MediaDot, "Notch_MediaTab");
        LabelNavigation(FocusTab, FocusDot, "Notch_FocusTab");
        LabelNavigation(CalendarTab, CalendarDot, "Notch_Calendar");
        LabelNavigation(ShortcutsTab, ShortcutsDot, "Notch_Shortcuts");
        LabelNavigation(WeatherTab, WeatherDot, "Notch_Weather");
        LabelNavigation(DashboardTab, DashboardDot, "Notch_SysInfo");
        LabelNavigation(BatteriesTab, BatteriesDot, "Notch_Batteries");
        LabelNavigation(QuickSettingsTab, QuickSettingsDot, "Notch_QuickSettings");
        QuickSettingsPanel.ApplyLanguage();
        EmptyPanel.Text = Text("Notch_Empty");
        OverviewHint.Text = Text("Notch_OverviewDescription");
        if (_localization.CurrentLanguage is { Length: > 0 } language) CalendarPanel.Language = language;
        RefreshCalendarDate();
        DashboardPanel.ApplyModules(_modules, _localization);
        if (ShowsBatteries) RenderBatteries();
        Phase.ItemsSource = new[] { Text("Notch_Work"), Text("Notch_ShortBreak"), Text("Notch_LongBreak"), Text("Notch_CustomTimer") };
        Phase.SelectedIndex = (int)_focus.Phase;
        TimerReset.Content = Text("Notch_Reset");
        RenderCompactStatus();
        AutomationProperties.SetName(Grip, Text("Notch_Expand"));
        AutomationProperties.SetName(CompactAudioVisualizer, Text("Notch_AudioVisualizer"));
        AutomationProperties.SetName(CollapseButton, Text("Notch_Collapse"));
        AutomationProperties.SetName(SettingsButton, Text("Notch_OpenOptimizer"));
        AutomationProperties.SetName(Phase, Text("Notch_FocusTab"));
        AutomationProperties.SetName(TimerMinutes, Text("Notch_TimerMinutes"));
        ShortcutsHeading.Text = Text("Notch_Shortcuts");
        LabelAction(AddShortcut, "Notch_PinFile");
        LabelAction(ShareToPhone, "Notch_ShareToPhone");
        AppChoice.PlaceholderText = Text("Notch_ChooseApp");
        AutomationProperties.SetName(AppChoice, AppChoice.PlaceholderText);
        ConfirmApp.Content = Text("Notch_PinApp");
        CancelApp.Content = Text("Button_Cancel");
        RenderShortcutItems();
        RenderShortcutsStatus();
        RenderWeather();
        AutomationProperties.SetName(PreviousButton, Text("Notch_Previous"));
        AutomationProperties.SetName(TrackSeek, Text("Notch_Seek"));
        AutomationProperties.SetName(NextButton, Text("Notch_Next"));
        RenderTimer();
    }

    /// <summary>Gives both selectors for a view the same accessible name.</summary>
    private void LabelNavigation(RadioButton tab, RadioButton dot, string key)
    {
        var name = Text(key);
        AutomationProperties.SetName(tab, name);
        AutomationProperties.SetName(dot, name);
    }

    private void LabelAction(FrameworkElement element, string key)
    {
        var label = Text(key);
        AutomationProperties.SetName(element, label);
        ToolTipService.SetToolTip(element, label);
    }

    private void OnCalendarToday(object sender, RoutedEventArgs args)
    {
        CalendarPanel.DisplayMode = CalendarViewDisplayMode.Month;
        CalendarPanel.SetDisplayDate(DateTimeOffset.Now);
    }

    private void RefreshCalendarDate()
    {
        _calendarDate = DateTime.Today;
        var culture = _localization.CurrentLanguage is { Length: > 0 } language
            ? CultureInfo.GetCultureInfo(language) : CultureInfo.CurrentUICulture;
        TodayLabel.Text = Text("Notch_Today");
        TodayDay.Text = _calendarDate.ToString("%d", culture);
        TodayWeekday.Text = _calendarDate.ToString("dddd", culture);
        TodayMonth.Text = _calendarDate.ToString("MMMM yyyy", culture);
        LabelAction(TodayButton, "Notch_Today");
    }

    private static string FormatTime(TimeSpan time)
    {
        time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        return time.TotalHours >= 1 ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private string Text(string key) => _localization.GetString(key);

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        EndOverviewDrag();
        QuickSettingsPanel.Dispose();
        CloseAppPicker();
        _shortcutLifetime.Cancel();
        _shortcutLifetime.Dispose();
        EndCompactDrag(restoreOrder: false);
        _animation.Stop();
        _pageAnimation?.Stop();
        _updates.Stop();
        _seekTimer.Stop();
        _statusTimer.Stop();
        _fileShare?.Dispose();
        _ = _readings.StopAsync();
        _localization.LanguageChanged -= OnLanguageChanged;
        if (_xamlRoot is not null) _xamlRoot.Changed -= OnXamlRootChanged;
        if (_displays is not null)
        {
            _displays.Added -= OnDisplayUpdated;
            _displays.Updated -= OnDisplayUpdated;
            _displays.Removed -= OnDisplayUpdated;
            _displays.Stop();
        }
        _media.Dispose();
        WindowClosed?.Invoke(this, EventArgs.Empty);
    }
}
