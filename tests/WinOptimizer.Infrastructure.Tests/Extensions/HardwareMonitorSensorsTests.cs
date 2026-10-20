using LibreHardwareMonitor.Hardware;
using Moq;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Extensions;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Extensions;

public sealed class HardwareMonitorSensorsTests
{
    [Fact]
    public void SamplingOpensOnceUpdatesSubhardwareAndClosesBeforeReopening()
    {
        var parent = Hardware("Processor", HardwareType.Cpu, [Sensor("Package", SensorType.Temperature, 48)]);
        var child = Hardware("Super IO", HardwareType.SuperIO, [Sensor("CPU Fan", SensorType.Fan, 1200)]);
        parent.SetupGet(h => h.SubHardware).Returns([child.Object]);
        var session = new FakeSession();
        session.Hardware = [parent.Object];
        session.HasLowLevelAccess = true;
        var creates = 0;
        using var reader = new HardwareMonitorSensors(() => { creates++; return session; });
        Assert.Equal(0, creates);
        List<string> notices = [];
        var sample = reader.Read(default, notices);
        Assert.Equal("Package · CPU · Processor", Assert.Single(sample.Temperatures).Name);
        Assert.Equal(48, sample.Temperatures[0].Value);
        Assert.Equal(1200, Assert.Single(sample.Fans).Value);
        Assert.Empty(notices);
        reader.Read(default, notices);
        Assert.Equal(1, creates);
        parent.Verify(h => h.Update(), Times.Exactly(2));
        child.Verify(h => h.Update(), Times.Exactly(2));
        reader.Dispose();
        Assert.Equal(1, session.Closes);
        reader.Read(default, notices);
        Assert.Equal(2, creates);
    }

    [Fact]
    public void InvalidValuesAreExcludedAndAStoppedFanIsAValidMeasurement()
    {
        var hardware = Hardware("GPU", HardwareType.GpuNvidia,
        [Sensor("Core", SensorType.Temperature, 55), Sensor("Missing", SensorType.Temperature, null),
            Sensor("Invalid", SensorType.Temperature, float.NaN), Sensor("Too hot", SensorType.Temperature, 201),
            Sensor("Stopped", SensorType.Fan, 0), Sensor("Bad RPM", SensorType.Fan, -1),
            Sensor("PWM", SensorType.Control, 70)]);
        var session = new FakeSession();
        session.Hardware = [hardware.Object];
        session.HasLowLevelAccess = false;
        using var reader = new HardwareMonitorSensors(() => session);
        List<string> notices = [];
        var sample = reader.Read(default, notices);
        Assert.Equal(55, Assert.Single(sample.Temperatures).Value);
        Assert.Equal(0, Assert.Single(sample.Fans).Value);
        Assert.Contains("PawnIO", Assert.Single(notices));
    }

    [Fact]
    public void AdditionalMetricsKeepTheirHardwareIdentityAndNormalizeGpuMemoryToGigabytes()
    {
        var gpu = Hardware("Graphics", HardwareType.GpuNvidia,
            [Sensor("Core", SensorType.Load, 65), Sensor("Allocated", SensorType.SmallData, 4096),
                Sensor("Total", SensorType.Data, 8), Sensor("Clock", SensorType.Clock, 1750),
                Sensor("Board power", SensorType.Power, 120.5f), Sensor("Core voltage", SensorType.Voltage, 1.025f)]);
        var storage = Hardware("SSD", HardwareType.Storage,
            [Sensor("Activity", SensorType.Load, 12), Sensor("Read", SensorType.Throughput, 1048576)]);
        var cpu = Hardware("Processor", HardwareType.Cpu,
            [Sensor("Total", SensorType.Load, 30), Sensor("Core clock", SensorType.Clock, 4200)]);
        var session = new FakeSession { HasLowLevelAccess = true, Hardware = [gpu.Object, storage.Object, cpu.Object] };
        using var reader = new HardwareMonitorSensors(() => session);
        var metrics = reader.Read(default, []).Metrics;
        Assert.Equal(9, metrics.Count);
        Assert.Equal(65, Assert.Single(metrics, m => m.Kind == SystemMonitorMetricKind.GpuUsage).Value);
        var memory = metrics.Where(m => m.Kind == SystemMonitorMetricKind.GpuMemory).ToArray();
        Assert.Equal(new[] { 4d, 8d }, memory.Select(m => m.Value));
        Assert.All(memory, m => Assert.Contains("Graphics", m.Name));
        Assert.Equal(2, metrics.Count(m => m.Kind == SystemMonitorMetricKind.ClockSpeed));
        Assert.Equal(120.5, Assert.Single(metrics, m => m.Kind == SystemMonitorMetricKind.Power).Value);
        Assert.Equal(1.025, Assert.Single(metrics, m => m.Kind == SystemMonitorMetricKind.Voltage).Value, 3);
        Assert.Equal(12, Assert.Single(metrics, m => m.Kind == SystemMonitorMetricKind.StorageActivity).Value);
        Assert.Equal(1048576, Assert.Single(metrics, m => m.Kind == SystemMonitorMetricKind.StorageThroughput).Value);
        gpu.Verify(h => h.Update(), Times.Once);
        storage.Verify(h => h.Update(), Times.Once);
        cpu.Verify(h => h.Update(), Times.Once);
    }

