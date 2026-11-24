using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class BatteryChargeServiceTests
{
    private readonly Mock<IApplicationModeService> _mode = new();
    public BatteryChargeServiceTests() => _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());

    [Theory]
    [InlineData(60)]
    [InlineData(70)]
    [InlineData(80)]
    [InlineData(90)]
    public async Task DellDiscoversRangesWritesInOrderAndConfirmsReadback(int percentage)
    {
        var wmi = new FirmwareFake("Dell Inc.");
        var provider = new DellBatteryFirmware(wmi);
        var service = new BatteryChargeService(wmi, [provider], _mode.Object);
        var before = await service.ReadAsync();
        Assert.Contains(80, before.SupportedLimits); Assert.DoesNotContain(54, before.SupportedLimits);
        Assert.Equal(100, before.ActiveLimit); Assert.Empty(wmi.Writes);
        Assert.Equal(BatteryControlIssue.None, before.Issue);
        Assert.Contains(percentage, before.SupportedLimits);
        var actual = await service.SetLimitAsync(percentage);
        Assert.Equal(percentage, actual.ActiveLimit);
        Assert.Equal(new[] { "CustomChargeStart=50", $"CustomChargeStop={percentage}", "PrimaryBattChargeCfg=Custom" }, wmi.Writes);
        await service.SetLimitAsync(100);
        Assert.Equal("PrimaryBattChargeCfg=Standard", wmi.Writes.Last());
    }

    [Fact]
    public async Task DellAppliesTheSelectedRestartThresholdAndConfirmsBothValues()
    {
        var wmi = new FirmwareFake("Dell");
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi)], _mode.Object);
        await service.SetLimitAsync(60);
        wmi.Writes.Clear();

        var before = await service.ReadAsync();
        Assert.Equal(new BatteryResumeThresholds(50, 75, 1), before.ResumeThresholds[80]);
        Assert.Equal(50, before.ActiveResumeThreshold);
        Assert.Empty(wmi.Writes);
        var delayed = await service.SetLimitAsync(80, 70);
        Assert.Equal(80, delayed.ActiveLimit);
        Assert.Equal(70, delayed.ActiveResumeThreshold);
        Assert.Equal(new[] { "CustomChargeStop=80", "CustomChargeStart=70", "PrimaryBattChargeCfg=Custom" }, wmi.Writes);

        Assert.Equal(60, (await service.SetLimitAsync(80, 60)).ActiveResumeThreshold);
        Assert.Equal(50, (await service.SetLimitAsync(70, 50)).ActiveResumeThreshold);
        Assert.Equal(new[] { "CustomChargeStart=50", "CustomChargeStop=70", "PrimaryBattChargeCfg=Custom" }, wmi.Writes.TakeLast(3));
        var fullWithRestart = await service.SetLimitAsync(100, 90);
        Assert.Equal(100, fullWithRestart.ActiveLimit);
        Assert.Equal(90, fullWithRestart.ActiveResumeThreshold);
        Assert.Equal("PrimaryBattChargeCfg=Custom", wmi.Writes.Last());
        Assert.Null((await service.SetLimitAsync(100)).ActiveResumeThreshold);
        Assert.Equal("PrimaryBattChargeCfg=Standard", wmi.Writes.Last());
    }

    [Theory]
    [InlineData(55, 45)]
    [InlineData(79, 69)]
    [InlineData(80, 80)]
    [InlineData(100, 100)]
    public async Task DellRejectsRestartThresholdsOutsideTheAdvertisedRangeAndIncrement(int percentage, int restart)
    {
        var wmi = new FirmwareFake("Dell");
        wmi.DellStart["ScalarIncrement"] = 5;
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi)], _mode.Object);
        var state = await service.ReadAsync();
        Assert.Contains(percentage, state.SupportedLimits);
        Assert.True(state.ResumeThresholds[percentage].Contains(50));
        Assert.False(state.ResumeThresholds[percentage].Contains(restart));
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(percentage, restart));
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task DellKeepsCustomChargingAtOneHundredWhenStandardModeIsUnavailable()
    {
        var wmi = new FirmwareFake("Dell");
        wmi.DellMode["PossibleValue"] = new[] { "Custom" };
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi)], _mode.Object);
        var state = await service.ReadAsync();
        Assert.False(state.ResumeThresholds[100].SupportsStandardCharging);
        Assert.Equal(50, (await service.SetLimitAsync(100)).ActiveResumeThreshold);
        Assert.Equal("PrimaryBattChargeCfg=Custom", wmi.Writes.Last());
    }

    [Fact]
    public async Task DellRequiresStandardReadbackWhenRevertingFromCustomChargingAtOneHundred()
    {
        var wmi = new FirmwareFake("Dell");
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi)], _mode.Object);
        Assert.True((await service.ReadAsync()).ResumeThresholds[100].SupportsStandardCharging);
        await service.SetLimitAsync(100, 80);
        wmi.AcceptChargingMode = false;

        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(100));
        Assert.Equal(80, (await service.ReadAsync()).ActiveResumeThreshold);
    }

    [Fact]
    public async Task DellRejectsSuccessIfFirmwareConfirmsOnlyTheStopThreshold()
    {
        var wmi = new FirmwareFake("Dell") { AcceptRestartThreshold = false };
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi)], _mode.Object);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80, 70));
        var state = await service.ReadAsync();
        Assert.Equal(80, state.ActiveLimit);
        Assert.Equal(50, state.ActiveResumeThreshold);
    }

    [Fact]
    public async Task HpOffersOnlyItsAdvertisedFixedModes()
    {
        var wmi = new FirmwareFake("HP");
        var service = new BatteryChargeService(wmi, [new HpBatteryFirmware(wmi)], _mode.Object);
        var state = await service.ReadAsync();
        Assert.Equal(new[] { 80, 100 }, state.SupportedLimits);
        Assert.Empty(state.ResumeThresholds);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80, 70));
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(70));
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(85));
        Assert.Empty(wmi.Writes);
        Assert.Equal(80, (await service.SetLimitAsync(80)).ActiveLimit);
        Assert.Equal("Battery Health Manager=Maximize my battery health", Assert.Single(wmi.Writes));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(70)]
    [InlineData(80)]
    [InlineData(90)]
    public async Task AsusUsesAnExistingDriverAndRequiresConfirmedReadback(int percentage)
    {
        var wmi = new FirmwareFake("ASUSTeK COMPUTER INC.");
        var device = new ChargeDeviceFake();
        var service = new BatteryChargeService(wmi, [new AsusBatteryFirmware(device)], _mode.Object);
        Assert.Contains(percentage, (await service.ReadAsync()).SupportedLimits);
        Assert.Equal(percentage, (await service.SetLimitAsync(percentage)).ActiveLimit);
        device.AcceptWrites = false;
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(95));
        Assert.Equal(percentage, (await service.ReadAsync()).ActiveLimit);
    }

    private sealed class ChargeDeviceFake : IAsusChargeDevice
    {
        public bool AcceptWrites { get; set; } = true;
        private int _current = 100;
        public int? ReadLimit() => _current;
        public void SetLimit(int percentage) { if (AcceptWrites) _current = percentage; }
    }

    [Fact]
    public async Task MissingBatteryAndUnknownOemNeverClaimControl()
    {
        var wmi = new FirmwareFake("Lenovo");
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi), new HpBatteryFirmware(wmi)], _mode.Object);
        var state = await service.ReadAsync();
        Assert.False(state.CanApply); Assert.Null(state.ActiveLimit);
        Assert.Equal(BatteryControlIssue.NoProvider, state.Issue);
        Assert.NotNull(state.Setup);
        Assert.Equal("support.lenovo.com", state.HelpUri.Host);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80));
        wmi.HasBattery = false;
        Assert.False((await service.ReadAsync()).HasBattery);
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task ReadOnlyAndMissingFirmwareAttributesDisableDirectControl()
    {
        var wmi = new FirmwareFake("Dell");
        wmi.DellMode["ReadOnly"] = true;
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi)], _mode.Object);
        var locked = await service.ReadAsync();
        Assert.False(locked.CanApply);
        Assert.Equal(BatteryControlIssue.FirmwareLocked, locked.Issue);
        wmi.DellMode.Clear();
        Assert.False((await service.ReadAsync()).CanApply);
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ModeGuardsPreventFirmwareWrites(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        var wmi = new FirmwareFake("Dell");
        var service = new BatteryChargeService(wmi, [new DellBatteryFirmware(wmi)], _mode.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetLimitAsync(80));
        Assert.Empty(wmi.Writes);
    }

    /// <summary>Emulates WMI attributes and their normal Set methods without contacting hardware.</summary>
    private sealed class FirmwareFake(string manufacturer) : IBatteryWmi
    {
        public bool HasBattery { get; set; } = true;
        public bool AcceptRestartThreshold { get; init; } = true;
        public bool AcceptChargingMode { get; set; } = true;
        public List<string> Writes { get; } = [];
        public Dictionary<string, object?> DellMode { get; } = new() { ["ReadOnly"] = false, ["PossibleValue"] = new[] { "Standard", "Custom" }, ["CurrentValue"] = "Standard" };
        private readonly Dictionary<string, object?> _stop = new() { ["ReadOnly"] = false, ["CurrentValue"] = 100, ["LowerBound"] = 55, ["UpperBound"] = 100, ["ScalarIncrement"] = 1 };
        public Dictionary<string, object?> DellStart { get; } = new() { ["ReadOnly"] = false, ["CurrentValue"] = 50, ["LowerBound"] = 50, ["UpperBound"] = 95 };
        private readonly Dictionary<string, object?> _hp = new() { ["IsReadOnly"] = 0, ["PossibleValues"] = new[] { "Maximize my battery health", "Maximize my battery duration" }, ["CurrentValue"] = "Maximize my battery duration" };
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string query)
        {
            if (scope == @"root\wmi" && new[] { "BatteryStaticData", "BatteryFullChargedCapacity", "BatteryStatus", "BatteryCycleCount", "BatteryTemperature" }
                .Any(name => query == $"SELECT * FROM {name} WHERE Active=TRUE")) return [];
            if (query.Contains("Win32_ComputerSystem")) return [new Dictionary<string, object?> { ["Manufacturer"] = manufacturer, ["Model"] = "Test laptop" }];
            if (query.Contains("Win32_Battery")) return HasBattery ? [new Dictionary<string, object?> { ["EstimatedChargeRemaining"] = 72 }] : [];
            if (query.Contains("PrimaryBattChargeCfg")) return [DellMode];
            if (query.Contains("CustomChargeStart")) return [DellStart];
            if (query.Contains("CustomChargeStop")) return [_stop];
            if (query.Contains("HP_BIOSEnumeration")) return [_hp];
            throw new InvalidOperationException("Unexpected test query: " + query);
        }
        public IReadOnlyDictionary<string, object?> Invoke(string scope, string className, string method,
            IReadOnlyDictionary<string, object> arguments, string? resultProperty, string? instanceName = null)
        {
            if (scope == HpBatteryFirmware.Scope)
            {
                Assert.Equal("SetBIOSSetting", method); Assert.Equal("<utf-16/>", arguments["Password"]); Assert.Equal("Return", resultProperty);
                _hp["CurrentValue"] = arguments["Value"]; Writes.Add(arguments["Name"] + "=" + arguments["Value"]);
            }
            else
            {
                Assert.Equal(DellBatteryFirmware.Scope, scope); Assert.Equal("SetAttribute", method); Assert.Equal("Status", resultProperty);
                Assert.Equal(0u, arguments["SecType"]);
                var name = (string)arguments["AttributeName"]; var value = (string)arguments["AttributeValue"];
                if (name == "CustomChargeStart")
                    Assert.True(Convert.ToInt32(_stop["CurrentValue"]) - int.Parse(value) >= 5);
                else if (name == "CustomChargeStop")
                    Assert.True(int.Parse(value) - Convert.ToInt32(DellStart["CurrentValue"]) >= 5);
                var destination = name == "PrimaryBattChargeCfg" ? DellMode : name == "CustomChargeStop" ? _stop : DellStart;
                if ((name != "CustomChargeStart" || AcceptRestartThreshold) &&
                    (name != "PrimaryBattChargeCfg" || AcceptChargingMode))
                    destination["CurrentValue"] = value;
                Writes.Add(name + "=" + value);
            }
            return new Dictionary<string, object?> { ["Status"] = 0u, ["Return"] = 0u };
        }
    }
}
