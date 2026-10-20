using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Serializes samples and computes deltas only for matching, non-reset counters.</summary>
public sealed class WindowsSystemMonitorService(ISystemMonitorProbe probe) : ISystemMonitorService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SystemMonitorCounters? _previous;

    /// <summary>Waits for in-flight sampling before releasing hardware resources on a worker thread.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(probe.Close).ConfigureAwait(false); _previous = null; }
        finally { _gate.Release(); }
    }

    public void Reset()
    {
        // The widget awaits its sampling loop before resetting a new session.
        if (!_gate.Wait(0)) throw new InvalidOperationException("SysMonitor is still sampling.");
        try { _previous = null; }
        finally { _gate.Release(); }
    }

    public async Task<SystemMonitorSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await Task.Run(() => probe.Read(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            double? cpu = null, download = null, upload = null;
            if (_previous is { } previous)
            {
                if (current.IdleTime is { } idle && current.KernelTime is { } kernel && current.UserTime is { } user &&
                    previous.IdleTime is { } oldIdle && previous.KernelTime is { } oldKernel && previous.UserTime is { } oldUser &&
                    idle >= oldIdle && kernel >= oldKernel && user >= oldUser)
                {
                    var total = (double)(kernel - oldKernel) + (user - oldUser);
                    if (total > 0) cpu = Math.Clamp(100 * (1 - (idle - oldIdle) / total), 0, 100);
                }
                var seconds = (current.Elapsed - previous.Elapsed).TotalSeconds;
                if (seconds > 0)
                {
                    foreach (var (id, counters) in current.Network)
                    {
                        if (!previous.Network.TryGetValue(id, out var old) || counters.Received < old.Received || counters.Sent < old.Sent)
                            continue;
                        download = (download ?? 0) + (counters.Received - old.Received) / seconds;
                        upload = (upload ?? 0) + (counters.Sent - old.Sent) / seconds;
                    }
                }
            }
            _previous = current;
            return new(current.CollectedAt, cpu, current.TotalMemoryBytes, current.AvailableMemoryBytes,
                download, upload, current.Temperatures, current.Fans, current.Notices)
                { HardwareMetrics = current.HardwareMetrics };
        }
        finally { _gate.Release(); }
    }
}
