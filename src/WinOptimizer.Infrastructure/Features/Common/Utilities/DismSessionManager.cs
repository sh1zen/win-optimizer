using System.Diagnostics;
using WinOptimizer.Core.Features.Common.Native;

namespace WinOptimizer.Infrastructure.Features.Common.Utilities;

/// <summary>Runs read-only DISM queries serially, reusing the online session between successful queries.</summary>
internal sealed class DismSessionManager(Func<Action<string>?, DismSessionManager.IOnlineSession> openSession,
    TimeSpan queryTimeout, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static DismSessionManager Shared { get; } = new(log => new OnlineSession(log),
        TimeSpan.FromSeconds(30), TimeProvider.System);

    internal interface IOnlineSession : IDisposable
    {
        uint Handle { get; }
    }

    internal sealed record QueryResult<T>(T? Value, Exception? Error) where T : class;

    private sealed class PendingQuery<T>(Func<uint, T> read) where T : class
    {
        public Func<uint, T> Read { get; } = read;
        public TaskCompletionSource<CancellationToken> Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<T> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Gives each requested query its own deadline and starts a fresh session after a failed query.</summary>
    internal async Task<QueryResult<T>[]> ExecuteAsync<T>(IReadOnlyList<Func<uint, T>> queries,
        Action<string>? log = null) where T : class
    {
        var results = new QueryResult<T>[queries.Count];
        var next = 0;
        while (next < queries.Count)
        {
            using var firstDeadline = new CancellationTokenSource(queryTimeout, clock);
            var pending = queries.Skip(next).Select(query => new PendingQuery<T>(query)).ToArray();
            var acquired = false;
            Task<T>? worker = null;
            try
            {
                log?.Invoke("[DismSession] Waiting for semaphore...");
                await _gate.WaitAsync(firstDeadline.Token).ConfigureAwait(false);
                acquired = true;
                worker = Task.Run(() => RunOnlineAsync(pending, log));
                for (var index = 0; index < pending.Length; index++)
                {
                    using var deadline = index == 0 ? null : new CancellationTokenSource(queryTimeout, clock);
                    var token = (deadline ?? firstDeadline).Token;
                    var phase = pending[index];
                    phase.Start.SetResult(token);
                    // The final query also waits for DISM cleanup. Earlier queries
                    // can deliver their results while the session stays open.
                    var completed = index == pending.Length - 1 ? worker :
                        await Task.WhenAny(phase.Result.Task, worker).WaitAsync(token).ConfigureAwait(false);
                    var value = await completed.WaitAsync(token).ConfigureAwait(false);
                    results[next++] = new(value, null);
                }
            }
            catch (Exception ex)
            {
                results[next++] = new(null, ex);
            }
            finally
            {
                // Once a deadline expires, the abandoned worker must never run the remaining queries.
                foreach (var phase in pending) phase.Start.TrySetCanceled();
                if (worker is not null) _ = ObserveWorkerAsync(worker);
                if (acquired) _gate.Release();
            }
        }
        return results;
    }

    private async Task<T> RunOnlineAsync<T>(PendingQuery<T>[] pending, Action<string>? log) where T : class
    {
        var token = await pending[0].Start.Task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var session = openSession(log);
        for (var index = 0; index < pending.Length; index++)
        {
            var phase = pending[index];
            token = await phase.Start.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.StartNew();
            var result = phase.Read(session.Handle);
            log?.Invoke($"[DismSession] Action completed ({elapsed.ElapsedMilliseconds}ms)");
            if (index == pending.Length - 1) return result;
            phase.Result.SetResult(result);
        }
        throw new InvalidOperationException("The DISM session has no queries.");
    }

    /// <summary>Observes native completion even when a caller's deadline has already expired.</summary>
    private static async Task ObserveWorkerAsync(Task worker)
    {
        try { await worker.ConfigureAwait(false); }
        catch (Exception) { /* The query result already carries the failure or timeout. */ }
    }

    private sealed class OnlineSession : IOnlineSession
    {
        private readonly Action<string>? _log;

        public OnlineSession(Action<string>? log)
        {
            _log = log;
            _log?.Invoke("[DismSession] Calling DismInitialize...");
            DismApi.ThrowIfFailed(DismApi.DismInitialize(DismApi.DismLogErrors, null, null), "Initialize");
            try
            {
                _log?.Invoke("[DismSession] Calling DismOpenSession...");
                DismApi.ThrowIfFailed(DismApi.DismOpenSession(DismApi.DISM_ONLINE_IMAGE_PATH, null, null, out var handle),
                    "OpenSession");
                Handle = handle;
            }
            catch
            {
                DismApi.DismShutdown();
                throw;
            }
        }

        public uint Handle { get; }

        public void Dispose()
        {
            try { DismApi.DismCloseSession(Handle); }
            finally { DismApi.DismShutdown(); }
        }
    }
}
