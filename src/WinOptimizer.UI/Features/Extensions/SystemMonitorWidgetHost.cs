using WinOptimizer.Core.Features.Common.Models;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Extensions.ViewModels;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Owns the actual widget state, UI-thread creation and one cancellable sampling session.</summary>
public sealed class SystemMonitorWidgetHost : ISystemMonitorWidgetHost, IDisposable
{
    private readonly ISystemMonitorService _monitor;
    private readonly ILocalizationService _localization;
    private readonly IThemeService _theme;
    private readonly IDispatcherService _dispatcher;
    private readonly ILogService _log;
    private readonly Func<SystemMonitorReadingsViewModel, ISystemMonitorWindow> _createWindow;
    private ISystemMonitorWindow? _window;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private SystemMonitorOptions _options = new();
    private bool _shuttingDown;
    public SystemMonitorState State { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => State == SystemMonitorState.Running;
    public event EventHandler? StateChanged;
    public event EventHandler? DisableRequested;
    public event EventHandler? ToggleSizeRequested;
    public event EventHandler? SettingsRequested;

    public SystemMonitorWidgetHost(ISystemMonitorService monitor, ILocalizationService localization, IThemeService theme,
        IDispatcherService dispatcher, ILogService log, Func<SystemMonitorReadingsViewModel, ISystemMonitorWindow> createWindow)
    {
        _monitor = monitor;
        _localization = localization;
        _theme = theme;
        _dispatcher = dispatcher;
        _log = log;
        _createWindow = createWindow;
        _localization.LanguageChanged += OnLanguageChanged;
        _theme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>Preference I/O can finish on a worker; every window operation returns to the desktop dispatcher.</summary>
    public Task ApplyOptionsAsync(SystemMonitorOptions options)
    {
        options.Validate();
        return _dispatcher.RunOnUIThreadWithContextAsync(() =>
        { ApplyOptionsCore(options, bringToFront: false); return Task.CompletedTask; });
    }

    public Task ShowAsync() => _dispatcher.RunOnUIThreadWithContextAsync(() =>
    { ApplyOptionsCore(_options, bringToFront: true); return Task.CompletedTask; });

    private void ApplyOptionsCore(SystemMonitorOptions options, bool bringToFront)
    {
        _options = options;
        if (_shuttingDown) return;
        if (!options.Enabled) { CloseWidget(); SetState(SystemMonitorState.Disabled); return; }
        try
        {
            var window = _window;
            var creating = window is null;
            if (window is null)
            {
                SetState(SystemMonitorState.Starting);
                window = _createWindow(new(_localization));
                window.DisableRequested += (_, _) => DisableRequested?.Invoke(this, EventArgs.Empty);
                window.ToggleSizeRequested += (_, _) => ToggleSizeRequested?.Invoke(this, EventArgs.Empty);
                window.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
                window.ClosedBySystem += (_, _) => OnWindowClosed(window);
                _window = window;
            }
            window.ApplyOptions(options, _theme.GetEffectiveTheme());
            if (creating || bringToFront)
            {
                window.PlaceInitially();
                window.Show();
            }
            if (creating)
            {
                _cancellation = new();
                _worker = SampleAsync(_worker, window, _cancellation.Token);
            }
            SetState(SystemMonitorState.Running);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            CloseWidget();
            _log.LogError($"SysMonitor could not open its widget: {ex.Message}", ex);
            SetState(SystemMonitorState.Failed, ex.Message);
        }
    }

    /// <summary>Awaits the preceding cancelled session before resetting counters for a newly enabled widget.</summary>
    private async Task SampleAsync(Task preceding, ISystemMonitorWindow window, CancellationToken cancellationToken)
    {
        using var logTopic = _log.BeginTopic(LogTopic.Monitoring);
        await preceding;
        if (cancellationToken.IsCancellationRequested) return;
        _monitor.Reset();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await _monitor.ReadAsync(cancellationToken);
                    if (cancellationToken.IsCancellationRequested) return;
                    window.Publish(snapshot);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or COMException)
                { if (!cancellationToken.IsCancellationRequested) window.ShowError(ex.Message); }
                try { await Task.Delay(TimeSpan.FromSeconds(_options.RefreshIntervalSeconds), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
        finally { await _monitor.StopAsync(); }
    }

    private void OnWindowClosed(ISystemMonitorWindow window)
    {
        if (!ReferenceEquals(window, _window)) return;
        CancelSampling();
        _window = null;
        SetState(SystemMonitorState.Failed, "The SysMonitor window closed unexpectedly.");
    }

    private void CancelSampling()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void CloseWidget()
    {
        CancelSampling();
        var window = _window;
        _window = null;
        window?.CloseFromHost();
    }

    private void SetState(SystemMonitorState state, string? error = null)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops telemetry only when the application is actually terminating.</summary>
    public async Task StopAsync()
    {
        await _dispatcher.RunOnUIThreadWithContextAsync(() =>
        {
            _shuttingDown = true;
            CloseWidget();
            SetState(SystemMonitorState.Disabled);
            return Task.CompletedTask;
        });
        await _worker;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        _dispatcher.RunOnUIThread(() => _window?.RefreshLabels());
    private void OnThemeChanged(object? sender, WinOptimizerTheme e) => ApplyOptionsAsync(_options).FireAndForget(_log);
    public void Dispose()
    {
        CloseWidget();
        _localization.LanguageChanged -= OnLanguageChanged;
        _theme.ThemeChanged -= OnThemeChanged;
    }
}