    [Theory]
    [InlineData(HardwareType.GpuNvidia)] [InlineData(HardwareType.GpuAmd)] [InlineData(HardwareType.GpuIntel)]
    public void GpuMetricsRejectInvalidValuesWhileKeepingIdleReadings(HardwareType type)
    {
        var hardware = Hardware("Graphics", type,
            [Sensor("Idle", SensorType.Load, 0), Sensor("Too high", SensorType.Load, 101),
                Sensor("Invalid", SensorType.Load, float.PositiveInfinity), Sensor("Missing", SensorType.SmallData, null),
                Sensor("Bad memory", SensorType.SmallData, -1), Sensor("Bad clock", SensorType.Clock, -1),
                Sensor("Bad power", SensorType.Power, -1), Sensor("Rail", SensorType.Voltage, -12)]);
        using var reader = new HardwareMonitorSensors(() => new FakeSession { HasLowLevelAccess = true, Hardware = [hardware.Object] });
        var metrics = reader.Read(default, []).Metrics;
        Assert.Equal(2, metrics.Count);
        Assert.Equal(0, Assert.Single(metrics, m => m.Kind == SystemMonitorMetricKind.GpuUsage).Value);
        Assert.Equal(-12, Assert.Single(metrics, m => m.Kind == SystemMonitorMetricKind.Voltage).Value);
    }

    [Fact]
    public void OneDeviceFailurePreservesOthersAndCancellationDoesNotBecomeANotice()
    {
        var broken = Hardware("Motherboard", HardwareType.Motherboard, []);
        broken.Setup(h => h.Update()).Throws(new IOException("device unavailable"));
        var gpu = Hardware("GPU", HardwareType.GpuNvidia, [Sensor("Core", SensorType.Temperature, 50)]);
        var session = new FakeSession();
        session.Hardware = [broken.Object, gpu.Object];
        session.HasLowLevelAccess = true;
        using var reader = new HardwareMonitorSensors(() => session);
        List<string> notices = [];
        Assert.Single(reader.Read(default, notices).Temperatures);
        Assert.Equal("Motherboard: device unavailable", Assert.Single(notices));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.Read(cancellation.Token, notices));
        Assert.Equal(1, session.Reads);
    }

    private static Mock<IHardware> Hardware(string name, HardwareType type, ISensor[] sensors)
    {
        var hardware = new Mock<IHardware>();
        hardware.SetupGet(h => h.Name).Returns(name);
        hardware.SetupGet(h => h.HardwareType).Returns(type);
        hardware.SetupGet(h => h.Sensors).Returns(sensors);
        hardware.SetupGet(h => h.SubHardware).Returns([]);
        return hardware;
    }

    private static ISensor Sensor(string name, SensorType type, float? value)
    {
        var sensor = new Mock<ISensor>();
        sensor.SetupGet(s => s.Name).Returns(name);
        sensor.SetupGet(s => s.SensorType).Returns(type);
        sensor.SetupGet(s => s.Value).Returns(value);
        return sensor.Object;
    }

    private sealed class FakeSession : IHardwareMonitorSession
    {
        public IReadOnlyList<IHardware> Hardware { get; set; } = [];
        public bool HasLowLevelAccess { get; set; }
        public int Reads { get; private set; }
        public int Closes { get; private set; }
        public IEnumerable<IHardware> GetHardware() { Reads++; return Hardware; }
        public void Dispose() => Closes++;
    }
}
