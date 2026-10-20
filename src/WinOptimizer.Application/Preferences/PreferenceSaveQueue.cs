using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Preferences;

/// <summary>Serializes immutable preference snapshots and retains the latest edit's validation or persistence error.</summary>
public sealed class PreferenceSaveQueue<T>(Func<T, Task<OperationResult>> write, Action<T> publish,
    Action<string?> reportError, string failureMessage)
{
    private Task<bool> _pending = Task.FromResult(true);
    private long _revision;
    private bool _valid = true;

    public void Enqueue(T snapshot)
    {
        _valid = true;
        _pending = WriteAsync(_pending, snapshot, ++_revision);
    }

    public void Reject(string message)
    {
        _valid = false;
        _revision++;
        reportError(message);
    }

    private async Task<bool> WriteAsync(Task<bool> preceding, T snapshot, long revision)
    {
        await preceding;
        OperationResult result;
        try { result = await write(snapshot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (revision == _revision) reportError(ex.Message);
            return false;
        }
        if (!result.Success)
        {
            if (revision == _revision) reportError(result.ErrorMessage ?? failureMessage);
            return false;
        }
        publish(snapshot);
        if (revision == _revision) reportError(null);
        return true;
    }

    /// <summary>Drains edits added while an earlier write was pending; callers can reject stale settings on failure.</summary>
    public async Task<bool> FlushAsync()
    {
        Task<bool> pending;
        bool saved;
        do { pending = _pending; saved = await pending; } while (!ReferenceEquals(pending, _pending));
        return saved && _valid;
    }
}
