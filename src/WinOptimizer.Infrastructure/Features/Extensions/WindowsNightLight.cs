using Microsoft.Win32;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Edits the existing Night light state record for the desktop account.</summary>
internal static class WindowsNightLight
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\";
    private const string StatePath = @"\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";
    private const string Data = "Data";

    // CloudStore wraps the state in two Bond Compact Binary v1 records. Match the known envelope before writing.
    private static ReadOnlySpan<byte> Envelope => [0x43, 0x42, 1, 0, 0x0A, 2, 1, 0, 0x2A, 6];
    private static ReadOnlySpan<byte> Payload => [0x2A, 0x2B, 0x0E];
    private static ReadOnlySpan<byte> Header => [0x43, 0x42, 1, 0];
    private static ReadOnlySpan<byte> EnabledField => [0x10, 0];
    private static ReadOnlySpan<byte> SourceField => [0xD0, 0x0A];
    private static ReadOnlySpan<byte> TransitionField => [0xC6, 0x14];
    private static ReadOnlySpan<byte> SupportField => [0xC2, 0x1E];
    private static ReadOnlySpan<byte> CloudRecord => [0x43, 0x42, 1, 0, 0x0A, 0, 0x26];

    internal readonly record struct State(bool Enabled, bool? Supported, long Modified);

    internal static bool? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Store + "Current" + StatePath);
        var data = ReadData(key);
        if (data is null) return null;
        var state = Decode(data);
        return state.Supported == false ? null : state.Enabled;
    }

    /// <summary>Updates the manual state while leaving the schedule and strength in their separate record.</summary>
    internal static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Store + "Current" + StatePath, writable: true);
        if (key is null || ReadData(key) is not { } original)
            throw new InvalidOperationException("Windows has not initialized Night light state.");
        var state = Decode(original);
        if (state.Supported == false) throw new InvalidOperationException("Windows reports that Night light is unavailable.");
        if (state.Enabled == enabled) return;

        using var cloud = Registry.CurrentUser.OpenSubKey(Store + "Cloud" + StatePath);
        var cloudTimestamp = ReadData(cloud) is { } record ? DecodeCloudTimestamp(record) : 0;
        var updated = Encode(enabled, state, cloudTimestamp, DateTimeOffset.UtcNow);
        if (ReadData(key) is not { } current || !original.AsSpan().SequenceEqual(current))
            throw new IOException("Night light changed in Windows. Try again.");
        key.SetValue(Data, updated, RegistryValueKind.Binary);
    }

    private static byte[]? ReadData(RegistryKey? key) => key?.GetValue(Data) switch
    {
        null => null,
        byte[] { Length: <= 256 } data => data,
        _ => throw InvalidRecord()
    };

    /// <summary>Validates the state layout and identifies unsupported displays before returning a reading.</summary>
    internal static State Decode(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data, writable: false));
        try
        {
            Expect(reader, Envelope);
            long modified = ReadTimestamp(reader);
            Expect(reader, Payload);
            int size = reader.Read7BitEncodedInt();
            if (size < 5 || size != reader.BaseStream.Length - reader.BaseStream.Position - 3) throw InvalidRecord();
            Expect(reader, Header);
            bool enabled = Consume(reader, EnabledField);
            if (Consume(reader, SourceField) && reader.ReadByte() is not (0 or 2)) throw InvalidRecord();
            if (Consume(reader, TransitionField)) _ = ReadTimestamp(reader);
            bool? supported = Consume(reader, SupportField) ? reader.ReadByte() switch
            {
                0 => false,
                1 => true,
                _ => throw InvalidRecord()
            } : null;
            Expect(reader, [0, 0, 0, 0]);
            if (reader.BaseStream.Position != data.Length) throw InvalidRecord();
            return new(enabled, supported, modified);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException) { throw InvalidRecord(ex); }
    }

    private static long DecodeCloudTimestamp(byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data, writable: false));
        try
        {
            Expect(reader, CloudRecord);
            long timestamp = ReadTimestamp(reader);
            Expect(reader, [0]);
            if (reader.BaseStream.Position != data.Length) throw InvalidRecord();
            return timestamp;
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException) { throw InvalidRecord(ex); }
    }

    /// <summary>Stamps a manual transition after both CloudStore versions so the shell can apply it.</summary>
    internal static byte[] Encode(bool enabled, State state, long cloudTimestamp, DateTimeOffset now)
    {
        using var payload = new MemoryStream();
        using var fields = new BinaryWriter(payload);
        fields.Write(Header);
        if (enabled) fields.Write(EnabledField);
        fields.Write(SourceField);
        fields.Write((byte)2); // Compact Binary zigzag encoding of the manual source, 1.
        fields.Write(TransitionField);
        fields.Write7BitEncodedInt64(now.ToFileTime());
        if (state.Supported is bool supported)
        {
            fields.Write(SupportField);
            fields.Write(supported);
        }
        fields.Write((byte)0);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(Envelope);
        var previous = Math.Max(state.Modified, cloudTimestamp);
        if (previous > long.MaxValue - 2) throw InvalidRecord();
        writer.Write7BitEncodedInt64(Math.Max(now.ToUnixTimeSeconds(), previous + 2));
        writer.Write(Payload);
        writer.Write7BitEncodedInt((int)payload.Length);
        payload.WriteTo(output);
        writer.Write(new byte[3]);
        return output.ToArray();
    }

    private static long ReadTimestamp(BinaryReader reader)
    {
        long timestamp = reader.Read7BitEncodedInt64();
        return timestamp >= 0 ? timestamp : throw InvalidRecord();
    }

    private static bool Consume(BinaryReader reader, ReadOnlySpan<byte> marker)
    {
        var position = reader.BaseStream.Position;
        if (reader.ReadBytes(marker.Length).AsSpan().SequenceEqual(marker)) return true;
        reader.BaseStream.Position = position;
        return false;
    }

    private static void Expect(BinaryReader reader, ReadOnlySpan<byte> marker)
    {
        if (!Consume(reader, marker)) throw InvalidRecord();
    }

    private static InvalidDataException InvalidRecord(Exception? inner = null) =>
        new("Windows returned an unrecognized Night light state record.", inner);
}
