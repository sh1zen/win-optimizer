using Moq;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Exercises capture ownership and PID-reuse gates with small fake Windows boundaries.</summary>
public sealed class ProcessMonitorServiceTests
{
    private static readonly ProcessIdentity Identity = new(42, 100);
    private readonly Source _source = new();
    private readonly Files _files = new();
    private readonly Network _network = new();
    private ProcessMonitorService Create() => new(_source, _files, _network, TimeProvider.System);

    [Fact]
    public void ConstructionDoesNotOpenWindowsResources()
    { _ = Create(); Assert.Equal(0, _source.Asserts); Assert.Equal(0, _network.Starts); Assert.Equal(0, _files.Reads); }

    [Fact]
    public async Task MissingFileConsentDoesNotLaunchAToolOrQueryTheProcess()
    {
        await Assert.ThrowsAsync<UtilityOperationException>(() => Create().ReadOpenFilesAsync(Identity, false, default));
        Assert.Equal(0, _files.Reads); Assert.Equal(0, _source.Asserts);
    }

    [Fact]
    public async Task FilesAreValidatedBeforeAndAfterTheNativeQuery()
    {
        var result = await Create().ReadOpenFilesAsync(Identity, true, default);
        Assert.Equal(Identity, result.Identity); Assert.Equal(2, _source.Asserts); Assert.Equal(1, _files.Reads);
    }

    [Fact]
    public async Task AProcessThatExitsDuringFileEnumerationCannotPublishThoseFiles()
    {
        _files.AfterRead = () => _source.Accessible = false;
        await Assert.ThrowsAsync<UtilityOperationException>(() => Create().ReadOpenFilesAsync(Identity, true, default));
    }

    [Fact]
    public async Task MetadataAndPortsAreBoundToTheSelectedLifetime()
    {
        var service = Create(); await service.InspectAsync(Identity, default); await service.ReadPortsAsync(Identity, default);
        Assert.Equal(4, _source.Asserts);
        _source.Accessible = false;
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.ReadPortsAsync(Identity, default));
    }

    [Fact]
    public async Task OnlyOneCaptureCanBeOwnedAndStoppingRetainsTheLastValidatedSnapshot()
    {
        var service = Create(); await service.StartNetworkAsync(Identity, default);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.StartNetworkAsync(Identity, default));
        var observed = await service.ReadNetworkAsync(default); var stopped = await service.StopNetworkAsync();
        Assert.Equal(ProcessNetworkState.Stopped, stopped!.State); Assert.Same(observed!.Hosts, stopped.Hosts);
        Assert.Equal(1, _network.Session.Disposals); Assert.Equal(1, _network.Starts);
        await service.StopNetworkAsync(); Assert.Equal(1, _network.Session.Disposals);
    }

    [Fact]
    public async Task ReusedPidStopsTheCaptureAndRetainsOnlyPreviouslyValidatedCounters()
    {
        var service = Create(); await service.StartNetworkAsync(Identity, default);
        var first = await service.ReadNetworkAsync(default); _source.Accessible = false;
        var ended = await service.ReadNetworkAsync(default);
        Assert.Equal(ProcessNetworkState.ProcessEnded, ended!.State); Assert.Same(first!.Hosts, ended.Hosts);
        Assert.Equal(1, _network.Session.Reads); Assert.Equal(1, _network.Session.Disposals);
    }

    [Fact]
    public async Task IdentityChangedDuringSnapshotReadDoesNotPublishTheNewCounters()
    {
        var service = Create(); await service.StartNetworkAsync(Identity, default);
        var first = await service.ReadNetworkAsync(default);
        _network.Session.AfterRead = () => _source.Accessible = false;
        var ended = await service.ReadNetworkAsync(default);
        Assert.Equal(ProcessNetworkState.ProcessEnded, ended!.State); Assert.Same(first!.Hosts, ended.Hosts); Assert.Equal(1, _network.Session.Disposals);
    }

    [Fact]
    public async Task FailedEtwStreamsAreDisposedAndKeepTheirFailureState()
    {
        _network.Session.State = ProcessNetworkState.Failed;
        var service = Create(); await service.StartNetworkAsync(Identity, default);
        var snapshot = await service.ReadNetworkAsync(default);
        Assert.Equal(ProcessNetworkState.Failed, snapshot!.State); Assert.Equal(1, _network.Session.Disposals);
    }

    [Fact]
    public async Task AProcessLostDuringCaptureStartupDisposesTheNewSession()
    {
        _network.AfterStart = () => _source.Accessible = false;
        await Assert.ThrowsAsync<UtilityOperationException>(() => Create().StartNetworkAsync(Identity, default));
        Assert.Equal(1, _network.Session.Disposals);
    }

    [Fact]
    public async Task CancellationAfterSessionCreationStillDisposesTheSession()
    {
        using var cancellation = new CancellationTokenSource(); _network.AfterStart = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().StartNetworkAsync(Identity, cancellation.Token));
        Assert.Equal(1, _network.Session.Disposals);
    }

    private sealed class Source : IProcessMonitorSource
    {
        public int Asserts; public bool Accessible = true;
        public void AssertCurrent(ProcessIdentity identity) { Asserts++; if (!Accessible || identity != Identity) throw new UtilityOperationException("Lifetime changed"); }
        public NativeProcessSample Read() => new(2, []);
        public ProcessInspection Inspect(ProcessIdentity identity) => new(identity, DateTimeOffset.Now, "example.exe", "Example user", "Example", "Example", [], []);
        public ProcessPortSnapshot ReadPorts(ProcessIdentity identity) => new(identity, DateTimeOffset.Now, [], []);
    }
    private sealed class Files : IProcessOpenFileSource
    {
        public int Reads; public Action? AfterRead;
        public Task<ProcessFileSnapshot> ReadAsync(ProcessIdentity identity, bool acceptLicense, CancellationToken token)
        { Reads++; AfterRead?.Invoke(); return Task.FromResult(new ProcessFileSnapshot(identity, DateTimeOffset.Now, [new("10", "File", "C:\\Example.txt")], "")); }
    }
    private sealed class Network : IProcessNetworkSource
    {
        public int Starts; public Action? AfterStart; public Session Session = new();
        public IProcessNetworkSession Start(ProcessIdentity identity) { Starts++; AfterStart?.Invoke(); return Session; }
    }
    private sealed class Session : IProcessNetworkSession
    {
        public int Disposals, Reads; public Action? AfterRead; public ProcessNetworkState State = ProcessNetworkState.Running;
        public ProcessNetworkSnapshot Read() { Reads++; AfterRead?.Invoke(); return new(Identity, DateTimeOffset.Now, DateTimeOffset.Now, State,
            [new("TCP", "198.51.100.1", (ulong)Reads * 100, 0, Reads, DateTimeOffset.Now)], 0, 0, false, State == ProcessNetworkState.Failed ? "ETW failed" : ""); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
