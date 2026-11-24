using System.ComponentModel;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SharpCompress.Archives.SevenZip;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Extracts isolated CAB and ZIP contents without executing files from the package.</summary>
internal static class DriverArchive
{
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 24000;
    private static ReadOnlySpan<byte> SevenZipMagic => [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    internal sealed class Budget
    {
        private long _remaining = MaximumExpandedBytes;
        private int _entries = MaximumEntries;
        public void Consume(long bytes)
        {
            if (bytes < 0 || --_entries < 0 || (_remaining -= bytes) < 0)
                throw new IOException("The driver archive exceeds its expanded size or entry limit.");
        }
    }

    internal static string EntryPath(string root, string name)
    {
        name = name.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(name)) throw new IOException("Rooted driver archive path.");
        var path = root;
        foreach (var segment in name.TrimEnd(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar))
        {
            if (segment.Length == 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.EndsWith('.') || segment.EndsWith(' ') || IsDeviceName(segment))
                throw new IOException("Unsafe driver archive path.");
            path = Path.Combine(path, segment);
        }
        return DriverScanWorkspace.ValidateChild(root, path);
    }

    private static bool IsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(name[3]));
    }

    internal static async Task ExtractZipAsync(string path, string root, CancellationToken ct, Budget? budget = null)
    {
        using var zip = ZipFile.OpenRead(path);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        budget ??= new();
        if (zip.Entries.Count > MaximumEntries) throw new IOException("The driver archive has too many entries.");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = EntryPath(root, entry.FullName);
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!seen.Add(target) || unixMode is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new IOException("The driver archive contains duplicate paths or unsafe links.");
            budget.Consume(entry.Length);
            if (entry.Name.Length == 0 || unixMode == 0x4000)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            await using var input = entry.Open();
            await ExtractFileAsync(input, entry.Length, target, ct).ConfigureAwait(false);
        }
    }

    internal static async Task ExtractSevenZipAsync(string path, string root, CancellationToken ct, Budget budget)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = SevenZipArchive.OpenArchive(path);
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = EntryPath(root, entry.Key ?? "");
                var attributes = entry.Attrib ?? 0;
                var unixMode = (attributes >> 16) & 0xF000;
                if (!seen.Add(target) || entry.LinkTarget is not null || entry.IsEncrypted ||
                    unixMode is not (0 or 0x8000 or 0x4000) || (attributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The driver archive contains encrypted entries, duplicate paths, or unsafe links.");
                budget.Consume(entry.Size);
            }
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Entry.IsDirectory) continue;
                var target = EntryPath(root, reader.Entry.Key ?? "");
                using var input = reader.OpenEntryStream();
                await ExtractFileAsync(input, reader.Entry.Size, target, ct).ConfigureAwait(false);
            }
        }
        catch (SharpCompress.Common.ArchiveException ex)
        { throw new IOException("The embedded 7z archive could not be extracted.", ex); }
    }

    /// <summary>Finds a bounded 7z header in a signed self-extracting PE and copies only the embedded archive.</summary>
    internal static async Task<bool> ExtractSevenZipOverlayAsync(string executable, string destination, CancellationToken ct)
    {
        await using var stream = File.OpenRead(executable);
        var buffer = new byte[(int)Math.Min(stream.Length, 8L * 1024 * 1024)];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        for (var index = 0; index <= buffer.Length - 32;)
        {
            var offset = buffer.AsSpan(index).IndexOf(SevenZipMagic);
            if (offset < 0 || index + offset > buffer.Length - 32) break;
            index += offset;
            var header = buffer.AsSpan(index, 32);
            var nextOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
            var nextSize = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
            if (header[6] == 0 && nextSize is > 0 and <= 32L * 1024 * 1024 &&
                nextOffset <= (ulong)stream.Length && nextOffset + nextSize + 32 <= (ulong)(stream.Length - index) &&
                Crc32(header[12..]) == BinaryPrimitives.ReadUInt32LittleEndian(header[8..]))
            {
                stream.Position = index;
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await CopyExactAsync(stream, output, (long)(nextOffset + nextSize + 32), ct).ConfigureAwait(false);
                return true;
            }
            index++;
        }
        return false;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
        }
        return ~crc;
    }

    private static async Task ExtractFileAsync(Stream input, long length, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyExactAsync(input, output, length, ct).ConfigureAwait(false);
        var extra = new byte[1];
        if (await input.ReadAsync(extra, ct).ConfigureAwait(false) != 0)
            throw new IOException("The driver archive entry exceeds its stated size.");
    }

    private static async Task CopyExactAsync(Stream input, Stream output, long remaining, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct).ConfigureAwait(false);
            if (count == 0) throw new IOException("The driver archive entry is truncated.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            remaining -= count;
        }
    }

    internal static Task ExtractCabAsync(string path, string root, CancellationToken ct, Budget? budget = null) =>
        Task.Run(() => ExtractCab(path, root, ct, budget ?? new()), ct);

    private static void ExtractCab(string path, string root, CancellationToken ct, Budget budget)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? failure = null;
        var succeeded = SetupIterateCabinet(path, 0, (_, notification, parameter, _) =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                switch (notification)
                {
                    case 0x11: // SPFILENOTIFY_FILEINCABINET
                        var file = Marshal.PtrToStructure<CabinetFile>(parameter);
                        var target = EntryPath(root, file.Name);
                        budget.Consume(file.Size);
                        if (!seen.Add(target) || target.Length >= 260 ||
                            (file.Attributes & (int)FileAttributes.ReparsePoint) != 0)
                            throw new IOException("The CAB contains an unsafe path, duplicate, or exceeds its extraction limit.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        file.Target = target;
                        Marshal.StructureToPtr(file, parameter, false);
                        return 1; // FILEOP_DOIT
                    case 0x12: // SPFILENOTIFY_NEEDNEWCABINET
                        throw new IOException("Driver packages requiring additional CAB volumes are unsupported.");
                    default:
                        return 0;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
            {
                failure = ex;
                return notification == 0x11 ? 0u : 1223u;
            }
        }, IntPtr.Zero);
        ct.ThrowIfCancellationRequested();
        if (failure is not null) throw new IOException("The driver CAB could not be safely extracted.", failure);
        if (!succeeded) throw new IOException("The driver CAB could not be extracted.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CabinetFile
    {
        public IntPtr NamePointer;
        public uint Size;
        public uint Error;
        public ushort Date;
        public ushort Time;
        public ushort Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Target;
        public readonly string Name => Marshal.PtrToStringUni(NamePointer) ?? "";
    }

    private delegate uint CabinetCallback(IntPtr context, uint notification, IntPtr parameter1, IntPtr parameter2);

    [DllImport("setupapi.dll", EntryPoint = "SetupIterateCabinetW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupIterateCabinet(string cabinet, uint reserved,
        CabinetCallback callback, IntPtr context);
}
