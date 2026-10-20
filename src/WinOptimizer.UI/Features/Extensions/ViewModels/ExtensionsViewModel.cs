using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Application.Preferences;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Extensions.ViewModels;

/// <summary>Owns extension preferences and publishes them to the widget only after successful persistence.</summary>
public partial class ExtensionsViewModel : ObservableObject, IDisposable
{
    private readonly IUserPreferencesService _preferences;
    private readonly ILocalizationService _localization;
    private readonly ISystemMonitorWidgetHost _widget;
    private readonly IDispatcherService _dispatcher;
    private readonly ISystemMonitorStartupService _startup;
    private bool _startupSynchronized;
    private readonly PreferenceSaveQueue<SystemMonitorOptions> _saves;
    private Task<bool>? _loading;
    private bool _applying;
    private bool _loaded;
    private bool _widgetRestoreDeferred;
    private Task _applyTask = Task.CompletedTask;
    public SystemMonitorOptions Options { get; private set; } = new();

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ShowWidgetCommand))] public partial bool IsEnabled { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool IsCompact { get; set; } = true;
    [ObservableProperty] public partial bool AlwaysOnTop { get; set; } = true;
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScaleText))] public partial double ScalePercent { get; set; } = 100;
    [ObservableProperty] public partial int RefreshIndex { get; set; } = 1;
    [ObservableProperty] public partial bool ShowTemperatures { get; set; } = true;
    [ObservableProperty] public partial bool ShowFans { get; set; } = true;
    [ObservableProperty] public partial bool ShowMemory { get; set; } = true;
    [ObservableProperty] public partial bool ShowCpu { get; set; } = true;
    [ObservableProperty] public partial bool ShowNetwork { get; set; } = true;
    [ObservableProperty] public partial bool ShowGpuUsage { get; set; }
    [ObservableProperty] public partial bool ShowGpuMemory { get; set; }
    [ObservableProperty] public partial bool ShowClockSpeed { get; set; }
    [ObservableProperty] public partial bool ShowPower { get; set; }
    [ObservableProperty] public partial bool ShowVoltage { get; set; }
    [ObservableProperty] public partial bool ShowStorageActivity { get; set; }
    [ObservableProperty] public partial bool ShowStorageThroughput { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ShowWidgetCommand))] public partial bool CanEdit { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] public partial string Status { get; private set; } = "";

    public ExtensionsViewModel(IUserPreferencesService preferences, ILocalizationService localization,
        ISystemMonitorWidgetHost widget, IDispatcherService dispatcher, ISystemMonitorStartupService startup)
    {
        _preferences = preferences;
        _localization = localization;
        _widget = widget;
        _dispatcher = dispatcher;
        _startup = startup;
        _saves = new(SaveOptionsAsync,
            options => { Options = options; _applyTask = _widget.ApplyOptionsAsync(options); },
            error => _dispatcher.RunOnUIThread(() => Status = error ?? ""), "Unable to save SysMonitor preferences.");
        _localization.LanguageChanged += OnLanguageChanged;
        _widget.DisableRequested += OnDisableRequested;
        _widget.ToggleSizeRequested += OnToggleSizeRequested;
        _widget.StateChanged += OnWidgetStateChanged;
    }

    /// <summary>Restores the saved extension state once; initial loading does not save defaults.</summary>
    public Task<bool> LoadAsync(bool automaticLaunch = false) => _loaded && (automaticLaunch || !_widgetRestoreDeferred) ? Task.FromResult(true) :
        _loading is { IsCompleted: false } ? _loading : _loading = LoadCoreAsync(automaticLaunch);

    private async Task<bool> LoadCoreAsync(bool automaticLaunch)
    {
        var loaded = false;
        await _dispatcher.RunOnUIThreadWithContextAsync(async () =>
        {
            CanEdit = false;
            try
            {
                var options = await _preferences.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, new SystemMonitorOptions());
                options.Validate();
                _applying = true;
                IsEnabled = options.Enabled;
                StartWithWindows = options.StartWithWindows;
                IsCompact = options.IsCompact;
                AlwaysOnTop = options.AlwaysOnTop;
                ThemeIndex = (int)options.Theme;
                ScalePercent = options.ScalePercent;
                RefreshIndex = options.RefreshIntervalSeconds switch { 1 => 0, 2 => 1, _ => 2 };
                ShowTemperatures = options.ShowTemperatures;
                ShowFans = options.ShowFans;
                ShowMemory = options.ShowMemory;
                ShowCpu = options.ShowCpu;
                ShowNetwork = options.ShowNetwork;
                ShowGpuUsage = options.ShowGpuUsage;
                ShowGpuMemory = options.ShowGpuMemory;
                ShowClockSpeed = options.ShowClockSpeed;
                ShowPower = options.ShowPower;
                ShowVoltage = options.ShowVoltage;
                ShowStorageActivity = options.ShowStorageActivity;
                ShowStorageThroughput = options.ShowStorageThroughput;
                Options = options;
                _widgetRestoreDeferred = automaticLaunch && !SystemMonitorLaunch.ShouldStart(options);
                if (!_widgetRestoreDeferred)
                    await (_applyTask = _widget.ApplyOptionsAsync(options));
                _loaded = loaded = true;
                var startupResult = await _startup.ApplyAsync(options);
                _startupSynchronized = startupResult.Success;
                Status = startupResult.Success ? "" : startupResult.ErrorMessage ?? "Unable to configure SysMonitor startup.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { Status = ex.Message; }
            finally { _applying = false; CanEdit = _loaded; }
        });
        return loaded;
    }

    /// <summary>Serializes startup registration with preference saves and restores registration if persistence fails.</summary>
    private async Task<OperationResult> SaveOptionsAsync(SystemMonitorOptions options)
    {
        var reconcile = !_startupSynchronized || SystemMonitorLaunch.ShouldStart(options) != SystemMonitorLaunch.ShouldStart(Options);
        if (reconcile)
        {
            var result = await _startup.ApplyAsync(options);
            if (!result.Success) { _startupSynchronized = false; return result; }
        }
        OperationResult saved;
        try { saved = await _preferences.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { saved = OperationResult.Failed(ex.Message, ex); }
        if (!saved.Success && reconcile)
        {
            var restored = await _startup.ApplyAsync(Options);
            _startupSynchronized = restored.Success;
            if (!restored.Success)
                return OperationResult.Failed($"{saved.ErrorMessage} Startup recovery failed: {restored.ErrorMessage}");
        }
        else if (saved.Success) _startupSynchronized = true;
        return saved;
    }

    /// <summary>Queues one complete snapshot so rapid edits preserve their order.</summary>
    private void Save()
    {
        if (!_loaded || _applying) return;
        var options = new SystemMonitorOptions
        {
            Enabled = IsEnabled, StartWithWindows = StartWithWindows, IsCompact = IsCompact, AlwaysOnTop = AlwaysOnTop,
            Theme = (SystemMonitorTheme)ThemeIndex,
            ScalePercent = ScalePercent,
            RefreshIntervalSeconds = RefreshIndex switch { 0 => 1, 1 => 2, 2 => 5, _ => 0 },
            ShowTemperatures = ShowTemperatures, ShowFans = ShowFans, ShowMemory = ShowMemory,
            ShowCpu = ShowCpu, ShowNetwork = ShowNetwork,
            ShowGpuUsage = ShowGpuUsage,
            ShowGpuMemory = ShowGpuMemory,
            ShowClockSpeed = ShowClockSpeed,
            ShowPower = ShowPower,
            ShowVoltage = ShowVoltage,
            ShowStorageActivity = ShowStorageActivity,
            ShowStorageThroughput = ShowStorageThroughput,
        };
        try { options.Validate(); _saves.Enqueue(options); }
        catch (ArgumentException ex) { _saves.Reject(ex.Message); }
    }

    partial void OnIsEnabledChanged(bool value) => Save();
    partial void OnStartWithWindowsChanged(bool value) => Save();
    partial void OnIsCompactChanged(bool value) => Save();
    partial void OnAlwaysOnTopChanged(bool value) => Save();
    partial void OnThemeIndexChanged(int value) => Save();
    partial void OnScalePercentChanged(double value) => Save();
    partial void OnRefreshIndexChanged(int value) => Save();
    partial void OnShowTemperaturesChanged(bool value) => Save();
    partial void OnShowFansChanged(bool value) => Save();
    partial void OnShowMemoryChanged(bool value) => Save();
    partial void OnShowCpuChanged(bool value) => Save();
    partial void OnShowNetworkChanged(bool value) => Save();
    partial void OnShowGpuUsageChanged(bool value) => Save();
    partial void OnShowGpuMemoryChanged(bool value) => Save();
    partial void OnShowClockSpeedChanged(bool value) => Save();
    partial void OnShowPowerChanged(bool value) => Save();
    partial void OnShowVoltageChanged(bool value) => Save();
    partial void OnShowStorageActivityChanged(bool value) => Save();
    partial void OnShowStorageThroughputChanged(bool value) => Save();
    private void OnDisableRequested(object? sender, EventArgs e) => IsEnabled = false;
    private void OnToggleSizeRequested(object? sender, EventArgs e) => IsCompact = !IsCompact;
    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
    private void OnWidgetStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(WidgetStateText));
        OnPropertyChanged(nameof(RuntimeError));
        OnPropertyChanged(nameof(HasRuntimeError));
    }

    /// <summary>Waits for both persistence and the actual desktop transition, including queued rapid edits.</summary>
    public async Task<bool> FlushAsync()
    {
        var saved = await _saves.FlushAsync();
        await _applyTask;
        return saved;
    }

    private bool CanShowWidget() => CanEdit && IsEnabled;

    /// <summary>Recovers a hidden or failed widget through its existing host without rewriting preferences.</summary>
    [RelayCommand(CanExecute = nameof(CanShowWidget))]
    private async Task ShowWidgetAsync()
    {
        if (await FlushAsync() && Options.Enabled) await _widget.ShowAsync();
    }

    public string WidgetStateText => Text(_widget.State switch
    {
        SystemMonitorState.Starting => "SysMonitor_Starting",
        SystemMonitorState.Running => "SysMonitor_Running",
        SystemMonitorState.Failed => "SysMonitor_Failed",
        _ => "SysMonitor_Disabled"
    });
    public string RuntimeError => _widget.LastError ?? "";
    public bool HasRuntimeError => !string.IsNullOrWhiteSpace(RuntimeError);
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public string ShowWidgetLabel => Text("SysMonitor_ShowWidget");

    public string MonitorDescription => Text("SysMonitor_Description");
    public string ConfigureLabel => Text("SysMonitor_Configure");
    public string EnableLabel => Text("SysMonitor_Enable");
    public string StartupLabel => Text("SysMonitor_StartWithWindows");
    public string StartupDescription => Text("SysMonitor_StartupDescription");
    public string SettingsLabel => Text("SysMonitor_Settings");
    public string CompactLabel => Text("SysMonitor_Compact");
    public string CompactDescription => Text("SysMonitor_CompactDescription");
    public string TopLabel => Text("SysMonitor_AlwaysOnTop");
    public string TopDescription => Text("SysMonitor_TopDescription");
    public string ThemeLabel => Text("SysMonitor_Theme");
    public string ScaleLabel => Text("SysMonitor_Scale");
    public string ScaleDescription => Text("SysMonitor_ScaleDescription");
    public string ScaleText => $"{ScalePercent:0}%";
    public string SystemThemeLabel => Text("SysMonitor_SystemTheme");
    public string LightLabel => Text("SysMonitor_Light");
    public string DarkLabel => Text("SysMonitor_Dark");
    public string RefreshLabel => Text("SysMonitor_Refresh");
    public string SensorsDescription => Text("SysMonitor_SensorsDescription");
    public string BackgroundDescription => Text("SysMonitor_BackgroundDescription");
    public string BackgroundTitle => Text("SysMonitor_BackgroundTitle");
    public string SensorsTitle => Text("SysMonitor_SensorsTitle");
    public string ExpandedContentLabel => Text("SysMonitor_ExpandedContent");
    public string ExpandedContentDescription => Text("SysMonitor_ExpandedContentDescription");
    public string TemperaturesLabel => Text("SysMonitor_Temperatures");
    public string FansLabel => Text("SysMonitor_Fans");
    public string MemoryLabel => Text("SysMonitor_Memory");
    public string CpuLabel => Text("SysMonitor_CpuHistory");
    public string NetworkLabel => Text("SysMonitor_Network");
    public string GpuUsageLabel => Text("SysMonitor_GpuUsage");
    public string GpuMemoryLabel => Text("SysMonitor_GpuMemory");
    public string ClockSpeedLabel => Text("SysMonitor_ClockSpeed");
    public string PowerLabel => Text("SysMonitor_Power");
    public string VoltageLabel => Text("SysMonitor_Voltage");
    public string StorageActivityLabel => Text("SysMonitor_StorageActivity");
    public string StorageThroughputLabel => Text("SysMonitor_StorageThroughput");
    public string SensorSetupLabel => Text("SysMonitor_SensorSetup");
    private string Text(string key) => _localization.GetString(key);

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _widget.DisableRequested -= OnDisableRequested;
        _widget.ToggleSizeRequested -= OnToggleSizeRequested;
        _widget.StateChanged -= OnWidgetStateChanged;
    }
}
