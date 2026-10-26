using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;

internal sealed record LeftoverDirectory(string Name, UninstallFileRoot Root, IReadOnlyList<UninstallEvidence> Registration,
    string EvidenceReason, bool EmptyOnly = false);

/// <summary>Captures aged installation remnants tied to obsolete registrations, without executable payloads or active ownership.</summary>
internal static class LeftoverDirectoryScanner
{
    /// <summary>Finds empty leaf folders only in dedicated installation roots, excluding system, shared and hidden directories.</summary>
    internal static void DiscoverEmpty(IReadOnlyList<UninstallFileRoot> roots, List<LeftoverDirectory> directories,
        List<string> notices, CancellationToken ct)
    {
        var registered = directories.Select(directory => directory.Root.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!UninstallResources.Exists(root.Path, directory: true)) continue;
                CleanerPaths.ValidateDirectory(root.BaseDirectory, root.Path);
                int count = 0;
                foreach (var child in Directory.EnumerateDirectories(root.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > 10000) throw new IOException("The installation root exceeds the bounded leftover scan.");
                    var name = Path.GetFileName(child);
                    if (registered.Contains(child) || !IsPrivateDescendant(child, root.Path) ||
                        name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Uninstall Information", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if ((File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden)) != 0 ||
                            Directory.EnumerateFileSystemEntries(child).Any()) continue;
                        directories.Add(new(name, root with { Path = child }, [],
                            "This empty installation directory contains no files or subfolders, and no known active installation references its path.", EmptyOnly: true));
                    }
                    catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{child}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{root.Path}: {ex.Message}"); }
        }
    }

    /// <summary>Binds registered installation folders to machine roots or the owning account's AppData.</summary>
    internal static UninstallFileRoot? FindRoot(string path, string? sid, IReadOnlyList<UninstallFileRoot> searchRoots)
    {
        foreach (var root in searchRoots)
            if ((root.Sid == null || root.Sid == sid) && IsPrivateDescendant(path, root.Path)) return root with { Path = path };
        return null;
    }

    private static bool IsPrivateDescendant(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !UninstallResources.Under(path, directory) ||
            path.Equals(directory, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = Path.GetRelativePath(directory, path);
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (relative.Equals("Programs", StringComparison.OrdinalIgnoreCase)) return false;
        return !new[] { "Common Files", "WindowsApps", "ModifiableWindowsApps", "Packages", "Microsoft", "Package Cache",
            "Windows", "Temp", "CrashDumps", "ConnectedDevicesPlatform", "Comms", "D3DSCache", "VirtualStore" }
            .Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads each owned tree completely; uncertainty, links, active references or runnable payloads exclude the whole tree.</summary>
    internal static IReadOnlyList<LeftoverCandidate> Read(IReadOnlyList<LeftoverDirectory> directories,
        HashSet<string> activeDirectories, InstallerReferences references, DateTime cutoff, List<string> notices, CancellationToken ct)
    {
        List<LeftoverCandidate> result = [];
        var groups = directories.GroupBy(directory => directory.Root.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var paths = groups.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> overlapping = new(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            for (var parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
                if (paths.Contains(parent)) { overlapping.Add(path); overlapping.Add(parent); }
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var directory = group.First();
            var root = directory.Root;
            try
            {
                bool Overlaps(string path) => UninstallResources.Under(path, root.Path) || UninstallResources.Under(root.Path, path);
                // Age and missing payloads cannot establish that an arbitrary data folder belongs to a removed app.
                var emptyOnly = group.Any(entry => entry.EmptyOnly);
                if (group.Any(other => other.Root != root || other.Registration.Count == 0 && !other.EmptyOnly) || activeDirectories.Any(Overlaps) ||
                    references.SourceDirectories.Any(Overlaps) || references.Files.Any(path =>
                        UninstallResources.Under(path, root.Path) && !CleanerPaths.IsMissingLocalFile(path)) || overlapping.Contains(root.Path)) continue;
                List<(string Path, bool Directory, long Bytes, DateTime Modified)> resources = [];
                var pending = new Stack<(string Path, int Depth)>();
                pending.Push((root.Path, 0));
                bool eligible = true;
                int count = 0;
                while (pending.TryPop(out var next))
                {
                    ct.ThrowIfCancellationRequested();
                    if (next.Depth > 16 || ++count > 10000) throw new IOException("The installation folder exceeds the bounded leftover scan.");
                    CleanerPaths.ValidateDescendant(root.BaseDirectory, next.Path);
                    var attributes = File.GetAttributes(next.Path);
                    if (emptyOnly && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) { eligible = false; break; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var info = new DirectoryInfo(next.Path);
                        if (info.LastWriteTimeUtc > cutoff) { eligible = false; break; }
                        resources.Add((next.Path, true, 0, info.LastWriteTimeUtc));
                        foreach (var entry in Directory.EnumerateFileSystemEntries(next.Path))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (pending.Count + count >= 10000) throw new IOException("The installation folder exceeds the bounded leftover scan.");
                            pending.Push((entry, next.Depth + 1));
                        }
                    }
                    else
                    {
                        var info = new FileInfo(next.Path);
                        if (HasExecutablePayload(next.Path) || info.LastWriteTimeUtc > cutoff || info.LastAccessTimeUtc > cutoff)
                        { eligible = false; break; }
                        resources.Add((next.Path, false, info.Length, info.LastWriteTimeUtc));
                    }
                }
                if (!eligible || emptyOnly && resources.Count != 1) continue;
                var item = new LeftoverItem(Guid.NewGuid(), directory.Name, LeftoverKind.Folder, root.Path,
                    resources.Sum(resource => resource.Bytes), true,
                    emptyOnly ? directory.EvidenceReason : directory.EvidenceReason + " This aged installation folder has no executable payload or active installation reference.");
                List<UninstallEvidence> evidence = group.SelectMany(entry => entry.Registration)
                    .DistinctBy(UninstallResources.PhysicalIdentity, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var resource in resources)
                {
                    var finding = new UninstallResidue(Guid.NewGuid(), item.Id.ToString("N"), item.Name, "Leftover",
                        resource.Directory ? UninstallArtifactKind.Directory : UninstallArtifactKind.File, resource.Path,
                        null, "", resource.Bytes, resource.Modified, false, true, item.Reason);
                    var file = resource.Directory ? null : new CleanerFileTarget(Path.GetDirectoryName(root.Path)!,
                        resource.Path, resource.Bytes, resource.Modified, cutoff);
                    evidence.Add(new(finding, root, file));
                }
                result.Add(new("directory:" + root.Path, item, evidence));
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { notices.Add($"{root.Path}: {ex.Message}"); }
        }
        return result;
    }

    private static bool HasExecutablePayload(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".exe" or ".com" or ".dll" or ".sys" or ".scr" or ".cmd" or ".bat" or ".ps1" or ".vbs" or ".js" or
        ".jar" or ".py" or ".msi" or ".msp" or ".appx" or ".msix" or ".lnk" or ".url" or ".hta" or
        ".wsf" or ".wsh" or ".psm1" or ".cpl" or ".ocx" or ".drv" or ".pyd" or ".node" or ".mjs" or ".cjs" or
        ".pyw" or ".pyc" or ".pyo" or ".vbe" or ".jse" or ".msixbundle" or ".appxbundle" or ".appref-ms" or ".application";
}
