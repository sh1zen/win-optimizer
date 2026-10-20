using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Extensions;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using WinOptimizer.Infrastructure.Extensions.DI;

namespace WinOptimizer.Infrastructure.Tests.Extensions;

public sealed class SystemMonitorTests
{
    private static SystemMonitorCounters Sample(int second, ulong idle = 100, ulong kernel = 200, ulong user = 100,
        IReadOnlyDictionary<string, (long Received, long Sent)>? adapters = null) =>
        new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero).AddSeconds(second), TimeSpan.FromSeconds(second),
            idle, kernel, user, 16000, 6000, adapters ?? new Dictionary<string, (long, long)> { ["ethernet"] = (1000, 2000) },
            [new("CPU Package", 48)], [new("CPU Fan", 1200)], []);

    [Fact]
    public void DesktopCompositionResolvesOneMonitorWithoutReadingHardware()
    {
        using var services = new ServiceCollection().AddInfrastructureServices().BuildServiceProvider();
        Assert.IsType<WindowsSystemMonitorProbe>(services.GetRequiredService<ISystemMonitorProbe>());
        var monitor = services.GetRequiredService<ISystemMonitorService>();
        Assert.IsType<WindowsSystemMonitorService>(monitor);
        Assert.Same(monitor, services.GetRequiredService<ISystemMonitorService>());
    }

    [Fact]
    public async Task ConcurrentReadsAreSerializedAndAWaitingReadCanBeCancelled()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var probe = new FakeProbe(Sample(0)) { OnRead = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); } };
        var service = new WindowsSystemMonitorService(probe);
        var first = service.ReadAsync(default);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            var second = service.ReadAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(1, probe.Reads);
        }
        finally { release.Set(); await first; }
    }

    [Fact]
    public async Task InitialSampleRetainsMeasurementsWithoutInventingRates()
    {
        var measured = Sample(0) with { HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, "GPU Core", 55)] };
        var probe = new FakeProbe(measured);
        var service = new WindowsSystemMonitorService(probe);
        Assert.Equal(0, probe.Reads);
        var snapshot = await service.ReadAsync(default);
        Assert.Null(snapshot.CpuUsagePercent);
        Assert.Null(snapshot.DownloadBytesPerSecond);
        Assert.Equal(16000UL, snapshot.TotalMemoryBytes);
        Assert.Equal(48, Assert.Single(snapshot.Temperatures).Value);
        Assert.Equal(1200, Assert.Single(snapshot.Fans).Value);
        Assert.Same(measured.HardwareMetrics, snapshot.HardwareMetrics);
    }

    [Fact]
    public async Task RatesUseElapsedTimeAndKernelIncludesIdle()
    {
        var service = new WindowsSystemMonitorService(new FakeProbe(Sample(0),
            Sample(2, 150, 300, 200, new Dictionary<string, (long, long)> { ["ethernet"] = (3048, 3024) })));
        await service.ReadAsync(default);
        var snapshot = await service.ReadAsync(default);
        Assert.Equal(75, snapshot.CpuUsagePercent);
        Assert.Equal(1024, snapshot.DownloadBytesPerSecond);
        Assert.Equal(512, snapshot.UploadBytesPerSecond);
    }

    [Fact]
    public async Task AdapterChangesAndCounterResetsDoNotCauseSpikes()
    {
        var service = new WindowsSystemMonitorService(new FakeProbe(
            Sample(0, adapters: new Dictionary<string, (long, long)> { ["stable"] = (100, 200), ["reset"] = (10000, 10000) }),
            Sample(1, 1, 2, 1, new Dictionary<string, (long, long)>
            { ["stable"] = (400, 400), ["reset"] = (5, 5), ["new"] = (1000000, 1000000) })));
        await service.ReadAsync(default);
        var snapshot = await service.ReadAsync(default);
        Assert.Null(snapshot.CpuUsagePercent);
        Assert.Equal(300, snapshot.DownloadBytesPerSecond);
        Assert.Equal(200, snapshot.UploadBytesPerSecond);
    }

    [Fact]
    public async Task ResetStartsANewSessionWithoutReusingDisabledTime()
    {
        var service = new WindowsSystemMonitorService(new FakeProbe(Sample(0), Sample(20, 200, 500, 300)));
        await service.ReadAsync(default);
        service.Reset();
        var snapshot = await service.ReadAsync(default);
        Assert.Null(snapshot.CpuUsagePercent);
        Assert.Null(snapshot.UploadBytesPerSecond);
    }

    [Fact]
    public async Task CancelledSampleDoesNotReplacePreviousEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new FakeProbe(Sample(0), Sample(1), Sample(2, 150, 300, 200));
        var service = new WindowsSystemMonitorService(probe);
        await service.ReadAsync(default);
        probe.OnRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(cancellation.Token));
        probe.OnRead = null;
        var snapshot = await service.ReadAsync(default);
        Assert.Equal(75, snapshot.CpuUsagePercent);
    }

    [Fact]
    public void EmptyOrPartialWmiProvidersDoNotHideTheNextProvidersReadings()
    {
        foreach (var firstHasTemperature in new[] { true, false })
        {
            List<string> calls = [], notices = [];
            var readings = WindowsSystemMonitorProbe.ReadSensors(provider =>
            {
                calls.Add(provider);
                return provider == "LibreHardwareMonitor"
                    ? (firstHasTemperature ? [new("First CPU", 48)] : [], [])
                    : ([new("Second CPU", 49)], [new("CPU Fan", 1200)]);
            }, default, notices);
            Assert.Equal(new[] { "LibreHardwareMonitor", "OpenHardwareMonitor" }, calls);
            Assert.Equal(firstHasTemperature ? "First CPU" : "Second CPU", Assert.Single(readings.Temperatures).Name);
            Assert.Equal(1200, Assert.Single(readings.Fans).Value);
            Assert.Empty(notices);
        }
    }

    [Fact]
    public void WmiAccessFailureIsReportedWhileTheNextProviderCanSucceed()
    {
        List<string> notices = [];
        var readings = WindowsSystemMonitorProbe.ReadSensors(provider =>
        {
            if (provider == "LibreHardwareMonitor") throw new UnauthorizedAccessException("provider access denied");
            return ([new("CPU", 45)], [new("Fan", 1000)]);
        }, default, notices);
        Assert.Single(readings.Temperatures);
        Assert.Single(readings.Fans);
        Assert.Equal("LibreHardwareMonitor: provider access denied", Assert.Single(notices));
    }

    [Fact]
    public async Task StopWaitsForTheCurrentReadAndReleasesTheHardwareSession()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var probe = new FakeProbe(Sample(0), Sample(1)) { OnRead = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); } };
        var service = new WindowsSystemMonitorService(probe);
        var read = service.ReadAsync(default);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            var stop = service.StopAsync();
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, probe.Closes);
            release.Set();
            await read;
            await stop;
            Assert.Equal(1, probe.Closes);
            probe.OnRead = null;
            Assert.Null((await service.ReadAsync(default)).CpuUsagePercent);
        }
        finally { release.Set(); await read; }
    }

    [Fact]
    public async Task MissingCountersAndSensorsRemainUnavailable()
    {
        var sample = Sample(0) with { IdleTime = null, KernelTime = null, UserTime = null,
            TotalMemoryBytes = null, AvailableMemoryBytes = null, Temperatures = [], Fans = [],
            Network = new Dictionary<string, (long, long)>(), Notices = ["CPU: unavailable"] };
        var service = new WindowsSystemMonitorService(new FakeProbe(sample, sample with { Elapsed = TimeSpan.FromSeconds(2) }));
        await service.ReadAsync(default);
        var snapshot = await service.ReadAsync(default);
        Assert.Null(snapshot.CpuUsagePercent);
        Assert.Null(snapshot.TotalMemoryBytes);
        Assert.Null(snapshot.DownloadBytesPerSecond);
        Assert.Empty(snapshot.Temperatures);
        Assert.Equal("CPU: unavailable", Assert.Single(snapshot.Notices));
    }

    private sealed class FakeProbe(params SystemMonitorCounters[] samples) : ISystemMonitorProbe
    {
        private readonly Queue<SystemMonitorCounters> _samples = new(samples);
        public int Reads { get; private set; }
        public Action? OnRead { get; set; }
        public int Closes { get; private set; }
        public void Close() => Closes++;
        public SystemMonitorCounters Read(CancellationToken cancellationToken)
        { Reads++; OnRead?.Invoke(); return _samples.Dequeue(); }
    }
}
