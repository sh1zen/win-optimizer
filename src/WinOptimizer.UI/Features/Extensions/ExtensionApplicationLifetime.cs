using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Extensions.ViewModels;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Retains the desktop session while an extension runs and releases it after the last widget is disabled.</summary>
public sealed class ExtensionApplicationLifetime : IDisposable
{
    private readonly ISystemMonitorWidgetHost _widget;
    private readonly ExtensionsViewModel _preferences;
    private readonly Action _hideMainWindow;
    private readonly Action _showMainWindow;
    private readonly Action _shutdown;
    private readonly ILogService _log;
    private bool _mainWindowHidden;
    private bool _mainWindowRequested;
    private bool _exiting;
    private Task? _exitTask;

    public ExtensionApplicationLifetime(ISystemMonitorWidgetHost widget, ExtensionsViewModel preferences,
        Action hideMainWindow, Action showMainWindow, Action shutdown, ILogService log)
    {
        _widget = widget;
        _preferences = preferences;
        _hideMainWindow = hideMainWindow;
        _showMainWindow = showMainWindow;
        _shutdown = shutdown;
        _log = log;
        _widget.StateChanged += OnWidgetStateChanged;
    }

    /// <summary>Starts the saved widget without showing the shell; stale registrations exit and failures remain visible.</summary>
    public async Task StartInBackgroundAsync()
    {
        var loaded = await _preferences.LoadAsync(automaticLaunch: true);
        if (_mainWindowRequested) return;
        if (!loaded) { ShowMainWindow(); return; }
        if (!WinOptimizer.Core.Features.Extensions.SystemMonitorLaunch.ShouldStart(_preferences.Options))
        { await ExitAsync(); return; }
        if (!_widget.IsRunning || _preferences.HasStatus) { ShowMainWindow(); return; }
        _mainWindowHidden = true;
        _hideMainWindow();
    }

    /// <summary>Closing the main window keeps an actually running widget; a failed start cannot leave an invisible process.</summary>
    public async Task CloseAsync()
    {
        if (_exitTask is { } exit) { await exit; return; }
        await _preferences.FlushAsync();
        if (_widget.IsRunning)
        {
            _mainWindowHidden = true;
            _hideMainWindow();
        }
        else await ExitAsync();
    }

    /// <summary>Reopens the same shell from the widget's gear or redirected single-instance activation.</summary>
    public void ShowMainWindow()
    {
        if (_exiting) return;
        _mainWindowRequested = true;
        _mainWindowHidden = false;
        _showMainWindow();
    }

    private void OnWidgetStateChanged(object? sender, EventArgs e)
    {
        if (!_mainWindowHidden || _exiting) return;
        if (_widget.State == SystemMonitorState.Disabled) ExitAsync().FireAndForget(_log);
        else if (_widget.State == SystemMonitorState.Failed) ShowMainWindow();
    }

    private Task ExitAsync()
    {
        _exiting = true;
        return _exitTask ??= ExitCoreAsync();
    }

    private async Task ExitCoreAsync()
    {
        try
        {
            await _preferences.FlushAsync();
            await _widget.StopAsync();
        }
        finally { Dispose(); _shutdown(); }
    }

    public void Dispose() => _widget.StateChanged -= OnWidgetStateChanged;
}
