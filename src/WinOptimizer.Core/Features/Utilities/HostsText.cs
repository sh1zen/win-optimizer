namespace WinOptimizer.Core.Features.Utilities;

/// <summary>A parsed mapping tied to its exact source line, including the original line ending.</summary>
public sealed record HostsMapping(int Start, string Source, string Address, string HostNames, string Comment, bool IsEnabled);

/// <summary>Structured mappings and the presence of active lines requiring review in the text editor.</summary>
public sealed record HostsTextSnapshot(IReadOnlyList<HostsMapping> Entries, bool HasInvalidLines);

/// <summary>Edits individual hosts mappings without rewriting surrounding comments, whitespace or line endings.</summary>
public static class HostsText
{
    public static HostsTextSnapshot Parse(string content)
    {
        var entries = new List<HostsMapping>();
        var invalid = false;
        for (var start = 0; start < content.Length;)
        {
            var end = start;
            while (end < content.Length && content[end] is not ('\r' or '\n')) end++;
            var next = end;
            if (next < content.Length && content[next++] == '\r' && next < content.Length && content[next] == '\n') next++;
            var line = content[start..end];
            var mapping = line.TrimStart();
            var enabled = !mapping.StartsWith('#');
            if (!enabled) mapping = mapping[1..].TrimStart();
            var parts = mapping.Split('#', 2);
            var fields = parts[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0 && HostsSyntax.MappingError(fields) == null && !line.Contains('\0'))
                entries.Add(new(start, content[start..next], fields[0], string.Join(' ', fields.Skip(1)),
                    parts.Length == 2 ? parts[1].Trim() : "", enabled));
            else if (enabled && line.Trim().Length > 0) invalid = true;
            start = next;
        }
        return new(entries, invalid || content.Contains('\0'));
    }

    /// <summary>Validates a dialog's fields even when the resulting mapping will be disabled.</summary>
    public static string FormatEntry(string address, string hostNames, string comment, bool enabled)
    {
        if (new[] { address, hostNames, comment }.Any(value => value.Any(c => c is '\r' or '\n' or '\0')) ||
            address.Contains('#') || hostNames.Contains('#'))
            throw new ArgumentException("Entry fields cannot contain line breaks, null characters or mapping comment markers.");
        var fields = new[] { address.Trim() }.Concat(hostNames.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (HostsSyntax.MappingError(fields) is { } error) throw new ArgumentException(error);
        return (enabled ? "" : "# ") + fields[0] + "\t" + string.Join(' ', fields.Skip(1)) +
            (string.IsNullOrWhiteSpace(comment) ? "" : " # " + comment.Trim());
    }

    public static string Add(string content, string address, string hostNames, string comment, bool enabled)
    {
        var line = FormatEntry(address, hostNames, comment, enabled);
        var firstBreak = content.IndexOfAny(['\r', '\n']);
        var ending = firstBreak < 0 ? "\r\n" : content[firstBreak] == '\r'
            ? firstBreak + 1 < content.Length && content[firstBreak + 1] == '\n' ? "\r\n" : "\r" : "\n";
        return content + (content.Length == 0 || content[^1] is '\r' or '\n' ? "" : ending) + line + ending;
    }

    public static string Edit(string content, HostsMapping entry, string address, string hostNames, string comment, bool enabled)
    {
        var endingStart = entry.Source.Length;
        while (endingStart > 0 && entry.Source[endingStart - 1] is '\r' or '\n') endingStart--;
        return Replace(content, entry, FormatEntry(address, hostNames, comment, enabled) + entry.Source[endingStart..]);
    }

    /// <summary>Adds/removes only the disable marker, keeping the mapping's spacing and inline comment intact.</summary>
    public static string SetEnabled(string content, HostsMapping entry, bool enabled)
    {
        if (entry.IsEnabled == enabled) return content;
        var marker = 0;
        while (marker < entry.Source.Length && char.IsWhiteSpace(entry.Source[marker])) marker++;
        var source = enabled ? entry.Source.Remove(marker, marker + 1 < entry.Source.Length && entry.Source[marker + 1] == ' ' ? 2 : 1)
            : entry.Source.Insert(marker, "# ");
        return Replace(content, entry, source);
    }

    public static string Remove(string content, HostsMapping entry) => Replace(content, entry, "");

    private static string Replace(string content, HostsMapping entry, string replacement)
    {
        if (entry.Start < 0 || entry.Start > content.Length - entry.Source.Length ||
            !content.AsSpan(entry.Start, entry.Source.Length).SequenceEqual(entry.Source))
            throw new ArgumentException("The entry changed. Reopen it before editing.");
        return string.Concat(content.AsSpan(0, entry.Start), replacement, content.AsSpan(entry.Start + entry.Source.Length));
    }
}
