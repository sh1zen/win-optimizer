using System.Net;

namespace WinOptimizer.Core.Features.Utilities;

/// <summary>A versioned editable hosts file; the revision detects concurrent external edits.</summary>
public sealed record HostsDocument(string Content, string Revision, string FilePath);
public sealed record HostsSaveResult(HostsDocument Document, string BackupPath);

public interface IHostsEditorService
{
    Task<HostsDocument> ReadAsync();
    Task<HostsSaveResult> SaveAsync(HostsDocument original, string content);
}

/// <summary>Validates active mappings while preserving comments, whitespace and disabled lines verbatim.</summary>
public static class HostsSyntax
{
    public static void Validate(string content)
    {
        if (content.Contains('\0')) throw new ArgumentException("The hosts file cannot contain null characters.");
        using var reader = new StringReader(content);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            var fields = line.Split('#', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (MappingError(fields) is { } error) throw new ArgumentException($"Line {number}: {error}");
        }
    }

    /// <summary>Shares the mapping rules between file validation and structured-entry discovery.</summary>
    internal static string? MappingError(string[] fields)
    {
        if (fields.Length < 2 || !IPAddress.TryParse(fields[0], out _) || fields[0].Contains('%'))
            return "enter an IPv4/IPv6 address followed by at least one host name.";
        foreach (var host in fields.Skip(1))
            if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns ||
                host.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*'))
                return $"invalid host name '{host}'.";
        return null;
    }
}
