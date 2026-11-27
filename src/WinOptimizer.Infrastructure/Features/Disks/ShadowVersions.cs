using WinOptimizer.Core.Features.Disks;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Reads existing VSS copies for the selected volume and restores data to a fresh destination.</summary>
internal sealed class ShadowVersions(IShadowCopyCatalog catalog)
{
    public IReadOnlyList<DiskShadowVersion> Find(string input, CancellationToken ct) =>
        Read(input, ct).Select(entry => entry.Version).OrderByDescending(version => version.Created).ToArray();

    public string Restore(string input, DiskShadowVersion version, string destinationFolder, CancellationToken ct)
    {
        var path = DiskPaths.LocalPath(input);
        var destination = DiskPaths.LocalPath(destinationFolder, allowRoot: true);
        var root = DiskPaths.Drive(Path.GetPathRoot(destination)!).Name;
        if (!DiskPaths.CheckTree(destination, root)!.Value.HasFlag(FileAttributes.Directory))
            throw new ArgumentException("Select an existing destination folder.");
        var target = Path.Join(destination, Path.GetFileName(path));
        if (DiskPaths.ReadAttributes(target, allowMissing: true) is not null)
            throw new IOException($"The destination already exists: {target}");

        var entry = Read(path, ct).FirstOrDefault(entry => entry.Version == version)
            ?? throw new IOException("This previous version is no longer available for the selected path.");
        var staging = Directory.CreateDirectory(Path.Join(destination, $".win-optimizer-restore-{Guid.NewGuid():N}")).FullName;
        try
        {
            var copy = Path.Join(staging, Path.GetFileName(path));
            if (version.IsDirectory)
                CopyTree(entry.Path, copy, ct);
            else
                CopyFile(entry.Path, copy, ct);

            ct.ThrowIfCancellationRequested();
            if (version.IsDirectory)
                Directory.Move(copy, target);
            else
                File.Move(copy, target);
            return target;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private IEnumerable<ShadowEntry> Read(string input, CancellationToken ct)
    {
        var path = DiskPaths.LocalPath(input);
        var root = DiskPaths.Drive(Path.GetPathRoot(path)!).Name;
        DiskPaths.CheckTree(path, root, allowMissing: true);
        foreach (var shadow in catalog.List(DiskPaths.VolumeId(root), ct))
        {
            ct.ThrowIfCancellationRequested();
            var shadowPath = Path.Join(shadow.Root, path[root.Length..]);
            var attributes = DiskPaths.CheckTree(shadowPath, shadow.Root, allowMissing: true);
            if (attributes.HasValue)
                yield return new(new(shadow.Id, shadow.Created, attributes.Value.HasFlag(FileAttributes.Directory)), shadowPath);
        }
    }

    private static void CopyTree(string source, string destination, CancellationToken ct)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.TryPop(out var next))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(next.Destination);
            foreach (var item in Directory.EnumerateFileSystemEntries(next.Source))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(item);
                DiskPaths.ValidateName(name);
                var target = Path.Join(next.Destination, name);
                if (DiskPaths.ReadAttributes(item)!.Value.HasFlag(FileAttributes.Directory))
                    pending.Push((item, target));
                else
                    CopyFile(item, target, ct);
            }
        }
    }

    private static void CopyFile(string source, string destination, CancellationToken ct)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            input.CopyToAsync(output, ct).GetAwaiter().GetResult();
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private sealed record ShadowEntry(DiskShadowVersion Version, string Path);
}
