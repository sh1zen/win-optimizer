using System.Globalization;
using System.Management;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;

namespace WinOptimizer.Infrastructure.Features.SystemInformation;

/// <summary>Collects one typed snapshot; each category retains its own availability and errors.</summary>
public sealed class WindowsSystemInformationService(ISystemInformationQuery query) : ISystemInformationService
{
    private SystemInformationSnapshot? _cachedSnapshot;

    /// <summary>Shares the completed snapshot for this singleton service's lifetime without querying Windows.</summary>
    public SystemInformationSnapshot? CachedSnapshot => Volatile.Read(ref _cachedSnapshot);

    private enum FieldKind { Text, Number, Bytes, Kilobytes, Megahertz, BitsPerSecond, Pixels, Percent, Date, Boolean }
    private sealed record Field(string Property, string Label, FieldKind Kind = FieldKind.Text);
    private sealed record Category(SystemInformationCategory Id, string Class, string Title, Field[] Fields);

    // Query definitions own selected properties, normalization and ordering together.
    private static readonly Category[] Categories =
    [
        new(SystemInformationCategory.Machine, "Win32_ComputerSystem", "Name",
            [new("Name", "ComputerName"), new("Manufacturer", "Manufacturer"), new("Model", "Model"),
             new("SystemType", "SystemType"), new("TotalPhysicalMemory", "TotalMemory", FieldKind.Bytes),
             new("NumberOfProcessors", "Sockets", FieldKind.Number), new("NumberOfLogicalProcessors", "LogicalProcessors", FieldKind.Number)]),
        new(SystemInformationCategory.Windows, "Win32_OperatingSystem", "Caption",
            [new("Caption", "OperatingSystem"), new("Version", "Version"), new("BuildNumber", "Build"), new("OSArchitecture", "Architecture"),
             new("InstallDate", "InstalledOn", FieldKind.Date), new("LastBootUpTime", "LastBoot", FieldKind.Date),
             new("FreePhysicalMemory", "AvailableMemory", FieldKind.Kilobytes), new("WindowsDirectory", "WindowsDirectory")]),
        new(SystemInformationCategory.Firmware, "Win32_BIOS", "Name",
            [new("Manufacturer", "Manufacturer"), new("SMBIOSBIOSVersion", "Version"), new("ReleaseDate", "ReleaseDate", FieldKind.Date),
             new("SMBIOSMajorVersion", "SmbiosMajor", FieldKind.Number), new("SMBIOSMinorVersion", "SmbiosMinor", FieldKind.Number)]),
        new(SystemInformationCategory.Motherboard, "Win32_BaseBoard", "Product",
            [new("Manufacturer", "Manufacturer"), new("Product", "Model"), new("Version", "Version")]),
        new(SystemInformationCategory.Processors, "Win32_Processor", "Name",
            [new("Manufacturer", "Manufacturer"), new("SocketDesignation", "Socket"), new("NumberOfCores", "Cores", FieldKind.Number),
             new("NumberOfLogicalProcessors", "LogicalProcessors", FieldKind.Number), new("MaxClockSpeed", "MaximumClock", FieldKind.Megahertz)]),
        new(SystemInformationCategory.Memory, "Win32_PhysicalMemory", "DeviceLocator",
            [new("BankLabel", "Bank"), new("Manufacturer", "Manufacturer"), new("PartNumber", "PartNumber"),
             new("Capacity", "Capacity", FieldKind.Bytes), new("ConfiguredClockSpeed", "ConfiguredClock", FieldKind.Megahertz)]),
        new(SystemInformationCategory.Graphics, "Win32_VideoController", "Name",
            [new("VideoProcessor", "VideoProcessor"), new("DriverVersion", "DriverVersion"), new("DriverDate", "DriverDate", FieldKind.Date),
             new("CurrentHorizontalResolution", "HorizontalResolution", FieldKind.Pixels), new("CurrentVerticalResolution", "VerticalResolution", FieldKind.Pixels)]),
        new(SystemInformationCategory.Storage, "Win32_DiskDrive", "Model",
            [new("InterfaceType", "Interface"), new("MediaType", "MediaType"), new("Size", "Capacity", FieldKind.Bytes),
             new("Partitions", "Partitions", FieldKind.Number), new("FirmwareRevision", "FirmwareVersion")]),
        new(SystemInformationCategory.Volumes, "Win32_LogicalDisk", "DeviceID",
            [new("VolumeName", "VolumeLabel"), new("FileSystem", "FileSystem"), new("Size", "Capacity", FieldKind.Bytes), new("FreeSpace", "FreeSpace", FieldKind.Bytes)]),
        new(SystemInformationCategory.Network, "Win32_NetworkAdapter", "Name",
            [new("Manufacturer", "Manufacturer"), new("AdapterType", "AdapterType"), new("MACAddress", "MacAddress"),
             new("PhysicalAdapter", "PhysicalAdapter", FieldKind.Boolean), new("NetEnabled", "Enabled", FieldKind.Boolean), new("Speed", "LinkSpeed", FieldKind.BitsPerSecond)]),
        new(SystemInformationCategory.Audio, "Win32_SoundDevice", "Name",
            [new("Manufacturer", "Manufacturer"), new("Status", "Status")]),
        new(SystemInformationCategory.Battery, "Win32_Battery", "Name",
            [new("EstimatedChargeRemaining", "Charge", FieldKind.Percent), new("Status", "Status")]),
        new(SystemInformationCategory.Drivers, "Win32_PnPSignedDriver", "DeviceName",
            [new("DeviceClass", "DeviceClass"), new("DeviceID", "DeviceId"), new("Manufacturer", "Manufacturer"), new("DriverProviderName", "Provider"),
             new("DriverVersion", "DriverVersion"), new("DriverDate", "DriverDate", FieldKind.Date), new("InfName", "InfName"), new("IsSigned", "Signed", FieldKind.Boolean)])
    ];

