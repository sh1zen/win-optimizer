using Microsoft.UI.Dispatching;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Coalesces committed preference changes and refreshes both widget settings owners on the UI thread.</summary>
internal sealed class WidgetPreferencesWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<Task> _refresh;
    private readonly ILogService _log;
    private bool _refreshing, _pending, _disposed;

    public WidgetPreferencesWatcher(IInteractiveUserService user, DispatcherQueue dispatcher, Func<Task> refresh, ILogService log)
    {
        _dispatcher = dispatcher;
        _refresh = refresh;
        _log = log;
        var preferences = AppDataPaths.Preferences(user);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.IsRepeating = false;
        _timer.Tick += Refresh;
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(preferences)!, Path.GetFileName(preferences))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _log.LogError("Could not observe a desktop widget preference change.", e.GetException());
        ScheduleRefresh();
    }

    private void ScheduleRefresh() => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Start();
    });

    private async void Refresh(DispatcherQueueTimer sender, object args)
    {
        if (_disposed) return;
        if (_refreshing) { _pending = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _pending = false;
                await _refresh();
            } while (_pending && !_disposed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { _log.LogError("Could not reload desktop widget preferences.", ex); }
        finally { _refreshing = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= Refresh;
        _watcher.Dispose();
    }
}
