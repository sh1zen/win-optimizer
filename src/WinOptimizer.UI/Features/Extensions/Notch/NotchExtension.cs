using System.ComponentModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

internal interface INotchWindow
{
    event EventHandler? WindowClosed;
    event EventHandler? OpenOptimizerRequested;
    void ApplyModules(NotchModule modules);
    void ApplyContent(NotchContent content);
    void ApplyPlacement(NotchPlacement placement);
    Task StopReadingsAsync();
    void Show();
    void Close();
}

/// <summary>Restores the desktop notch and keeps its single window in sync with saved preferences.</summary>
public sealed partial class NotchExtension : ObservableObject, IDisposable
{
    internal const string PreferenceKey = "Extensions.Notch.Enabled";
    internal const string StartupPreferenceKey = "Extensions.Notch.StartWithWindows";
    internal const string ModulesPreferenceKey = "Extensions.Notch.Modules";
    internal const string SysInfoPreferenceKey = "Extensions.Notch.SysInfo.Enabled";
    internal const string PinnedModulesPreferenceKey = "Extensions.Notch.PinnedModules";
    internal const string CompactOrderPreferenceKey = "Extensions.Notch.CompactOrder";
    internal const string ContentPreferenceKey = "Extensions.Notch.Content";
    internal const string PlacementPreferenceKey = "Extensions.Notch.Placement";
    internal const NotchModule DefaultModules = NotchModule.Media | NotchModule.Focus | NotchModule.Calendar | NotchModule.Clock;
    private const NotchModule DefaultPinnedModules = NotchModule.Clock;
    internal const NotchModule PinnableModules = NotchReadings.SystemModules | NotchModule.Batteries |
        NotchModule.AudioVisualizer | NotchModule.Focus | NotchModule.Clock | NotchModule.FrameRate;
    internal const NotchModule DashboardModules = NotchReadings.SystemModules | NotchModule.Uptime;
    private const NotchModule AllModules = DefaultModules | DashboardModules | NotchModule.Batteries |
        NotchModule.Shortcuts | NotchModule.Weather | NotchModule.AudioVisualizer |
        NotchModule.StatusAlerts | NotchModule.FrameRate | NotchModule.QuickSettings;
    private readonly IUserPreferencesService _preferences;
    private readonly ILocalizationService _localization;
    private readonly ILogService _log;
    private readonly INotchStartupService _startup;
    private readonly Func<NotchExtension, INotchWindow>? _createWindow;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<DesktopWeatherLocation>>>? _searchWeatherPlaces;
    private INotchWindow? _window;
    private Task _change = Task.CompletedTask;
    private Task _windowStop = Task.CompletedTask;
    private bool _loaded;
    private bool _disposed;
    private bool _hasSavedSysInfo;
    private bool _manualRestoreRequested;
    private NotchContent _content = NotchContent.Empty;

