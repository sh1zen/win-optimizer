using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.ViewModels;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Owns widget startup and drains the companion when its last window stops.</summary>
public sealed class ExtensionApplicationLifetime : IDisposable
{
    private readonly ISystemMonitorWidgetHost _widget;
    private readonly ExtensionsViewModel _preferences;
    private readonly NotchExtension _notch;
    private readonly Action _reportFailure;
    private readonly Action _shutdown;
    private readonly ILogService _log;
    private bool _started;
    private bool _refreshing;
    private bool _exiting;
    private Task? _exitTask;

    public ExtensionApplicationLifetime(ISystemMonitorWidgetHost widget, ExtensionsViewModel preferences, NotchExtension notch,
        Action reportFailure, Action shutdown, ILogService log)
    {
        _widget = widget;
        _preferences = preferences;
        _notch = notch;
        _reportFailure = reportFailure;
        _shutdown = shutdown;
        _log = log;
        _widget.StateChanged += OnStateChanged;
        _notch.StateChanged += OnStateChanged;
    }

    /// <summary>Starts the eligible widgets and surfaces any launch failure.</summary>
    public async Task StartInBackgroundAsync(bool automaticLaunch = true)
    {
        var monitorLoad = _preferences.LoadAsync(automaticLaunch);
        await Task.WhenAll(monitorLoad, _notch.LoadAsync(automaticLaunch));
        if (_exiting) return;
        _started = true;
        var monitorRequested = automaticLaunch ? SystemMonitorLaunch.ShouldStart(_preferences.Options) : _preferences.Options.Enabled;
        var notchRequested = automaticLaunch ? _notch.ShouldStartWithWindows : _notch.IsEnabled;
        if (!await monitorLoad || _notch.HasError ||
            (monitorRequested && (!_widget.IsRunning || _preferences.HasStatus)) ||
            (notchRequested && !_notch.IsRunning))
            _reportFailure();
        if (!_widget.IsRunning && !_notch.IsRunning) await CloseAsync();
    }

    /// <summary>Stops the desktop widgets and exits after any pending changes finish.</summary>
    public Task CloseAsync()
    {
        _exiting = true;
        return _exitTask ??= ExitCoreAsync();
    }

    /// <summary>Applies a settings change to both widgets before checking whether the companion can exit.</summary>
    public async Task RefreshAsync(bool automaticLaunch)
    {
        if (_exiting) return;
        _refreshing = true;
        try
        {
            await Task.WhenAll(_preferences.RefreshAsync(automaticLaunch, applyToWidget: true), _notch.RefreshAsync(automaticLaunch));
        }
        finally { _refreshing = false; }
        EvaluateState();
    }

    private void OnStateChanged(object? sender, EventArgs e) => EvaluateState();

    private void EvaluateState()
    {
        if (!_started || _exiting || _refreshing) return;
        if (_widget.State == SystemMonitorState.Failed || _notch.HasError)
            _reportFailure();
        if (_widget.State is SystemMonitorState.Disabled or SystemMonitorState.Failed && !_notch.IsRunning)
            CloseAsync().FireAndForget(_log);
    }

    private async Task ExitCoreAsync()
    {
        try
        {
            await Task.WhenAll(_preferences.FlushAsync(), _notch.FlushAsync());
            _notch.Dispose();
            await Task.WhenAll(_widget.StopAsync(), _notch.FlushAsync());
        }
        finally { Dispose(); _shutdown(); }
    }

    public void Dispose()
    {
        _widget.StateChanged -= OnStateChanged;
        _notch.StateChanged -= OnStateChanged;
    }
}
