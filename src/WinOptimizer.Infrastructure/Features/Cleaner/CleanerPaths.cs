using System.Security;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Restricts cleanup to local descendants and rejects junctions and symbolic links.</summary>
internal static class CleanerPaths
{
    public static bool IsLocalPath(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1..3] == ":\\";

    public static void ValidateDescendant(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var fullPath = Path.GetFullPath(path);
        if (!IsLocalPath(fullRoot) || !fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The cleanup path is outside its allowed directory.");
        for (string? part = fullPath; part is not null; part = Path.GetDirectoryName(part))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(part); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Cleanup does not follow links or junctions: {part}");
        }
    }

    public static bool IsMissingLocalFile(string path)
    {
        if (!IsLocalPath(path) || path.Contains('%') || path.IndexOfAny(['*', '?', '"', '\r', '\n']) >= 0 || path[3..].Contains(':')) return false;
        var drive = new DriveInfo(path[..3]);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return false;
        ValidateDescendant(path[..3], path);
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        // Access denied is not evidence of a missing file.
    }

    public static IEnumerable<string> EnumerateFiles(string root, List<string> notices, CancellationToken ct, bool recursive = true)
    {
        if (!Directory.Exists(root)) yield break;
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] entries;
            try
            {
                ValidateDescendant(root, Path.Combine(directory, ".cleaner-boundary"));
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                notices.Add($"{directory}: {ex.Message}");
                continue;
            }
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    notices.Add($"{entry}: {ex.Message}");
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) { if (recursive) pending.Push(entry); }
                else yield return entry;
            }
        }
    }
}
