namespace WinOptimizer.Core.Features.SystemInformation.Models;

public enum SystemInformationCategory
{
    Machine, Windows, Firmware, Motherboard, Processors, Memory, Graphics, Storage, Volumes, Network, Audio, Battery, Drivers
}

public enum SystemInformationUnit { Count, Bytes, Megahertz, BitsPerSecond, Pixels, Percent }

/// <summary>Preserves Windows value types; missing values stay unknown until presentation.</summary>
public abstract record SystemInformationValue;
public sealed record SystemInformationText(string? Value) : SystemInformationValue;
public sealed record SystemInformationNumber(ulong? Value, SystemInformationUnit Unit) : SystemInformationValue;
public sealed record SystemInformationDate(DateTime? Value) : SystemInformationValue;
public sealed record SystemInformationBoolean(bool? Value) : SystemInformationValue;
public sealed record SystemInformationProperty(string LabelKey, SystemInformationValue Value);
public sealed record SystemInformationItem(string? Name, IReadOnlyList<SystemInformationProperty> Properties);

/// <summary>An empty successful category differs from a Windows query that failed.</summary>
public sealed record SystemInformationSection(SystemInformationCategory Category,
    IReadOnlyList<SystemInformationItem> Items, string? Error);

/// <summary>A local machine snapshot collected on demand without persisting hardware or network identifiers.</summary>
public sealed record SystemInformationSnapshot(DateTimeOffset CollectedAt, IReadOnlyList<SystemInformationSection> Sections);

public sealed class SystemInformationException(string message, Exception? inner = null) : Exception(message, inner);
