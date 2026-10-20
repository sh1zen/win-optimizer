namespace WinOptimizer.Core.Features.Utilities;

/// <summary>Distinguishes installable OEM support from firmware restrictions and discovery failures.</summary>
public enum BatteryControlIssue
{
    None, InterfaceUnavailable, FirmwareUnsupported, FirmwareLocked, AccessDenied, DetectionFailed, NoProvider
}

public enum BatterySupportSetupKind { DriverAndUtility, OemUtility, Firmware }

/// <summary>Official installation and usage routes; package availability must be checked for the exact model.</summary>
public sealed record BatterySupportSetup(string PackageName, Uri InstallUri, Uri HelpUri, BatterySupportSetupKind Kind);

/// <summary>Firmware-confirmed charge limits; an empty list means direct control is unavailable.</summary>
public sealed record BatteryChargeState(string Manufacturer, string Model, bool HasBattery, int? ChargePercent,
    string Provider, IReadOnlyList<int> SupportedLimits, int? ActiveLimit, string Diagnostic, Uri HelpUri)
{
    public bool CanApply => HasBattery && SupportedLimits.Count > 0;
    public BatteryControlIssue Issue { get; init; }
    public BatterySupportSetup? Setup { get; init; }
    public BatteryTelemetrySnapshot? Telemetry { get; init; }
}

public interface IBatteryChargeService
{
    Task<BatteryChargeState> ReadAsync();
    Task<BatteryChargeState> SetLimitAsync(int percentage);
}

/// <summary>Actionable operation failures shared by the dedicated utility tools.</summary>
public sealed class UtilityOperationException(string message, Exception? inner = null) : Exception(message, inner);
