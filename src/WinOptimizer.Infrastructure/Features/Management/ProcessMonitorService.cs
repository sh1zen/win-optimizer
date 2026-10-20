using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

internal sealed record NativeProcessSample(int ProcessorCount, IReadOnlyList<ProcessReading> Processes);
internal interface IProcessMonitorSource
{
    NativeProcessSample Read();
    void AssertCurrent(ProcessIdentity identity);
    ProcessInspection Inspect(ProcessIdentity identity);
    ProcessPortSnapshot ReadPorts(ProcessIdentity identity);
}
internal interface IProcessOpenFileSource
{
    Task<ProcessFileSnapshot> ReadAsync(ProcessIdentity identity, bool acceptLicense, CancellationToken token);
}
internal interface IProcessNetworkSource
{
    IProcessNetworkSession Start(ProcessIdentity identity);
}
internal interface IProcessNetworkSession : IAsyncDisposable
{
    ProcessNetworkSnapshot Read();
}

/// <summary>Serializes samples and capture ownership; rechecks lifetimes around every selected-process query.</summary>
internal sealed class ProcessMonitorService(IProcessMonitorSource source, IProcessOpenFileSource files,
    IProcessNetworkSource network, TimeProvider clock) : IProcessMonitorService
{
    private readonly SemaphoreSlim _samples = new(1, 1);
    private readonly SemaphoreSlim _captures = new(1, 1);
    private readonly ProcessMetricWindow _metrics = new();
    private long? _sampleTime;
    private IProcessNetworkSession? _capture;
    private ProcessIdentity? _captureIdentity;
    private ProcessNetworkSnapshot? _lastNetwork;

    public async Task<ProcessSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        await _samples.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sample = await Task.Run(source.Read, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetTimestamp();
            var elapsed = _sampleTime.HasValue ? clock.GetElapsedTime(_sampleTime.Value, now) : TimeSpan.Zero;
            _sampleTime = now;
            return _metrics.Add(clock.GetUtcNow(), elapsed, sample.ProcessorCount, sample.Processes);
        }
        finally { _samples.Release(); }
    }

    public Task<ProcessInspection> InspectAsync(ProcessIdentity identity, CancellationToken cancellationToken) => Task.Run(() =>
    {
        source.AssertCurrent(identity);
        var result = source.Inspect(identity);
        source.AssertCurrent(identity); cancellationToken.ThrowIfCancellationRequested();
        return result;
    }, cancellationToken);

    public Task<ProcessPortSnapshot> ReadPortsAsync(ProcessIdentity identity, CancellationToken cancellationToken) => Task.Run(() =>
    {
        source.AssertCurrent(identity); var result = source.ReadPorts(identity); source.AssertCurrent(identity);
        cancellationToken.ThrowIfCancellationRequested(); return result;
    }, cancellationToken);

    public async Task<ProcessFileSnapshot> ReadOpenFilesAsync(ProcessIdentity identity, bool acceptSysinternalsLicense, CancellationToken cancellationToken)
    {
        if (!acceptSysinternalsLicense) throw new UtilityOperationException("Accept the Sysinternals license before requesting open files.");
        await Task.Run(() => source.AssertCurrent(identity), cancellationToken).ConfigureAwait(false);
        var result = await files.ReadAsync(identity, acceptSysinternalsLicense, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => source.AssertCurrent(identity), cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task StartNetworkAsync(ProcessIdentity identity, CancellationToken cancellationToken)
    {
        await _captures.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture != null) throw new UtilityOperationException("Stop the current process capture before starting another.");
            await Task.Run(() => source.AssertCurrent(identity), cancellationToken).ConfigureAwait(false);
            var capture = await Task.Run(() => network.Start(identity), cancellationToken).ConfigureAwait(false);
            try { await Task.Run(() => source.AssertCurrent(identity), cancellationToken).ConfigureAwait(false); }
            catch { await capture.DisposeAsync().ConfigureAwait(false); throw; }
            _capture = capture; _captureIdentity = identity; _lastNetwork = null;
        }
        finally { _captures.Release(); }
    }

    public async Task<ProcessNetworkSnapshot?> ReadNetworkAsync(CancellationToken cancellationToken)
    {
        await _captures.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_capture == null) return _lastNetwork;
            var capture = _capture;
            try
            {
                await Task.Run(() => source.AssertCurrent(_captureIdentity!), cancellationToken).ConfigureAwait(false);
                var snapshot = capture.Read();
                await Task.Run(() => source.AssertCurrent(_captureIdentity!), cancellationToken).ConfigureAwait(false);
                if (snapshot.State != ProcessNetworkState.Running)
                { await capture.DisposeAsync().ConfigureAwait(false); _capture = null; }
                return _lastNetwork = snapshot;
            }
            catch (UtilityOperationException ex)
            {
                await capture.DisposeAsync().ConfigureAwait(false); _capture = null;
                return _lastNetwork = (_lastNetwork ?? new(_captureIdentity!, clock.GetUtcNow(), clock.GetUtcNow(),
                    ProcessNetworkState.ProcessEnded, [], 0, 0, false, "")) with { State = ProcessNetworkState.ProcessEnded, Diagnostic = ex.Message };
            }
        }
        finally { _captures.Release(); }
    }

    public async Task<ProcessNetworkSnapshot?> StopNetworkAsync()
    {
        await _captures.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_capture != null) { await _capture.DisposeAsync().ConfigureAwait(false); _capture = null; }
            if (_lastNetwork is { State: ProcessNetworkState.Running }) _lastNetwork = _lastNetwork with { State = ProcessNetworkState.Stopped };
            return _lastNetwork;
        }
        finally { _captures.Release(); }
    }
}
