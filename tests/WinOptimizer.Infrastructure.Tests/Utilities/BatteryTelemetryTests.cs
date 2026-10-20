using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Exercises production discovery with deterministic Windows sensor data and no hardware access.</summary>
public sealed class BatteryTelemetryTests
{
    [Fact]
    public async Task NormalDiscoveryPublishesHealthPowerAndRuntimeInSiUnits()
    {
        var wmi = new SensorFake();
        var before = DateTimeOffset.Now;
        var service = new BatteryChargeService(wmi, [], Mock.Of<IApplicationModeService>());
        var state = await service.ReadAsync();
        Assert.False(state.CanApply);
        var snapshot = Assert.IsType<BatteryTelemetrySnapshot>(state.Telemetry);
        Assert.InRange(snapshot.CapturedAt, before, DateTimeOffset.Now);
        Assert.Empty(snapshot.Diagnostics);
        var battery = Assert.Single(snapshot.Batteries);
        Assert.Equal("Test battery", battery.Name);
        Assert.Equal("Test manufacturer", battery.Manufacturer);
        Assert.Equal(60, battery.DesignCapacityWh);
        Assert.Equal(42, battery.FullChargeCapacityWh);
        Assert.Equal(21, battery.RemainingEnergyWh);
        Assert.Equal(70, battery.HealthPercent);
        Assert.Equal(30, battery.WearPercent);
        Assert.Equal(55u, battery.CycleCount);
        Assert.Equal(30.05, battery.TemperatureCelsius!.Value, 5);
        Assert.Equal(14, battery.Voltage);
        Assert.Equal(0, battery.ChargePowerWatts);
        Assert.Equal(10.5, battery.DischargePowerWatts);
        Assert.Equal(.75, battery.CurrentAmperes);
        Assert.Equal(TimeSpan.FromHours(2), battery.EstimatedRuntime);
        Assert.False(battery.PowerOnline);
        Assert.True(battery.Discharging);
        Assert.False(battery.Critical);
        Assert.Equal(5, wmi.SensorQueries);

        wmi.Rows["BatteryStatus"][0]["RemainingCapacity"] = 10500u;
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single((await service.ReadAsync()).Telemetry!.Batteries).EstimatedRuntime);
    }

    [Fact]
    public void ChargingAndIdleNeverInventDischargeRuntimeOrZeroCurrent()
    {
        var wmi = new SensorFake();
        var status = wmi.Rows["BatteryStatus"][0];
        status["Charging"] = true; status["Discharging"] = false;
        status["PowerOnline"] = true; status["ChargeRate"] = 21000u; status["DischargeRate"] = 0u;
        var battery = Assert.Single(BatteryTelemetryReader.Read(wmi).Batteries);
        Assert.True(battery.Charging); Assert.True(battery.PowerOnline);
        Assert.Equal(1.5, battery.CurrentAmperes); Assert.Null(battery.EstimatedRuntime);
        status["Charging"] = false;
        battery = Assert.Single(BatteryTelemetryReader.Read(wmi).Batteries);
        Assert.Null(battery.CurrentAmperes); Assert.Null(battery.EstimatedRuntime);
        status["Charging"] = true; status["Discharging"] = true;
        battery = Assert.Single(BatteryTelemetryReader.Read(wmi).Batteries);
        Assert.Null(battery.CurrentAmperes); Assert.Null(battery.EstimatedRuntime);
    }

    [Theory]
    [InlineData(0xC0000000u)]
    [InlineData(null)]
    public void RelativeOrUnestablishedUnitsNeverBecomeWattsOrWattHours(uint? capabilities)
    {
        var wmi = new SensorFake();
        wmi.Rows["BatteryStaticData"][0]["Capabilities"] = capabilities;
        var battery = Assert.Single(BatteryTelemetryReader.Read(wmi).Batteries);
        Assert.Null(battery.DesignCapacityWh); Assert.Null(battery.FullChargeCapacityWh);
        Assert.Null(battery.RemainingEnergyWh); Assert.Null(battery.ChargePowerWatts);
        Assert.Null(battery.DischargePowerWatts); Assert.Null(battery.CurrentAmperes); Assert.Null(battery.EstimatedRuntime);
        Assert.Equal(70, battery.HealthPercent); Assert.Equal(14, battery.Voltage);
    }

    [Fact]
    public void UnsupportedAndUnknownValuesRemainUnavailableWhileRealZeroEnergyIsRetained()
    {
        var wmi = new SensorFake();
        wmi.Rows["BatteryStaticData"][0]["DesignedCapacity"] = 0u;
        wmi.Rows["BatteryFullChargedCapacity"][0]["FullChargedCapacity"] = uint.MaxValue;
        wmi.Rows["BatteryCycleCount"][0]["CycleCount"] = 0u;
        wmi.Rows["BatteryTemperature"][0]["Temperature"] = 0u;
        var status = wmi.Rows["BatteryStatus"][0];
        status["Voltage"] = uint.MaxValue; status["ChargeRate"] = int.MinValue;
        status["DischargeRate"] = 0x80000000u; status["RemainingCapacity"] = 0u;
        var battery = Assert.Single(BatteryTelemetryReader.Read(wmi).Batteries);
        Assert.Null(battery.DesignCapacityWh); Assert.Null(battery.FullChargeCapacityWh);
        Assert.Null(battery.HealthPercent); Assert.Null(battery.WearPercent); Assert.Null(battery.CycleCount);
        Assert.Null(battery.TemperatureCelsius); Assert.Null(battery.Voltage); Assert.Null(battery.ChargePowerWatts);
        Assert.Null(battery.DischargePowerWatts); Assert.Null(battery.CurrentAmperes); Assert.Null(battery.EstimatedRuntime);
        Assert.Equal(0, battery.RemainingEnergyWh);
        status["RemainingCapacity"] = uint.MaxValue;
        Assert.Null(Assert.Single(BatteryTelemetryReader.Read(wmi).Batteries).RemainingEnergyWh);
    }

    [Fact]
    public async Task OptionalSensorFailureKeepsFirmwareStateAndExposesSourceDiagnostic()
    {
        var wmi = new SensorFake { FailedSource = "BatteryTemperature" };
        var state = await new BatteryChargeService(wmi, [], Mock.Of<IApplicationModeService>()).ReadAsync();
        Assert.True(state.HasBattery); Assert.Equal(50, state.ChargePercent);
        Assert.Equal(BatteryControlIssue.NoProvider, state.Issue);
        Assert.Contains("BatteryTemperature", Assert.Single(state.Telemetry!.Diagnostics));
        Assert.Null(Assert.Single(state.Telemetry.Batteries).TemperatureCelsius);
        Assert.Equal(21, state.Telemetry.Batteries[0].RemainingEnergyWh);
    }

    [Fact]
    public void DifferentBatteriesJoinByIdentityEvenWhenSourcesAreReorderedOrMissing()
    {
        var wmi = new SensorFake();
        wmi.Rows["BatteryStaticData"].Add(new() { ["InstanceName"] = "second", ["DeviceName"] = "Second battery", ["Capabilities"] = 0x80000000u, ["DesignedCapacity"] = 80000u });
        wmi.Rows["BatteryFullChargedCapacity"].Insert(0, new() { ["InstanceName"] = "SECOND", ["FullChargedCapacity"] = 64000u });
        var snapshot = BatteryTelemetryReader.Read(wmi);
        Assert.Equal(2, snapshot.Batteries.Count);
        var first = snapshot.Batteries.Single(battery => battery.Name == "Test battery");
        var second = snapshot.Batteries.Single(battery => battery.Name == "Second battery");
        Assert.Equal(70, first.HealthPercent); Assert.Equal(80, second.HealthPercent);
        Assert.Null(second.Voltage); Assert.Null(second.TemperatureCelsius); Assert.Null(second.RemainingEnergyWh);
    }

    [Fact]
    public async Task MissingSensorsAndMissingBatteryAreDifferentStates()
    {
        var wmi = new SensorFake();
        wmi.Rows.Clear();
        var service = new BatteryChargeService(wmi, [], Mock.Of<IApplicationModeService>());
        var present = await service.ReadAsync();
        Assert.True(present.HasBattery); Assert.Empty(present.Telemetry!.Batteries);
        wmi.HasBattery = false;
        var missing = await service.ReadAsync();
        Assert.False(missing.HasBattery); Assert.Null(missing.Telemetry);
        Assert.Equal(5, wmi.SensorQueries);
    }

    [Fact]
    public void AmbiguousSensorsProduceDiagnosticsInsteadOfMergingBatteries()
    {
        var wmi = new SensorFake();
        wmi.Rows["BatteryTemperature"].Add(new(wmi.Rows["BatteryTemperature"][0]));
        var snapshot = BatteryTelemetryReader.Read(wmi);
        Assert.Contains("BatteryTemperature", Assert.Single(snapshot.Diagnostics));
        Assert.Null(Assert.Single(snapshot.Batteries).TemperatureCelsius);
    }

    /// <summary>Supplies the same class boundaries used in local production WMI queries.</summary>
    private sealed class SensorFake : IBatteryWmi
    {
        public bool HasBattery { get; set; } = true;
        public string? FailedSource { get; init; }
        public int SensorQueries { get; private set; }
        public Dictionary<string, List<Dictionary<string, object?>>> Rows { get; } = new()
        {
            ["BatteryStaticData"] = [new() { ["InstanceName"] = "first", ["DeviceName"] = "Test battery", ["ManufactureName"] = "Test manufacturer", ["Capabilities"] = 0x80000000u, ["DesignedCapacity"] = 60000u }],
            ["BatteryFullChargedCapacity"] = [new() { ["InstanceName"] = "first", ["FullChargedCapacity"] = 42000u }],
            ["BatteryStatus"] = [new() { ["InstanceName"] = "first", ["RemainingCapacity"] = 21000u, ["Voltage"] = 14000u, ["ChargeRate"] = 0u, ["DischargeRate"] = 10500u, ["PowerOnline"] = false, ["Charging"] = false, ["Discharging"] = true, ["Critical"] = false }],
            ["BatteryCycleCount"] = [new() { ["InstanceName"] = "first", ["CycleCount"] = 55u }],
            ["BatteryTemperature"] = [new() { ["InstanceName"] = "first", ["Temperature"] = 3032u }]
        };

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string query)
        {
            if (query.Contains("Win32_ComputerSystem")) return [new Dictionary<string, object?> { ["Manufacturer"] = "Test OEM", ["Model"] = "Test laptop" }];
            if (query.Contains("Win32_Battery")) return HasBattery ? [new Dictionary<string, object?> { ["EstimatedChargeRemaining"] = 50 }] : [];
            Assert.Equal(@"root\wmi", scope);
            SensorQueries++;
            var className = query.Split(' ')[3];
            Assert.Equal($"SELECT * FROM {className} WHERE Active=TRUE", query);
            if (className == FailedSource) throw new UnauthorizedAccessException("Test sensor access denied.");
            return Rows.GetValueOrDefault(className)?.Select(row => (IReadOnlyDictionary<string, object?>)row).ToArray() ?? [];
        }

        public IReadOnlyDictionary<string, object?> Invoke(string scope, string className, string method,
            IReadOnlyDictionary<string, object> arguments, string? resultProperty, string? instanceName = null) =>
            throw new InvalidOperationException("Sensor reads must never invoke firmware methods.");
    }
}
