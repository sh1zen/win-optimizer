using WinOptimizer.Core.Features.Management;
using Xunit;

namespace WinOptimizer.Core.Tests.Management;

/// <summary>Tests process-lifetime rates and hierarchy independently of Windows counters.</summary>
public sealed class ProcessMetricTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static ProcessReading Reading(int pid = 10, long start = 100, int parent = 0, long cpu = 0, ulong read = 0, ulong write = 0) =>
        new(new(pid, start), parent, "Example", "Example.exe", cpu, 1024, 2048, read, write, 2, 10, "");
    private static ProcessMetric Metric(int pid, long start, int parent) => new(Reading(pid, start, parent), null, null, null);

    [Fact]
    public void FirstReadingIsUnknownRatherThanZero()
    {
        var metric = Assert.Single(new ProcessMetricWindow().Add(At, TimeSpan.FromSeconds(2), 4, [Reading()]).Processes);
        Assert.Null(metric.CpuPercent); Assert.Null(metric.ReadBytesPerSecond); Assert.Null(metric.WriteBytesPerSecond);
    }

    [Fact]
    public void CpuUsesAllProcessorsAndIoUsesCounterDeltas()
    {
        var window = new ProcessMetricWindow(); window.Add(At, TimeSpan.Zero, 4, [Reading(cpu: 100, read: 100, write: 200)]);
        var metric = Assert.Single(window.Add(At.AddSeconds(2), TimeSpan.FromSeconds(2), 4,
            [Reading(cpu: 100 + TimeSpan.FromSeconds(2).Ticks, read: 2100, write: 6200)]).Processes);
        Assert.Equal(25d, metric.CpuPercent); Assert.Equal(1000d, metric.ReadBytesPerSecond); Assert.Equal(3000d, metric.WriteBytesPerSecond);
    }

    [Fact]
    public void ReusedPidNeverInheritsTheOldProcessBaseline()
    {
        var window = new ProcessMetricWindow(); window.Add(At, TimeSpan.Zero, 2, [Reading(start: 100, cpu: 1000)]);
        var metric = Assert.Single(window.Add(At.AddSeconds(2), TimeSpan.FromSeconds(2), 2, [Reading(start: 200, cpu: 2000)]).Processes);
        Assert.Null(metric.CpuPercent); Assert.Null(metric.ReadBytesPerSecond);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(11)]
    public void PausesAndInvalidIntervalsEstablishFreshBaselines(int seconds)
    {
        var window = new ProcessMetricWindow(); window.Add(At, TimeSpan.Zero, 2, [Reading()]);
        Assert.Null(Assert.Single(window.Add(At, TimeSpan.FromSeconds(seconds), 2, [Reading(cpu: 200, read: 1000)]).Processes).CpuPercent);
    }

    [Fact]
    public void CounterResetsAndUnavailableMetricsStayUnknown()
    {
        var window = new ProcessMetricWindow(); window.Add(At, TimeSpan.Zero, 2, [Reading(cpu: 100, read: 100, write: 100)]);
        var metric = Assert.Single(window.Add(At.AddSeconds(2), TimeSpan.FromSeconds(2), 2,
            [Reading(cpu: 0, read: 0) with { WriteBytes = null }]).Processes);
        Assert.Null(metric.CpuPercent); Assert.Null(metric.ReadBytesPerSecond); Assert.Null(metric.WriteBytesPerSecond);
    }

    [Fact]
    public void AProtectedLifetimeCannotProduceAComputedRate()
    {
        var window = new ProcessMetricWindow(); window.Add(At, TimeSpan.Zero, 2, [Reading(start: 0)]);
        Assert.Null(Assert.Single(window.Add(At.AddSeconds(2), TimeSpan.FromSeconds(2), 2, [Reading(start: 0, cpu: 100)]).Processes).CpuPercent);
    }

    [Fact]
    public void ExitedProcessesAreRemovedFromTheBaseline()
    {
        var window = new ProcessMetricWindow(); window.Add(At, TimeSpan.Zero, 2, [Reading()]);
        window.Add(At.AddSeconds(2), TimeSpan.FromSeconds(2), 2, []);
        Assert.Null(Assert.Single(window.Add(At.AddSeconds(4), TimeSpan.FromSeconds(2), 2, [Reading(cpu: 100)]).Processes).CpuPercent);
    }

    [Fact]
    public void ParentCreatedAfterChildIsAReusedPidRatherThanAnAncestor()
    {
        var parents = ProcessHierarchy.Parents([Metric(10, 300, 0), Metric(20, 200, 10), Metric(30, 400, 10)]);
        Assert.False(parents.ContainsKey(new(20, 200))); Assert.Equal(new ProcessIdentity(10, 300), parents[new(30, 400)]);
    }

    [Fact]
    public void CyclesAreBrokenAndDeepTreesRemainIterative()
    {
        var cycle = ProcessHierarchy.Parents([Metric(1, 100, 2), Metric(2, 100, 1)]);
        Assert.Single(cycle);
        var chain = Enumerable.Range(1, 5000).Select(pid => Metric(pid, pid, pid - 1)).ToArray();
        Assert.Equal(4999, ProcessHierarchy.Parents(chain).Count);
    }

    [Fact]
    public void SelfParentsAndUnknownLifetimesAreNotLinked() =>
        Assert.Empty(ProcessHierarchy.Parents([Metric(1, 0, 0), Metric(2, 100, 1), Metric(3, 100, 3)]));
}