    public async Task<SystemInformationSnapshot> ReadAsync(CancellationToken ct)
    {
        List<SystemInformationSection> sections = [];
        foreach (var category in Categories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var properties = category.Fields.Select(field => field.Property).Prepend(category.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var rows = await query.ReadAsync(category.Class, properties, ct).ConfigureAwait(false);
                var items = rows.Select(row => new SystemInformationItem(ReadText(row, category.Title),
                    category.Fields.Select(field => new SystemInformationProperty("SystemInfo_Field_" + field.Label,
                        ReadValue(row, field))).ToArray())).ToArray();
                sections.Add(new(category.Id, items, null));
            }
            catch (Exception ex) when (ex is SystemInformationException or FormatException or OverflowException or InvalidCastException or ArgumentOutOfRangeException)
            { sections.Add(new(category.Id, [], ex is SystemInformationException ? ex.Message : $"{category.Class}: {ex.Message}")); }
        }
        ct.ThrowIfCancellationRequested();
        var snapshot = new SystemInformationSnapshot(DateTimeOffset.UtcNow, sections);
        Volatile.Write(ref _cachedSnapshot, snapshot);
        return snapshot;
    }

    private static string? ReadText(IReadOnlyDictionary<string, object?> row, string property) =>
        row.TryGetValue(property, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() : null;

    /// <summary>Normalizes WMI's typed scalars and DMTF dates without adding guessed hardware values.</summary>
    private static SystemInformationValue ReadValue(IReadOnlyDictionary<string, object?> row, Field field)
    {
        row.TryGetValue(field.Property, out var value);
        if (field.Kind == FieldKind.Text) return new SystemInformationText(ReadText(row, field.Property));
        if (field.Kind == FieldKind.Boolean) return new SystemInformationBoolean(value is null ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        if (field.Kind == FieldKind.Date)
        {
            var text = ReadText(row, field.Property);
            // WMI uses wildcard DMTF fields for unknown dates; these do not establish a real timestamp.
            return new SystemInformationDate(string.IsNullOrWhiteSpace(text) || text.Contains('*') ? null : ManagementDateTimeConverter.ToDateTime(text));
        }
        ulong? number = value is null ? null : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        if (field.Kind == FieldKind.Kilobytes && number is not null) number = checked(number.Value * 1024);
        var unit = field.Kind switch
        {
            FieldKind.Bytes or FieldKind.Kilobytes => SystemInformationUnit.Bytes,
            FieldKind.Megahertz => SystemInformationUnit.Megahertz,
            FieldKind.BitsPerSecond => SystemInformationUnit.BitsPerSecond,
            FieldKind.Pixels => SystemInformationUnit.Pixels,
            FieldKind.Percent => SystemInformationUnit.Percent,
            _ => SystemInformationUnit.Count
        };
        return new SystemInformationNumber(number, unit);
    }
}
