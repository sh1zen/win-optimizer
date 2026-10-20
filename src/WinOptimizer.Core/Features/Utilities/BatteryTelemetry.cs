namespace WinOptimizer.Core.Features.Utilities;

/// <summary>Dated local sensor readings; diagnostics describe individual unavailable WMI sources.</summary>
public sealed record BatteryTelemetrySnapshot(DateTimeOffset CapturedAt, IReadOnlyList<BatteryTelemetry> Batteries,
    IReadOnlyList<string> Diagnostics);

/// <summary>One physical battery, with SI units and null for readings the firmware cannot supply.</summary>
public sealed record BatteryTelemetry(string InstanceName)
{
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public double? DesignCapacityWh { get; init; }
    public double? FullChargeCapacityWh { get; init; }
    public double? RemainingEnergyWh { get; init; }
    public double? HealthPercent { get; init; }
    public double? WearPercent => HealthPercent is { } health ? Math.Max(0, 100 - health) : null;
    public uint? CycleCount { get; init; }
    public double? TemperatureCelsius { get; init; }
    public double? Voltage { get; init; }
    public double? ChargePowerWatts { get; init; }
    public double? DischargePowerWatts { get; init; }
    public bool? PowerOnline { get; init; }
    public bool? Charging { get; init; }
    public bool? Discharging { get; init; }
    public bool? Critical { get; init; }
    public double? CurrentAmperes => Voltage is > 0 && (Charging == true ^ Discharging == true)
        ? (Charging == true ? ChargePowerWatts : DischargePowerWatts) / Voltage : null;
    public TimeSpan? EstimatedRuntime => Discharging == true && Charging != true &&
        DischargePowerWatts is > 0 && RemainingEnergyWh is { } energy
        ? TimeSpan.FromHours(energy / DischargePowerWatts.Value) : null;
}
