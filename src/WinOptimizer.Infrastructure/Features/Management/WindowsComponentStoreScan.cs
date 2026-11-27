using System.Globalization;
using System.Text;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Reads only the CBS log segment written during a component-store scan.</summary>
internal sealed class WindowsComponentStoreScan(Stream log, long start) : IDisposable
{
    private const int MaximumLogBytes = 8 * 1024 * 1024;
    private const string BeginSummary = "Checking System Update Readiness.";
    private const string TotalLabel = "Total Detected Corruption:";

    internal static WindowsComponentStoreScan? Open(IFileSystemService files)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");
        if (!files.FileExists(path)) return null;
        Stream? stream = null;
        try
        {
            stream = files.OpenReadShared(path);
            return new(stream, stream.Length);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }
    }

    internal async Task<WindowsComponentStoreFindings?> ReadFindingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var length = log.Length - start;
            if (length is <= 0 or > MaximumLogBytes) return null;
            log.Position = start;
            var data = new byte[(int)length];
            await log.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return ParseSummary(Encoding.UTF8.GetString(data));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Accepts one detection summary and counts distinct components only when every problem has an identity.</summary>
    private static WindowsComponentStoreFindings? ParseSummary(string text)
    {
        var inSummary = false;
        var detectsOnly = false;
        var detected = 0;
        var entries = 0;
        var hasUnidentifiedEntry = false;
        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            var prefix = rawLine.IndexOf("CBS    ", StringComparison.Ordinal);
            var line = (prefix >= 0 ? rawLine[(prefix + 7)..] : rawLine).Trim();
            if (line.SequenceEqual(BeginSummary))
            {
                if (inSummary) return null;
                inSummary = true;
            }
            else if (inSummary && line.StartsWith("Operation:", StringComparison.Ordinal))
            {
                if (detectsOnly || !line["Operation:".Length..].Trim().SequenceEqual("Detect only")) return null;
                detectsOnly = true;
            }
            else if (inSummary && line.StartsWith(TotalLabel, StringComparison.Ordinal))
            {
                if (detected != 0 || !detectsOnly || !int.TryParse(line[TotalLabel.Length..].Trim(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out detected) || detected <= 0) return null;
            }
            else if (inSummary && !detectsOnly && line.StartsWith("(p)", StringComparison.Ordinal))
            {
                entries++;
                if (TryReadIdentity(line, out var identity, out var name)) components.TryAdd(identity, name);
                else hasUnidentifiedEntry = true;
            }
        }

        if (detected == 0) return null;
        var count = !hasUnidentifiedEntry && entries == detected ? components.Count : (int?)null;
        return new(detected, count, count == 1 ? components.Values.Single() : null);
    }

    /// <summary>Extracts Windows package or component identities from named corruption entries.</summary>
    private static bool TryReadIdentity(ReadOnlySpan<char> line, out string identity, out string name)
    {
        identity = name = "";
        var nameMarker = line.IndexOf("(n)", StringComparison.Ordinal);
        if (nameMarker < 0) return false;
        var value = line[(nameMarker + 3)..].TrimStart();
        var end = value.IndexOfAny(" \t\\/");
        if (end >= 0) value = value[..end];
        if (value.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) value = value[..^9];
        else if (value.EndsWith(".mum", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (value.Length > 260 || value.IsEmpty) return false;

        var nativeId = value.ToString();
        if (nativeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '~')))
            return false;
        var package = nativeId.Split('~');
        var component = nativeId.Split('_');
        if (package.Length == 5 && package[0].Length > 0 && IsHexToken(package[1])) name = package[0];
        else if (component.Length == 6 && component[1].Length > 0 && IsHexToken(component[2]) && IsHexToken(component[5])) name = component[1];
        if (name.Length == 0) return false;
        identity = nativeId;
        return true;
    }

    private static bool IsHexToken(string value) => value.Length == 16 && value.All(char.IsAsciiHexDigit);

    public void Dispose() => log.Dispose();
}
