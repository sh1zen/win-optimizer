using WinOptimizer.Core.Features.Common.Constants;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Owns one temporary export directory and confines inspection and cleanup to that directory.</summary>
internal sealed class DriverScanWorkspace : IDisposable
{
    private readonly string _parent;
    public string Root { get; }

    public DriverScanWorkspace(string? temporaryDirectory = null)
    {
        _parent = Path.Combine(temporaryDirectory ?? AppDataPaths.Temp, "Drivers");
        Root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
        ValidateChild(_parent, Root);
        Directory.CreateDirectory(Root);
    }

    /// <summary>Rejects escape paths and reparse points before reading packages or deleting temporary exports.</summary>
    internal static string ValidateChild(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Driver package path escapes its scan workspace.");
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Driver package paths cannot contain reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    public void Dispose()
    {
        var target = ValidateChild(_parent, Root);
        if (!Guid.TryParseExact(Path.GetFileName(target), "N", out _)) throw new IOException("Invalid driver scan workspace.");
        if (!Directory.Exists(target)) return;
        // Native CAB and WUA exports can retain the publisher's read-only file attributes.
        foreach (var file in EnumerateFiles(target, null))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(target, recursive: true);
    }

    /// <summary>Walks exports without following directory junctions or symbolic links.</summary>
    internal static IEnumerable<string> EnumerateFiles(string root, string? extension)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var path = ValidateChild(root, entry);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                foreach (var child in EnumerateFiles(path, extension)) yield return child;
            }
            else if (extension is null || Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }
}
