using System.Text.Json;
using WinOptimizer.Core.Features.Extensions;
using Xunit;

namespace WinOptimizer.Core.Tests.Extensions;

public sealed class SystemMonitorOptionsTests
{
    [Fact]
    public void DefaultsAreDisabledAndCompactAndOldPreferencesRetainNewDefaults()
    {
        var options = JsonSerializer.Deserialize<SystemMonitorOptions>("{}");
        Assert.NotNull(options);
        Assert.False(options.Enabled);
        Assert.False(options.StartWithWindows);
        Assert.True(options.IsCompact);
        Assert.True(options.AlwaysOnTop);
        Assert.Equal(2, options.RefreshIntervalSeconds);
        Assert.Equal(100, options.ScalePercent);
        Assert.True(options.ShowTemperatures);
        Assert.True(options.ShowFans);
        Assert.True(options.ShowMemory);
        Assert.True(options.ShowCpu);
        Assert.True(options.ShowNetwork);
        Assert.All(Enum.GetValues<SystemMonitorMetricKind>(), kind => Assert.False(options.IsMetricVisible(kind)));
        options.Validate();
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(3)] [InlineData(100)]
    public void UnsupportedIntervalsAreRejected(int seconds) =>
        Assert.Throws<ArgumentException>(() => new SystemMonitorOptions { RefreshIntervalSeconds = seconds }.Validate());

    [Fact]
    public void InvalidThemeIsRejected() => Assert.Throws<ArgumentException>(() =>
        new SystemMonitorOptions { Theme = (SystemMonitorTheme)999 }.Validate());

    [Theory]
    [InlineData(50)] [InlineData(75)] [InlineData(100)] [InlineData(135)] [InlineData(200)]
    public void SupportedScalesAreAccepted(double percent) =>
        new SystemMonitorOptions { ScalePercent = percent }.Validate();

    [Theory]
    [InlineData(0)] [InlineData(49)] [InlineData(201)]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void InvalidScalesAreRejected(double percent) => Assert.Throws<ArgumentException>(() =>
        new SystemMonitorOptions { ScalePercent = percent }.Validate());

    [Fact]
    public void AllPreferencesRoundTrip()
    {
        var options = new SystemMonitorOptions { Enabled = true, StartWithWindows = true, IsCompact = false, AlwaysOnTop = false,
            Theme = SystemMonitorTheme.Dark, ScalePercent = 135, RefreshIntervalSeconds = 5,
            ShowTemperatures = false, ShowFans = false, ShowMemory = false, ShowCpu = false, ShowNetwork = false,
            ShowGpuUsage = true, ShowGpuMemory = true, ShowClockSpeed = true, ShowPower = true,
            ShowVoltage = true, ShowStorageActivity = true, ShowStorageThroughput = true };
        Assert.Equal(options, JsonSerializer.Deserialize<SystemMonitorOptions>(JsonSerializer.Serialize(options)));
        Assert.All(Enum.GetValues<SystemMonitorMetricKind>(), kind => Assert.True(options.IsMetricVisible(kind)));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AutomaticLaunchRequiresBothChoices(bool enabled, bool startup, bool expected) =>
        Assert.Equal(expected, SystemMonitorLaunch.ShouldStart(new() { Enabled = enabled, StartWithWindows = startup }));

    [Fact]
    public void LaunchArgumentMatchesOnlyACompleteArgument()
    {
        Assert.True(SystemMonitorLaunch.IsAutomatic(["app.exe", SystemMonitorLaunch.Argument]));
        Assert.True(SystemMonitorLaunch.IsAutomatic(["--SYSMONITOR-STARTUP"]));
        Assert.False(SystemMonitorLaunch.IsAutomatic([]));
        Assert.False(SystemMonitorLaunch.IsAutomatic(["--sysmonitor-startup-extra"]));
    }
}