    [ObservableProperty] public partial bool IsEnabled { get; private set; }
    [ObservableProperty] public partial bool StartWithWindows { get; private set; }
    [ObservableProperty] public partial bool IsSysInfoEnabled { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEdit))] public partial bool IsBusy { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; private set; } = "";
    public bool CanEdit => _loaded && !IsBusy;
    public bool HasError => Error.Length > 0;
    public bool IsOverviewEnabled => _content.OverviewEnabled == true;
    public bool IsRunning => _window is not null;
    internal bool ShouldStartWithWindows => IsEnabled && StartWithWindows;
    public NotchModule Modules { get; private set; } = DefaultModules;
    private NotchModule ActiveModules => IsSysInfoEnabled ? Modules : Modules & ~DashboardModules;
    internal NotchModule PinnedModules { get; private set; } = DefaultPinnedModules;
    internal IReadOnlyList<NotchModule> CompactOrder { get; private set; } = NotchCompactOrder.Normalize([]);
    internal NotchPlacement Placement { get; private set; } = NotchPlacement.Default;
    public IReadOnlyList<NotchModuleOption> ModuleOptions { get; }
    public IReadOnlyList<NotchModuleOption> GeneralModuleOptions { get; }
    public IReadOnlyList<NotchModuleOption> SysInfoOptions { get; }
    public IReadOnlyList<NotchShortcut> Shortcuts => _content.Shortcuts;
    internal DesktopWeatherLocation? WeatherLocation => _content.WeatherLocation;
    public string Description => _localization.GetString("Notch_Description");
    public string EnableLabel => _localization.GetString("Notch_Enable");
    public string StartupLabel => Text("Notch_StartWithWindows");
    public string StartupDescription => Text("Notch_StartupDescription");
    public string ModulesLabel => _localization.GetString("Notch_Modules");
    public string ModulesDescription => _localization.GetString("Notch_ModulesDescription");
    public string SysInfoLabel => Text("Notch_SysInfo");
    public string SysInfoDescription => Text("Notch_SysInfoDescription");
    public string OverviewLabel => Text("Notch_Overview");
    public string OverviewDescription => Text("Notch_OverviewDescription");
    public string ModuleCountText => _localization.GetString("Notch_ModuleCount",
        GeneralModuleOptions.Count(option => option.IsEnabled) + (IsSysInfoEnabled ? 1 : 0) + (IsOverviewEnabled ? 1 : 0),
        GeneralModuleOptions.Count + 2);
    public event EventHandler? StateChanged;
    public event EventHandler? OpenOptimizerRequested;

    public NotchExtension(IUserPreferencesService preferences, ILocalizationService localization, ILogService log,
        INotchStartupService startup, IConnectionTrafficService traffic, HttpClient? httpClient = null)
        : this(preferences, localization, log, startup, extension => new NotchWindow(extension, localization, log, new DesktopMediaSession(),
            weather: httpClient is null ? null : new DesktopWeather(httpClient), traffic: traffic),
            httpClient is null ? null : new DesktopPlaceSearch(httpClient).SearchAsync) { }

    /// <summary>Creates the shell's settings owner without an in-process desktop window.</summary>
    internal static NotchExtension CreateForShell(IUserPreferencesService preferences, ILocalizationService localization,
        ILogService log, INotchStartupService startup, HttpClient? httpClient) => new(preferences, localization, log, startup,
            createWindow: null, httpClient is null ? null : new DesktopPlaceSearch(httpClient).SearchAsync);

    internal NotchExtension(IUserPreferencesService preferences, ILocalizationService localization, ILogService log, INotchStartupService startup,
        Func<NotchExtension, INotchWindow>? createWindow,
        Func<string, CancellationToken, Task<IReadOnlyList<DesktopWeatherLocation>>>? searchWeatherPlaces = null)
    {
        _preferences = preferences;
        _localization = localization;
        _log = log;
        _startup = startup;
        _createWindow = createWindow;
        _searchWeatherPlaces = searchWeatherPlaces;
        ModuleOptions = Enum.GetValues<NotchModule>().Where(module => module != NotchModule.None)
            .Select(module => new NotchModuleOption(this, localization, module)).ToArray();
        GeneralModuleOptions = ModuleOptions.Where(option => (option.Module & DashboardModules) == 0).ToArray();
        SysInfoOptions = ModuleOptions.Where(option => (option.Module & DashboardModules) != 0).ToArray();
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Restores a saved notch when the shell is opened or when the user has opted in to sign-in launch.</summary>
    public Task LoadAsync(bool automaticLaunch = false) => LoadCoreAsync(automaticLaunch, refresh: false);

    /// <summary>Reconciles committed edits from the other process without rewriting the sign-in task.</summary>
    internal async Task RefreshAsync(bool automaticLaunch)
    {
        if (!_loaded || _disposed) return;
        await FlushAsync();
        await LoadCoreAsync(automaticLaunch, refresh: true);
    }

    private Task LoadCoreAsync(bool automaticLaunch, bool refresh)
    {
        if (!automaticLaunch) _manualRestoreRequested = true;
        if (IsBusy || _disposed || (!refresh && _loaded &&
            (_createWindow is null || !IsEnabled || IsRunning || (automaticLaunch && !StartWithWindows))))
            return _change;
        return _change = RunAsync(async () =>
        {
            var snapshot = await _preferences.ReadSnapshotAsync();
            if (_disposed) return;
            IsEnabled = snapshot.Get(PreferenceKey, false);
            StartWithWindows = snapshot.Get(StartupPreferenceKey, false);
            Modules = (NotchModule)snapshot.Get(ModulesPreferenceKey, (int)DefaultModules) & AllModules;
            var savedSysInfo = snapshot.Get<bool?>(SysInfoPreferenceKey, null);
            _hasSavedSysInfo = savedSysInfo.HasValue;
            IsSysInfoEnabled = savedSysInfo ?? ((Modules & DashboardModules) != 0);
            PinnedModules = (NotchModule)snapshot.Get(PinnedModulesPreferenceKey, (int)DefaultPinnedModules) & PinnableModules;
            CompactOrder = NotchCompactOrder.Normalize(snapshot.Get<NotchModule[]>(CompactOrderPreferenceKey, []));
            Placement = snapshot.Get(PlacementPreferenceKey, NotchPlacement.Default).Normalize();
            var content = snapshot.Get(ContentPreferenceKey, NotchContent.Empty).Normalize();
            PublishContent(content with { OverviewEnabled = content.OverviewEnabled ?? Modules.HasFlag(NotchModule.Calendar | NotchModule.Weather) });
            _loaded = true;
            if (!refresh && ShouldStartWithWindows)
            {
                var registration = await _startup.ApplyAsync(true);
                if (!registration.Success) Error = registration.ErrorMessage ?? Text("Notch_UpdateError");
            }
            if (IsEnabled && (_manualRestoreRequested || StartWithWindows))
            {
                await _windowStop;
                if (_disposed) return;
                if (_window is { } window)
                {
                    window.ApplyModules(ActiveModules);
                    window.ApplyPlacement(Placement);
                }
                else if (_createWindow is not null) ShowWindow();
                else if (!refresh) DesktopExtensionProcess.LaunchWidgets();
            }
            else if (refresh) CloseWindow();
        });
    }

    /// <summary>Controls the dashboard readings as a group while retaining their individual choices and pins.</summary>
    internal Task SetSysInfoEnabledAsync(bool enabled)
    {
        if (_disposed || !CanEdit || IsSysInfoEnabled == enabled) return _change;
        var next = enabled && !_hasSavedSysInfo && (Modules & DashboardModules) == 0 ? Modules | DashboardModules : Modules;
        return _change = RunAsync(async () =>
        {
            var result = await SaveModulesAsync(next, enabled);
            if (!result.Success)
            {
                Error = result.ErrorMessage ?? Text("Notch_UpdateError");
                return;
            }
            Modules = next;
            IsSysInfoEnabled = enabled;
            _window?.ApplyModules(ActiveModules);
        });
    }

    /// <summary>Applies a saved module selection immediately to a running notch.</summary>
    public Task SetModuleAsync(NotchModule module, bool enabled)
    {
        if (_disposed || !CanEdit || !Enum.IsDefined(module) || module == NotchModule.None) return _change;
        var next = enabled ? Modules | module : Modules & ~module;
        if (next == Modules) return _change;
        return _change = RunAsync(async () =>
        {
            bool? sysInfo = !_hasSavedSysInfo && (module & DashboardModules) != 0 ? IsSysInfoEnabled : null;
            var result = await SaveModulesAsync(next, sysInfo);
            if (!result.Success)
            {
                Error = result.ErrorMessage ?? _localization.GetString("Notch_UpdateError");
                return;
            }
            Modules = next;
            _window?.ApplyModules(ActiveModules);
        });
    }

    /// <summary>Confirms a compact pin without changing the enabled modules.</summary>
    internal Task SetPinnedAsync(NotchModule module, bool pinned)
    {
        if (_disposed || !CanEdit || module == NotchModule.None || !Enum.IsDefined(module) ||
            !PinnableModules.HasFlag(module) || !Modules.HasFlag(module)) return _change;
        var next = pinned ? PinnedModules | module : PinnedModules & ~module;
        if (next == PinnedModules) return _change;
        return _change = RunAsync(async () =>
        {
            var result = await _preferences.SetPreferenceAsync(PinnedModulesPreferenceKey, (int)next);
            if (!result.Success)
            {
                Error = result.ErrorMessage ?? Text("Notch_UpdateError");
                return;
            }
            PinnedModules = next;
            _window?.ApplyModules(ActiveModules);
        });
    }

    internal string Text(string key) => _localization.GetString(key);
    internal string DisplayLabel(int number, int width, int height) =>
        _localization.GetString("Notch_DisplayChoice", number, width, height);

    /// <summary>Applies the placement to the existing window after the preference is saved.</summary>
    internal Task SetPlacementAsync(NotchPlacement placement)
    {
        if (_disposed || !CanEdit) return _change;
        var next = placement.Normalize();
        if (next == Placement) return _change;
        return _change = RunAsync(async () =>
        {
            var result = await _preferences.SetPreferenceAsync(PlacementPreferenceKey, next);
            if (!result.Success)
            {
                Error = result.ErrorMessage ?? Text("Notch_UpdateError");
                return;
            }
            Placement = next;
            OnPropertyChanged(nameof(Placement));
            _window?.ApplyPlacement(next);
        });
    }

    /// <summary>Saves the reordered compact readouts without changing which modules are pinned.</summary>
    internal Task MoveCompactSlotAsync(NotchModule source, NotchModule target)
    {
        var active = ActiveModules & PinnedModules;
        if (_disposed || !CanEdit || !NotchCompactOrder.IsPinned(source, active) ||
            !NotchCompactOrder.IsPinned(target, active) ||
            NotchCompactOrder.Move(CompactOrder, source, target) is not { } next) return _change;
        return _change = RunAsync(async () =>
        {
            var result = await _preferences.SetPreferenceAsync(CompactOrderPreferenceKey, next);
            if (!result.Success)
            {
                Error = result.ErrorMessage ?? Text("Notch_UpdateError");
                return;
            }
            CompactOrder = next;
            _window?.ApplyModules(ActiveModules);
        });
    }

    /// <summary>Saves module selections together with the Sys info switch when its value is set.</summary>
    private async Task<OperationResult> SaveModulesAsync(NotchModule modules, bool? sysInfo)
    {
        if (sysInfo is not bool enabled) return await _preferences.SetPreferenceAsync(ModulesPreferenceKey, (int)modules);
        var result = await _preferences.UpdatePreferencesAsync(new Dictionary<string, object>
        {
            [ModulesPreferenceKey] = (int)modules,
            [SysInfoPreferenceKey] = enabled
        });
        if (result.Success) _hasSavedSysInfo = true;
        return result;
    }

    internal Task<IReadOnlyList<DesktopWeatherLocation>> SearchWeatherPlacesAsync(string query, CancellationToken token) =>
        (_searchWeatherPlaces ?? throw new InvalidOperationException("Photon is unavailable."))(query, token);

    internal Task SetWeatherLocationAsync(DesktopWeatherLocation? location)
    {
        if (_disposed || !CanEdit) return _change;
        if (location is { IsValid: false })
        {
            Error = Text("Notch_WeatherSelectLocation");
            return Task.CompletedTask;
        }
        return ChangeContentAsync(() => _content with { WeatherLocation = location });
    }

    /// <summary>Saves Overview visibility along with the layout it will use on its next visit.</summary>
    internal Task SetOverviewEnabledAsync(bool enabled) => ChangeContentAsync(() => _content with { OverviewEnabled = enabled });

    /// <summary>Commits a tile only after its view and slot are still available.</summary>
    internal Task PlaceOverviewTileAsync(NotchView view, int slot) => ChangeContentAsync(() =>
        IsOverviewEnabled && HasView(view, ActiveModules) && NotchOverviewLayout.Place(_content.OverviewTiles, view, slot) is { } tiles
            ? _content with { OverviewTiles = tiles } : _content);

    internal static bool HasView(NotchView view, NotchModule modules) => view switch
    {
        NotchView.Media => modules.HasFlag(NotchModule.Media),
        NotchView.Focus => modules.HasFlag(NotchModule.Focus),
        NotchView.Calendar => modules.HasFlag(NotchModule.Calendar),
        NotchView.Shortcuts => modules.HasFlag(NotchModule.Shortcuts),
        NotchView.Weather => modules.HasFlag(NotchModule.Weather),
        NotchView.Dashboard => (modules & DashboardModules) != 0,
        NotchView.Batteries => modules.HasFlag(NotchModule.Batteries),
        NotchView.QuickSettings => modules.HasFlag(NotchModule.QuickSettings),
        _ => false
    };

    internal Task AddShortcutAsync(string path) => PinShortcutAsync(path, null);

    internal Task AddApplicationAsync(DesktopApplication app) => PinShortcutAsync(app.Target, app.Name);

    /// <summary>Saves a single app or file pin using the shared Quick access limit and order.</summary>
    private Task PinShortcutAsync(string path, string? appName)
    {
        if (_disposed || !CanEdit) return _change;
        if (appName is null && !NotchContent.IsLocalPath(path)) return ReportShortcutError("Notch_ShortcutLocal");
        if (appName is not null && (string.IsNullOrWhiteSpace(appName) || !WindowsDesktopApplications.IsTarget(path)))
            return ReportShortcutError("Notch_UpdateError");
        if (_content.Shortcuts.Length == NotchContent.MaximumShortcuts)
            return ReportShortcutError("Notch_ShortcutLimit");
        return ChangeContentAsync(() =>
        {
            var normalized = appName is null ? Path.GetFullPath(path) : path;
            return _content.Shortcuts.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.Path, normalized))
                ? _content : _content with { Shortcuts = [.. _content.Shortcuts, new NotchShortcut(normalized, IsFolder: false) { AppName = appName }] };
        });
    }

    internal Task RemoveShortcutAsync(NotchShortcut shortcut) =>
        ChangeContentAsync(() => _content with { Shortcuts = _content.Shortcuts.Where(item => item != shortcut).ToArray() });

    private Task ReportShortcutError(string key)
    {
        Error = Text(key);
        return Task.CompletedTask;
    }

    private Task ChangeContentAsync(Func<NotchContent> update)
    {
        if (_disposed || !CanEdit) return _change;
        return _change = RunAsync(async () =>
        {
            var next = update();
            if (next == _content) return;
            var result = await _preferences.SetPreferenceAsync(ContentPreferenceKey, next);
            if (!result.Success)
            {
                Error = result.ErrorMessage ?? Text("Notch_UpdateError");
                return;
            }
            PublishContent(next);
        });
    }

    private void PublishContent(NotchContent content)
    {
        _content = content;
        OnPropertyChanged(nameof(Shortcuts));
        OnPropertyChanged(nameof(IsOverviewEnabled));
        _window?.ApplyContent(content);
    }

    /// <summary>Confirms activation only after both window creation and preference saving succeed.</summary>
    public Task SetEnabledAsync(bool enabled) => _disposed || !CanEdit || enabled == IsEnabled
        ? _change : _change = RunAsync(async () =>
        {
            if (enabled && _createWindow is not null)
            {
                await _windowStop;
                if (_disposed) return;
                ShowWindow();
            }
            try
            {
                if (await SaveActivationPreferenceAsync(PreferenceKey, enabled, enabled && StartWithWindows))
                {
                    IsEnabled = enabled;
                    if (enabled && _createWindow is null) DesktopExtensionProcess.LaunchWidgets();
                }
            }
            finally
            {
                if (!IsEnabled) CloseWindow();
            }
        });

    /// <summary>Changes the sign-in choice independently of the current desktop window.</summary>
    public Task SetStartupAsync(bool enabled) => _disposed || !CanEdit || enabled == StartWithWindows
        ? _change : _change = RunAsync(async () =>
        {
            if (await SaveActivationPreferenceAsync(StartupPreferenceKey, enabled, IsEnabled && enabled))
                StartWithWindows = enabled;
        });

    /// <summary>Registers the prospective launch and restores the previous registration if saving fails.</summary>
    private async Task<bool> SaveActivationPreferenceAsync(string key, bool value, bool startsWithWindows)
    {
        var previousStartup = ShouldStartWithWindows;
        if (startsWithWindows != previousStartup)
        {
            var registration = await _startup.ApplyAsync(startsWithWindows);
            if (!registration.Success)
            {
                Error = registration.ErrorMessage ?? Text("Notch_UpdateError");
                return false;
            }
        }

        var saved = false;
        try
        {
            var result = await _preferences.SetPreferenceAsync(key, value);
            saved = result.Success;
            if (!saved) Error = result.ErrorMessage ?? Text("Notch_UpdateError");
        }
        finally
        {
            if (!saved && startsWithWindows != previousStartup)
            {
                var restored = await _startup.ApplyAsync(previousStartup);
                if (!restored.Success)
                    Error = Text("Notch_StartupRecoveryError");
            }
        }
        return saved;
    }

    public async Task FlushAsync()
    {
        await _change;
        await _windowStop;
    }

    private async Task RunAsync(Func<Task> change)
    {
        IsBusy = true;
        Error = "";
        try { await change(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or COMException or Win32Exception)
        {
            _log.LogError("Could not apply the Notch extension state.", ex);
            Error = _localization.GetString("Notch_UpdateError");
        }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (!value) RefreshModuleOptions();
        else foreach (var option in ModuleOptions) option.RefreshEditability();
    }

    private void RefreshModuleOptions()
    {
        foreach (var option in ModuleOptions) option.Refresh();
        OnPropertyChanged(nameof(ModuleCountText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
        RefreshModuleOptions();
    }

    private void ShowWindow()
    {
        var window = (_createWindow ?? throw new InvalidOperationException("The Notch window belongs to its companion."))(this);
        _window = window;
        window.WindowClosed += OnWindowClosed;
        window.OpenOptimizerRequested += OnOpenOptimizerRequested;
        try
        {
            window.ApplyModules(ActiveModules);
            window.ApplyPlacement(Placement);
            window.ApplyContent(_content);
            window.Show();
            if (_window != window) throw new InvalidOperationException("The Notch window closed during startup.");
        }
        catch { CloseWindow(); throw; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenOptimizerRequested(object? sender, EventArgs e) => OpenOptimizerRequested?.Invoke(this, e);

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Error = _localization.GetString("Notch_UpdateError");
        ReleaseWindow(close: false);
    }

    private void CloseWindow() => ReleaseWindow(close: true);

    private void ReleaseWindow(bool close)
    {
        if (_window is not { } window) return;
        _window = null;
        window.WindowClosed -= OnWindowClosed;
        window.OpenOptimizerRequested -= OnOpenOptimizerRequested;
        _windowStop = window.StopReadingsAsync();
        try { if (close) window.Close(); }
        finally { StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void Dispose()
    {
        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;
        CloseWindow();
    }
}
