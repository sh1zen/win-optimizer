using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;

internal sealed record LeftoverDirectory(string Name, UninstallFileRoot Root, IReadOnlyList<UninstallEvidence> Registration);

/// <summary>Captures aged installation remnants with stale registration evidence and no executable or shared ownership.</summary>
internal static class LeftoverDirectoryScanner
{
    /// <summary>Limits installation-folder cleanup to machine application roots or the registration owner's AppData.</summary>
    internal static UninstallFileRoot? FindRoot(string path, string? sid, ICleanerRegistrySession account, IUninstallScanFolders folders)
    {
        foreach (var directory in folders.ApplicationDirectories)
            if (IsPrivateDescendant(path, directory)) return new(path, null, null, directory);
        if (sid != null)
            foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
            {
                var directory = account.GetFolderPath(folder);
                if (IsPrivateDescendant(path, directory)) return new(path, sid, folder, directory);
            }
        return null;
    }

    private static bool IsPrivateDescendant(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !UninstallResources.Under(path, directory) ||
            path.Equals(directory, StringComparison.OrdinalIgnoreCase)) return false;
        var first = Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar)[0];
        return !new[] { "Common Files", "WindowsApps", "ModifiableWindowsApps", "Packages", "Microsoft", "Package Cache" }
            .Contains(first, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads each owned tree completely; uncertainty, links, active references or runnable payloads exclude the whole tree.</summary>
    internal static IReadOnlyList<LeftoverCandidate> Read(IReadOnlyList<LeftoverDirectory> directories,
        HashSet<string> activeDirectories, InstallerReferences references, DateTime cutoff, List<string> notices, CancellationToken ct)
    {
        List<LeftoverCandidate> result = [];
        foreach (var group in directories.GroupBy(directory => directory.Root.Path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var directory = group.First();
            var root = directory.Root;
            try
            {
                bool Overlaps(string path) => UninstallResources.Under(path, root.Path) || UninstallResources.Under(root.Path, path);
                if (group.Any(other => other.Root != root) || activeDirectories.Any(Overlaps) ||
                    references.SourceDirectories.Any(Overlaps) || references.Files.Any(path =>
                        UninstallResources.Under(path, root.Path) && !CleanerPaths.IsMissingLocalFile(path)) ||
                    directories.Any(other => !other.Root.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase) && Overlaps(other.Root.Path))) continue;
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
                        if (HasExecutablePayload(next.Path) || WindowsLeftoverSource.FileProtection(true, false,
                            info.LastWriteTimeUtc, info.LastAccessTimeUtc, cutoff) != null) { eligible = false; break; }
                        resources.Add((next.Path, false, info.Length, info.LastWriteTimeUtc));
                    }
                }
                if (!eligible) continue;
                var item = new LeftoverItem(Guid.NewGuid(), directory.Name, LeftoverKind.Folder, root.Path,
                    resources.Sum(resource => resource.Bytes), true,
                    "The registered application executable is absent and its registration is obsolete. This aged installation folder has no executable payload or active installation reference.");
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
        ".wsf" or ".wsh" or ".psm1" or ".cpl" or ".ocx" or ".drv" or ".pyd" or ".node" or ".mjs" or ".cjs";
}
