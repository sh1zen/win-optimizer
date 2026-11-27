using System.Security.Cryptography;
using System.Text;
using WinOptimizer.Core.Features.Disks;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Hashes a file or a sorted tree of relative paths, entry types and file digests.</summary>
internal static class DiskHashes
{
    public static async Task<DiskHash> ComputeAsync(string input, string? expected, CancellationToken ct)
    {
        expected = expected?.Trim();
        if (!string.IsNullOrEmpty(expected) && (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Enter a SHA-256 digest of 64 hexadecimal characters.");

        var path = DiskPaths.LocalPath(input, allowRoot: true);
        var root = DiskPaths.Drive(Path.GetPathRoot(path)!).Name;
        var attributes = DiskPaths.CheckTree(path, root);
        string hash = Convert.ToHexString(attributes!.Value.HasFlag(FileAttributes.Directory)
            ? await HashDirectoryAsync(path, ct).ConfigureAwait(false)
            : await HashFileAsync(path, ct).ConfigureAwait(false));
        return new(hash, string.IsNullOrEmpty(expected) ? null : hash.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<byte[]> HashDirectoryAsync(string root, CancellationToken ct)
    {
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        manifest.AppendData("WinOptimizer SHA256 tree v1\0"u8);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                DiskPaths.ValidateName(Path.GetFileName(entry));
                var isDirectory = DiskPaths.ReadAttributes(entry)!.Value.HasFlag(FileAttributes.Directory);
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                manifest.AppendData(isDirectory ? "D"u8 : "F"u8);
                manifest.AppendData(Encoding.UTF8.GetBytes(relative));
                manifest.AppendData([0]);
                if (isDirectory)
                    pending.Push(entry);
                else
                    manifest.AppendData(await HashFileAsync(entry, ct).ConfigureAwait(false));
            }
        }
        return manifest.GetHashAndReset();
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
    }
}
