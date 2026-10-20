using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Joins independent Windows battery sensors by instance, preserving missing values and source failures.</summary>
internal static class BatteryTelemetryReader
{
    private const uint RelativeCapacity = 0x40000000;

    public static BatteryTelemetrySnapshot Read(IBatteryWmi wmi)
    {
        var diagnostics = new List<string>();
        var sources = new Dictionary<string, Dictionary<string, IReadOnlyDictionary<string, object?>>>();
        foreach (var className in new[] { "BatteryStaticData", "BatteryFullChargedCapacity", "BatteryStatus", "BatteryCycleCount", "BatteryTemperature" })
        {
            var rows = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in wmi.Query(@"root\wmi", $"SELECT * FROM {className} WHERE Active=TRUE"))
                {
                    if (row.GetValueOrDefault("InstanceName") is not string { Length: > 0 } instance)
                        throw new UtilityOperationException("A battery sensor has no instance identity.");
                    if (!rows.TryAdd(instance, row))
                        throw new UtilityOperationException("The battery sensor returned ambiguous instance identities.");
                }
            }
            catch (Exception ex) when (ex is UtilityOperationException or UnauthorizedAccessException)
            {
                rows.Clear();
                diagnostics.Add($"{className}: {ex.Message}");
            }
            sources.Add(className, rows);
        }

        var batteries = new List<BatteryTelemetry>();
        foreach (var instance in sources.Values.SelectMany(source => source.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var info = sources["BatteryStaticData"].GetValueOrDefault(instance);
            var status = sources["BatteryStatus"].GetValueOrDefault(instance);
            var capabilities = Number(info, "Capabilities");
            // Absolute energy and power units are established only by the battery's capabilities.
            var absolute = capabilities.HasValue && (capabilities.Value & RelativeCapacity) == 0;
            var designed = Positive(Number(info, "DesignedCapacity"));
            var full = Positive(Number(sources["BatteryFullChargedCapacity"].GetValueOrDefault(instance), "FullChargedCapacity"));
            var temperature = Positive(Number(sources["BatteryTemperature"].GetValueOrDefault(instance), "Temperature"));
            batteries.Add(new(instance)
            {
                Name = info?.GetValueOrDefault("DeviceName") as string ?? "",
                Manufacturer = info?.GetValueOrDefault("ManufactureName") as string ?? "",
                DesignCapacityWh = absolute ? designed / 1000d : null,
                FullChargeCapacityWh = absolute ? full / 1000d : null,
                RemainingEnergyWh = absolute ? Number(status, "RemainingCapacity") / 1000d : null,
                HealthPercent = designed.HasValue && full.HasValue ? 100d * full.Value / designed.Value : null,
                CycleCount = Positive(Number(sources["BatteryCycleCount"].GetValueOrDefault(instance), "CycleCount")),
                TemperatureCelsius = temperature / 10d - 273.15,
                Voltage = Positive(Number(status, "Voltage")) / 1000d,
                ChargePowerWatts = absolute ? Rate(status, "ChargeRate") / 1000d : null,
                DischargePowerWatts = absolute ? Rate(status, "DischargeRate") / 1000d : null,
                PowerOnline = status?.GetValueOrDefault("PowerOnline") as bool?,
                Charging = status?.GetValueOrDefault("Charging") as bool?,
                Discharging = status?.GetValueOrDefault("Discharging") as bool?,
                Critical = status?.GetValueOrDefault("Critical") as bool?
            });
        }
        return new(DateTimeOffset.Now, batteries, diagnostics);
    }

    /// <summary>Rejects WMI unknown sentinels and invalid numeric shapes without converting them into real measurements.</summary>
    private static uint? Number(IReadOnlyDictionary<string, object?>? row, string key) => row?.GetValueOrDefault(key) switch
    {
        uint value when value != uint.MaxValue => value,
        int value when value >= 0 => (uint)value,
        long value when value >= 0 && value < uint.MaxValue => (uint)value,
        ulong value when value < uint.MaxValue => (uint)value,
        ushort value => value,
        byte value => value,
        _ => null
    };

    private static uint? Positive(uint? value) => value is > 0 ? value : null;
    private static uint? Rate(IReadOnlyDictionary<string, object?>? row, string key) =>
        Number(row, key) is { } value && value != 0x80000000 ? value : null;
}
