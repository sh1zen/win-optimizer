using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class SystemMonitorPresentationTests
{
    private static SystemMonitorReadingsViewModel Create()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        return new(localization.Object);
    }

    [Fact]
    public void CompactReadingsUseMeasuredCpuTemperatureAndDoNotMislabelGpuTemperature()
    {
        var vm = Create();
        var sample = new SystemMonitorSnapshot(DateTimeOffset.UnixEpoch, 12, 16UL << 30, 8UL << 30, 0, 1024,
            [new("GPU", 52), new("CPU Package", 45)], [new("CPU Fan", 2158)], []);
        vm.Publish(sample);
        Assert.Equal("CPU · 12%", vm.CpuHeadline);
        Assert.Equal("45°C", vm.CpuTemperature);
        Assert.Equal("CPU Package", vm.CpuTemperatureName);
        Assert.Equal("2158 rpm", vm.PrimaryFanValue);
        Assert.Equal("50%", vm.MemoryUsageValue);
        Assert.Equal($"{8d:0.00}", vm.UsedMemoryNumber);
        Assert.Equal($"{8d:0.00}", vm.FreeMemoryNumber);
        vm.Publish(sample with { Temperatures = [new("GPU", 52)], Fans = [], CpuUsagePercent = null,
            TotalMemoryBytes = null, AvailableMemoryBytes = null });
        Assert.Equal("—", vm.CpuTemperature);
        Assert.Equal("—", vm.PrimaryFanValue);
        Assert.Equal("CPU · —", vm.CpuHeadline);
        Assert.Equal("—", vm.MemoryUsageValue);
        Assert.Equal("—", vm.UsedMemoryNumber);
        Assert.Equal("—", vm.FreeMemoryNumber);
    }

    [Fact]
    public void ExpandedChoicesSurviveNewSamplesAndCompactSwitchesWithoutDiscardingReadings()
    {
        var vm = Create();
        var options = new SystemMonitorOptions { IsCompact = false, ShowFans = false, ShowCpu = false, ShowMemory = false };
        vm.ApplyOptions(options);
        vm.Publish(new(DateTimeOffset.UnixEpoch, 12, 16UL << 30, 8UL << 30, 0, 1024,
            [new("CPU Package", 45)], [new("CPU Fan", 1200)], []));
        Assert.False(vm.ShowFans); Assert.False(vm.ShowCpu); Assert.False(vm.ShowMemory);
        Assert.True(vm.ShowTemperatures); Assert.True(vm.ShowNetwork);
        Assert.Single(vm.Fans); Assert.Single(vm.CpuHistory);
        vm.ApplyOptions(options with { IsCompact = true });
        Assert.True(vm.IsCompact);
        Assert.False(vm.ShowFans);
        Assert.Equal("1200 rpm", vm.PrimaryFanValue);
    }

    [Fact]
    public void AdditionalMetricCardsFormatUnitsClearStaleReadingsAndPreserveSelections()
    {
        var vm = Create();
        vm.ApplyOptions(new() { ShowGpuUsage = true, ShowGpuMemory = true, ShowClockSpeed = true, ShowPower = true,
            ShowVoltage = true, ShowStorageActivity = true, ShowStorageThroughput = true });
        var metrics = new SystemMonitorMetric[]
        {
            new(SystemMonitorMetricKind.GpuUsage, "Core · GPU", 65), new(SystemMonitorMetricKind.GpuMemory, "Used · GPU", 4),
            new(SystemMonitorMetricKind.ClockSpeed, "Core · CPU", 4200), new(SystemMonitorMetricKind.Power, "Package · CPU", 35.5),
            new(SystemMonitorMetricKind.Voltage, "Core · CPU", 1.025), new(SystemMonitorMetricKind.StorageActivity, "Activity · SSD", 12),
            new(SystemMonitorMetricKind.StorageThroughput, "Read · SSD", 1048576)
        };
        var sample = new SystemMonitorSnapshot(DateTimeOffset.UnixEpoch, 12, 16UL << 30, 8UL << 30, 0, 0, [], [], [])
            { HardwareMetrics = metrics };
        vm.Publish(sample);
        Assert.True(vm.HasVisibleMetrics);
        var expected = new[] { "65%", $"{4d:0.00} GB", "4200 MHz", $"{35.5:0.0} W", $"{1.025:0.000} V", "12%", $"{1d:0.0} MB/s" };
        Assert.Equal(expected, vm.MetricSections.Select(s => Assert.Single(s.Readings).Value));
        Assert.All(vm.MetricSections, s => { Assert.True(s.IsVisible); Assert.False(s.IsEmpty); });
        vm.Publish(sample with { HardwareMetrics = [] });
        Assert.All(vm.MetricSections, s => { Assert.True(s.IsVisible); Assert.True(s.IsEmpty); Assert.Empty(s.Readings); });
        vm.ApplyOptions(new() { IsCompact = false });
        Assert.False(vm.HasVisibleMetrics);
        Assert.All(vm.MetricSections, s => Assert.False(s.IsVisible));
    }

    [Fact]
    public void HardwareChartsKeepAlignedHistoryAndRealGapsAcrossMissingSensors()
    {
        var vm = Create();
        var section = vm.MetricSections.Single(s => s.Kind == SystemMonitorMetricKind.GpuUsage);
        var sample = new SystemMonitorSnapshot(DateTimeOffset.UnixEpoch, 12, null, null, null, null, [], [], []);
        var updates = 0;
        section.ReadingsUpdated += (_, _) => updates++;
        vm.Publish(sample with { HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, "GPU Core · Device A", 10),
            new(SystemMonitorMetricKind.GpuUsage, "GPU Memory Controller · Device A", 20)] });
        var core = section.Readings[0];
        var memory = section.Readings[1];
        vm.Publish(sample with { HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, core.Name, 30)] });
        vm.Publish(sample with { HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, memory.Name, 25),
            new(SystemMonitorMetricKind.GpuUsage, core.Name, 40), new(SystemMonitorMetricKind.GpuUsage, "GPU Core · Device B", 5)] });
        Assert.Equal(3, updates);
        Assert.Same(memory, section.Readings[0]);
        Assert.Same(core, section.Readings[1]);
        Assert.Equal(new double?[] { 20, null, 25 }, memory.History);
        Assert.Equal(new double?[] { 10, 30, 40 }, core.History);
        Assert.Equal(new double?[] { null, null, 5 }, section.Readings[2].History);
        Assert.Equal("Core 1", core.ShortName);
        Assert.Equal("Core 2", section.Readings[2].ShortName);
        Assert.Equal(0, section.Minimum);
        Assert.Equal(100, section.Maximum);
        for (var i = 0; i < 65; i++) vm.Publish(sample with { HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, core.Name, i)] });
        Assert.Equal(60, core.History.Count);
        Assert.Equal(5, core.History.First());
        vm.Publish(sample with { HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, memory.Name, 0)] });
        var returned = Assert.Single(section.Readings);
        Assert.NotSame(memory, returned);
        Assert.Equal(60, returned.History.Count);
        Assert.Single(returned.History, value => value.HasValue);
        Assert.Equal(0, returned.History.Last());
        Assert.False(section.IsEmpty);
    }

    [Fact]
    public void GpuMemoryLegendsDistinguishUsedTotalAndSharedValuesWithoutRepeatingTheCategory()
    {
        var vm = Create();
        var section = vm.MetricSections.Single(s => s.Kind == SystemMonitorMetricKind.GpuMemory);
        var names = new[] { "GPU Memory Used · Graphics", "GPU Memory Total · Graphics", "D3D Shared Memory Used · Graphics" };
        vm.Publish(new(DateTimeOffset.UnixEpoch, null, null, null, null, null, [], [], [])
        {
            HardwareMetrics = names.Select(name => new SystemMonitorMetric(SystemMonitorMetricKind.GpuMemory, name, 2)).ToArray()
        });
        Assert.Equal(new[] { "Used", "Total", "Shared Used" }, section.Readings.Select(series => series.ShortName));
        Assert.Equal(names, section.Readings.Select(series => series.Name));
    }

    [Fact]
    public void ChartAxesUseRetainedPhysicalValuesAndKeepDuplicateSensorNamesDistinct()
    {
        var vm = Create();
        var section = vm.MetricSections.Single(s => s.Kind == SystemMonitorMetricKind.Voltage);
        var sample = new SystemMonitorSnapshot(DateTimeOffset.UnixEpoch, 12, null, null, null, null, [], [], []);
        vm.Publish(sample with { HardwareMetrics = [new(SystemMonitorMetricKind.Voltage, "Rail · Board", -12),
            new(SystemMonitorMetricKind.Voltage, "Rail · Board", 5)] });
        Assert.Equal(2, section.Readings.Count);
        var negative = section.Readings[0];
        var positive = section.Readings[1];
        Assert.NotEqual(negative.ColorIndex, positive.ColorIndex);
        Assert.Equal(-12, section.Minimum);
        Assert.Equal(5, section.Maximum);
        vm.Publish(sample with { HardwareMetrics = [new(SystemMonitorMetricKind.Voltage, "Rail · Board", -6),
            new(SystemMonitorMetricKind.Voltage, "Rail · Board", 3)] });
        Assert.Equal(-12, section.Minimum);
        Assert.Equal(5, section.Maximum);
        Assert.Equal(new double?[] { -12, -6 }, negative.History);
        Assert.Equal(new double?[] { 5, 3 }, positive.History);
        Assert.Equal($"{-6d:0.000} V", negative.Value);
        vm.Publish(sample);
        Assert.True(section.IsEmpty);
        Assert.Empty(section.Readings);
    }

    [Fact]
    public void DiagnosticVisibilityTracksRealErrorsAndProviderNotices()
    {
        var vm = Create();
        Assert.False(vm.HasError); Assert.False(vm.HasNotices);
        vm.Error = "unavailable counter";
        Assert.True(vm.HasError);
        vm.Publish(new(DateTimeOffset.UnixEpoch, 0, 8UL << 30, 20UL << 30, null, null, [], [], ["provider access denied"]));
        Assert.False(vm.HasError); Assert.True(vm.HasNotices);
        Assert.Equal("provider access denied", vm.Notices);
        Assert.Equal("0%", vm.MemoryUsageValue);
        Assert.Equal($"{8d:0.00}", vm.FreeMemoryNumber);
    }
}
