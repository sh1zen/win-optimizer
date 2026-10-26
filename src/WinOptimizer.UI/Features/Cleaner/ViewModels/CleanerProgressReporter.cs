using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.UI.Features.Cleaner.ViewModels;

/// <summary>Keeps at most one UI progress callback queued, publishing the latest measured work.</summary>
internal sealed class CleanerProgressReporter(Action<CleanerProgress> publish, SynchronizationContext? context = null)
    : IProgress<CleanerProgress>
{
    private readonly SynchronizationContext _context = context ?? SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly object _gate = new();
    private CleanerProgress? _latest;
    private bool _queued;

    /// <summary>Replaces pending display work without posting another UI callback for each file.</summary>
    public void Report(CleanerProgress value)
    {
        lock (_gate)
        {
            _latest = value;
            if (_queued) return;
            _queued = true;
        }
        try { _context.Post(_ => PublishLatest(), null); }
        catch
        {
            lock (_gate) _queued = false;
            throw;
        }
    }

    /// <summary>Releases the queue slot before publication so reports arriving during a callback are retained.</summary>
    private void PublishLatest()
    {
        CleanerProgress? value;
        lock (_gate)
        {
            value = _latest;
            _latest = null;
            _queued = false;
        }
        if (value is not null) publish(value);
    }
}
