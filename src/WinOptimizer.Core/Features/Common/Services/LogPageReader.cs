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
            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                position++;
                if (buffer[index] == (byte)'\n')
                {
                    CompleteLine();
                    if (processed == pageSize) return new(entries, position, position < snapshotLength, unreadable);
                }
                else if (!oversized)
                {
                    if (line.Length == MaximumLineBytes) oversized = true;
                    else line.WriteByte(buffer[index]);
                }
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
            for (var index = count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                position--;
                if (firstByte)
                {
                    firstByte = false;
                    if (buffer[index] == (byte)'\n') continue;
                }
                if (buffer[index] == (byte)'\n')
                {
                    CompleteLine();
                    if (processed == pageSize) return new(entries, position + 1, position + 1 > 0, unreadable);
                }
                else if (!oversized)
                {
                    if (line.Length == MaximumLineBytes) oversized = true;
                    else line.WriteByte(buffer[index]);
                }
            }
        }
        CompleteLine();
        return new(entries, 0, false, unreadable);
    }

}
