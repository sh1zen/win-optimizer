using System.Globalization;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

internal sealed record FirmwareChargeState(string Provider, IReadOnlyList<int> Limits, int? ActiveLimit,
    BatteryControlIssue Issue = BatteryControlIssue.None);

/// <summary>One manufacturer's charge-control protocol; construction performs no hardware calls.</summary>
internal interface IBatteryFirmwareProvider
{
    bool Matches(string manufacturer);
    FirmwareChargeState Read();
    void SetLimit(int percentage);
}

/// <summary>Uses Dell's agentless BIOS attribute API and validates the discovered writable ranges.</summary>
internal sealed class DellBatteryFirmware(IBatteryWmi wmi) : IBatteryFirmwareProvider
{
    internal const string Scope = @"root\dcim\sysman\biosattributes";
    public bool Matches(string manufacturer) => manufacturer.Contains("Dell", StringComparison.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, object?>? Attribute(string type, string name) => wmi.Query(Scope,
        $"SELECT * FROM {type} WHERE AttributeName='{name}'").SingleOrDefault();
    private static bool Writable(IReadOnlyDictionary<string, object?>? attribute) => attribute != null &&
        attribute.TryGetValue("ReadOnly", out var readOnly) && readOnly is not null && !Convert.ToBoolean(readOnly);
    private static string[] Values(IReadOnlyDictionary<string, object?>? attribute) => attribute?.GetValueOrDefault("PossibleValue") as string[] ?? [];

    public FirmwareChargeState Read()
    {
        var mode = Attribute("EnumerationAttribute", "PrimaryBattChargeCfg");
        if (mode == null) return new("Dell BIOS WMI", [], null, BatteryControlIssue.InterfaceUnavailable);
        if (!Writable(mode)) return new("Dell BIOS WMI", [], null, BatteryControlIssue.FirmwareLocked);
        var limits = new List<int>();
        if (Values(mode).Contains("Standard", StringComparer.OrdinalIgnoreCase)) limits.Add(100);
        var stop = Attribute("IntegerAttribute", "CustomChargeStop");
        var start = Attribute("IntegerAttribute", "CustomChargeStart");
        if (Values(mode).Contains("Custom", StringComparer.OrdinalIgnoreCase) && stop != null && start != null && Writable(stop) && Writable(start) &&
            stop.GetValueOrDefault("LowerBound") is { } lower && stop.GetValueOrDefault("UpperBound") is { } upper &&
            start.GetValueOrDefault("LowerBound") is { } startLower && start.GetValueOrDefault("UpperBound") is { } startUpper)
        {
            var minimum = Math.Max(55, Math.Max(Convert.ToInt32(lower), Convert.ToInt32(startLower) + 5));
            var maximum = Math.Min(100, Convert.ToInt32(upper));
            var step = Math.Max(1, Convert.ToInt32(stop.GetValueOrDefault("ScalarIncrement") ?? 1));
            for (var value = minimum; value <= maximum; value++)
                if ((value - Convert.ToInt32(lower)) % step == 0 && Convert.ToInt32(startLower) <= Convert.ToInt32(startUpper)) limits.Add(value);
        }
        var currentMode = mode.GetValueOrDefault("CurrentValue") as string;
        int? active = string.Equals(currentMode, "Standard", StringComparison.OrdinalIgnoreCase) ? 100 :
            string.Equals(currentMode, "Custom", StringComparison.OrdinalIgnoreCase) && stop?.GetValueOrDefault("CurrentValue") is { } current
                ? Convert.ToInt32(current) : null;
        return new("Dell BIOS WMI", limits.Distinct().Order().ToArray(), active);
    }

    public void SetLimit(int percentage)
    {
        if (percentage == 100) { Write("PrimaryBattChargeCfg", "Standard"); return; }
        var start = Attribute("IntegerAttribute", "CustomChargeStart") ?? throw new UtilityOperationException("Dell custom charge start is unavailable.");
        var startValue = Math.Clamp(Convert.ToInt32(start.GetValueOrDefault("CurrentValue")),
            Convert.ToInt32(start["LowerBound"]), Math.Min(percentage - 5, Convert.ToInt32(start["UpperBound"])));
        Write("CustomChargeStart", startValue.ToString(CultureInfo.InvariantCulture));
        Write("CustomChargeStop", percentage.ToString(CultureInfo.InvariantCulture));
        Write("PrimaryBattChargeCfg", "Custom");
    }

    private void Write(string name, string value) => wmi.Invoke(Scope, "BIOSAttributeInterface", "SetAttribute",
        new Dictionary<string, object> { ["SecType"] = 0u, ["SecHndCount"] = 0u, ["SecHandle"] = Array.Empty<byte>(),
            ["AttributeName"] = name, ["AttributeValue"] = value }, "Status");
}

/// <summary>Enables HP's fixed 80% Battery Health Manager mode only when explicitly advertised.</summary>
internal sealed class HpBatteryFirmware(IBatteryWmi wmi) : IBatteryFirmwareProvider
{
    internal const string Scope = @"root\HP\InstrumentedBIOS";
    private const string Name = "Battery Health Manager";
    private const string Limited = "Maximize my battery health";
    private const string Full = "Maximize my battery duration";
    public bool Matches(string manufacturer) => manufacturer.Equals("HP", StringComparison.OrdinalIgnoreCase) ||
        manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, object?>? Setting() => wmi.Query(Scope,
        "SELECT * FROM HP_BIOSEnumeration WHERE Name='Battery Health Manager'").SingleOrDefault();

    public FirmwareChargeState Read()
    {
        var setting = Setting();
        if (setting == null) return new("HP Battery Health Manager", [], null, BatteryControlIssue.FirmwareUnsupported);
        if (!setting.TryGetValue("IsReadOnly", out var readOnly) || readOnly == null || Convert.ToBoolean(readOnly))
            return new("HP Battery Health Manager", [], null, BatteryControlIssue.FirmwareLocked);
        var values = setting.GetValueOrDefault("PossibleValues") as string[] ?? [];
        var limits = new List<int>();
        if (values.Contains(Limited, StringComparer.OrdinalIgnoreCase)) limits.Add(80);
        if (values.Contains(Full, StringComparer.OrdinalIgnoreCase)) limits.Add(100);
        var current = setting.GetValueOrDefault("CurrentValue") as string;
        return new("HP Battery Health Manager", limits, string.Equals(current, Limited, StringComparison.OrdinalIgnoreCase) ? 80 :
            string.Equals(current, Full, StringComparison.OrdinalIgnoreCase) ? 100 : null);
    }

    public void SetLimit(int percentage)
    {
        var value = percentage switch { 80 => Limited, 100 => Full, _ => throw new ArgumentOutOfRangeException(nameof(percentage)) };
        var values = Setting()?.GetValueOrDefault("PossibleValues") as string[] ?? [];
        var advertised = values.SingleOrDefault(candidate => candidate.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new UtilityOperationException("The requested HP charging mode is unavailable.");
        wmi.Invoke(Scope, "HP_BIOSSettingInterface", "SetBIOSSetting", new Dictionary<string, object>
            { ["Name"] = Name, ["Value"] = advertised, ["Password"] = "<utf-16/>" }, "Return");
    }
}
