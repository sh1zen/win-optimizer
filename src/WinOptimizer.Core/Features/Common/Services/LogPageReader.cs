using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Services;

/// <summary>Reads bounded UTF-8 journal pages without rescanning earlier records or splitting multibyte characters.</summary>
public static class LogPageReader
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private const int MaximumLineBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Reads a complete-record snapshot; malformed or oversized rows are counted for the viewer.</summary>
    public static async Task<LogPage<T>> ReadAsync<T>(Stream stream, long offset, long snapshotLength,
        int pageSize, Func<string, T> parse, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (offset > snapshotLength) throw new IOException("The log changed. Refresh it before loading more records.");
        stream.Seek(offset, SeekOrigin.Begin);
        var entries = new List<T>();
        var unreadable = 0;
        var processed = 0;
        var position = offset;
        var buffer = new byte[16384];
        using var line = new MemoryStream();
        var oversized = false;

        void CompleteLine()
        {
            if (line.Length == 0 && !oversized) return;
            processed++;
            try
            {
                if (oversized) unreadable++;
                else
                {
                    var text = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r').TrimStart('\uFEFF');
                    if (!string.IsNullOrWhiteSpace(text)) entries.Add(parse(text));
                }
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException or FormatException)
            { unreadable++; }
            line.SetLength(0);
            oversized = false;
        }

        while (position < snapshotLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, snapshotLength - position)), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The log changed while it was being read. Refresh to reload it.");
            var remaining = buffer.AsSpan(0, read);
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delimiter = remaining.IndexOf((byte)'\n');
                if (delimiter < 0)
                {
                    AppendLineBytes(line, remaining, ref oversized);
                    position += remaining.Length;
                    break;
                }
                AppendLineBytes(line, remaining[..delimiter], ref oversized);
                position += delimiter + 1;
                CompleteLine();
                if (processed == pageSize) return new(entries, position, position < snapshotLength, unreadable);
                remaining = remaining[(delimiter + 1)..];
            }
        }
        CompleteLine();
        return new(entries, position, false, unreadable);
    }

    /// <summary>Reads backwards in bounded blocks so recent activity never requires scanning an entire session.</summary>
    public static async Task<LogPage<T>> ReadNewestAsync<T>(Stream stream, long? beforeOffset, long snapshotLength,
        int pageSize, Func<string, T> parse, CancellationToken cancellationToken)
    {
        var position = beforeOffset ?? snapshotLength;
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (position > snapshotLength) throw new IOException("The log changed. Refresh it before loading more records.");
        var entries = new List<T>();
        var unreadable = 0;
        var processed = 0;
        var firstByte = true;
        var oversized = false;
        var buffer = new byte[16384];
        using var line = new MemoryStream();

        void CompleteLine()
        {
            if (line.Length == 0 && !oversized) return;
            processed++;
            try
            {
                if (oversized) unreadable++;
                else
                {
                    var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
                    bytes.Reverse();
                    var text = Utf8.GetString(bytes).TrimEnd('\r').TrimStart('\uFEFF');
                    if (!string.IsNullOrWhiteSpace(text)) entries.Add(parse(text));
                }
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException or FormatException)
            { unreadable++; }
            line.SetLength(0);
            oversized = false;
        }

        while (position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, position);
            stream.Seek(position - count, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            // Consume the block in reading order, reversing each completed record before decoding it.
            var remaining = buffer.AsSpan(0, count);
            remaining.Reverse();
            if (firstByte)
            {
                cancellationToken.ThrowIfCancellationRequested();
                firstByte = false;
                if (remaining[0] == (byte)'\n')
                {
                    position--;
                    remaining = remaining[1..];
                }
            }
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var delimiter = remaining.IndexOf((byte)'\n');
                if (delimiter < 0)
                {
                    AppendLineBytes(line, remaining, ref oversized);
                    position -= remaining.Length;
                    break;
                }
                AppendLineBytes(line, remaining[..delimiter], ref oversized);
                position -= delimiter + 1;
                CompleteLine();
                if (processed == pageSize) return new(entries, position + 1, position + 1 > 0, unreadable);
                remaining = remaining[(delimiter + 1)..];
            }
        }
        CompleteLine();
        return new(entries, 0, false, unreadable);
    }

    /// <summary>Buffers only lines that remain within the complete-record size limit.</summary>
    private static void AppendLineBytes(MemoryStream line, ReadOnlySpan<byte> bytes, ref bool oversized)
    {
        if (oversized) return;
        if (bytes.Length > MaximumLineBytes - line.Length) oversized = true;
        else line.Write(bytes);
    }
}
