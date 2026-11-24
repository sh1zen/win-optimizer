using System.Collections.Immutable;

namespace WinOptimizer.Core.Features.Utilities;

/// <summary>Distinguishes installable OEM support from firmware restrictions and discovery failures.</summary>
public enum BatteryControlIssue
{
    None, InterfaceUnavailable, FirmwareUnsupported, FirmwareLocked, AccessDenied, DetectionFailed, NoProvider
}

public enum BatterySupportSetupKind { DriverAndUtility, OemUtility, Firmware }

/// <summary>Official installation and usage routes; package availability must be checked for the exact model.</summary>
public sealed record BatterySupportSetup(string PackageName, Uri InstallUri, Uri HelpUri, BatterySupportSetupKind Kind);

/// <summary>Firmware-supported restart range for one stop limit, with an aligned maximum.</summary>
public sealed record BatteryResumeThresholds(int Minimum, int Maximum, int Step, bool SupportsStandardCharging = false)
{
    public bool Contains(int percentage) => Step > 0 && percentage >= Minimum && percentage <= Maximum &&
        (percentage - Minimum) % Step == 0;
}

/// <summary>Firmware-confirmed charge limits; an empty list means direct control is unavailable.</summary>
public sealed record BatteryChargeState(string Manufacturer, string Model, bool HasBattery, int? ChargePercent,
    string Provider, IReadOnlyList<int> SupportedLimits, int? ActiveLimit, string Diagnostic, Uri HelpUri)
{
    public bool CanApply => HasBattery && SupportedLimits.Count > 0;
    public BatteryControlIssue Issue { get; init; }
    public BatterySupportSetup? Setup { get; init; }
    public BatteryTelemetrySnapshot? Telemetry { get; init; }
    /// <summary>Windows reports that the battery is still charging above the firmware's reported limit.</summary>
    public bool IsChargingAboveLimit { get; init; }
    public int? ActiveResumeThreshold { get; init; }
    public IReadOnlyDictionary<int, BatteryResumeThresholds> ResumeThresholds { get; init; } =
        ImmutableDictionary<int, BatteryResumeThresholds>.Empty;
}

public interface IBatteryChargeService
{
    Task<BatteryChargeState> ReadAsync();
    Task<BatteryChargeState> SetLimitAsync(int percentage);
    Task<BatteryChargeState> SetLimitAsync(int percentage, int resumeThreshold);
}

/// <summary>Actionable operation failures shared by the dedicated utility tools.</summary>
public sealed class UtilityOperationException(string message, Exception? inner = null) : Exception(message, inner);
