using System.Security.Cryptography;
using System.Text.Json;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

/// <summary>Publishes authenticated, recoverable cleanup snapshots before the first resource is removed.</summary>
internal sealed class UninstallBackupStore(ICleanerBackupVault vault, UninstallResourceEditor editor)
{
    private sealed record Manifest(int Version, UninstallCleanupBackup Info, UninstallEvidence[] Entries,
        Dictionary<Guid, string> Hashes);
    internal sealed record Backup(UninstallCleanupBackup Info, IReadOnlyDictionary<Guid, string> Hashes);
    private string DirectoryFor(Guid id) => Path.Combine(vault.RootDirectory, id.ToString("N"));
    private static string ManifestPath(string directory) => Path.Combine(directory, "manifest.uninstall");

    public async Task<Backup> CreateAsync(IReadOnlyList<UninstallEvidence> entries, CancellationToken ct)
    {
        if (entries.Count == 0) throw new ArgumentException("An uninstall cleanup backup requires selected resources.", nameof(entries));
        foreach (var entry in entries) { ct.ThrowIfCancellationRequested(); editor.Verify(entry); }
        var info = new UninstallCleanupBackup(Guid.NewGuid(), DateTime.UtcNow,
            string.Join(", ", entries.Select(entry => entry.Finding.AppName).Distinct()), entries.Count);
        var directory = DirectoryFor(info.Id);
        vault.EnsureDirectory(directory);
        List<string> created = [];
        Dictionary<Guid, string> hashes = [];
        try
        {
            foreach (var entry in entries.Where(entry => entry.File != null))
            {
                ct.ThrowIfCancellationRequested();
                using var source = editor.OpenBackupFile(entry);
                var path = Payload(directory, entry.Finding.Id);
                using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                created.Add(path);
                hashes[entry.Finding.Id] = await CleanerBackupService.CopyVerifiedAsync(source, destination, ct).ConfigureAwait(false);
            }
            var manifest = new Manifest(1, info, entries.ToArray(), hashes);
            var partial = Path.Combine(directory, "manifest.partial");
            created.Add(partial);
            await File.WriteAllBytesAsync(partial, vault.Seal(JsonSerializer.SerializeToUtf8Bytes(manifest)), ct).ConfigureAwait(false);
            using (var stream = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            ct.ThrowIfCancellationRequested();
            var final = ManifestPath(directory);
            File.Move(partial, final);
            created.Add(final);
            _ = Read(info.Id);
            return new(info, hashes);
        }
        catch
        {
            foreach (var path in created)
            {
                CleanerPaths.ValidateDescendant(directory, path);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
            throw;
        }
    }

    public IReadOnlyList<UninstallCleanupBackup> List(CancellationToken ct)
    {
        if (!Directory.Exists(vault.RootDirectory)) return [];
        vault.ValidateDirectory(vault.RootDirectory);
        List<UninstallCleanupBackup> result = [];
        foreach (var directory in Directory.EnumerateDirectories(vault.RootDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && File.Exists(ManifestPath(directory)))
                result.Add(Read(id).Info);
        }
        return result.OrderByDescending(backup => backup.CreatedUtc).ToArray();
    }

    public async Task<UninstallRecoveryResult> RestoreAsync(Guid id, CancellationToken ct)
    {
        var manifest = Read(id);
        var directory = DirectoryFor(id);
        // Validate every payload before restoring the first resource.
        foreach (var entry in manifest.Entries.Where(entry => entry.File != null))
        {
            ct.ThrowIfCancellationRequested();
            using var stream = new FileStream(Payload(directory, entry.Finding.Id), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != entry.File!.Length ||
                Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) != manifest.Hashes[entry.Finding.Id])
                throw new IOException("Uninstall cleanup backup integrity verification failed. No resources were restored.");
        }
        int restored = 0;
        List<string> errors = [];
        foreach (var entry in manifest.Entries.OrderBy(entry => entry.Finding.Kind is UninstallArtifactKind.RegistryKey or UninstallArtifactKind.Directory ? 0 : 1)
            .ThenBy(entry => entry.Finding.Path.Count(character => character == '\\')))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await editor.RestoreAsync(entry, entry.File == null ? null : Payload(directory, entry.Finding.Id),
                    manifest.Hashes.GetValueOrDefault(entry.Finding.Id), ct).ConfigureAwait(false);
                restored++;
            }
            catch (Exception ex) when (UninstallResources.IsResourceError(ex)) { errors.Add($"{entry.Finding.Path}: {ex.Message}"); }
        }
        return new(restored, errors);
    }

    private Manifest Read(Guid id)
    {
        var directory = DirectoryFor(id);
        vault.ValidateDirectory(directory);
        var path = ManifestPath(directory);
        CleanerPaths.ValidateDescendant(directory, path);
        var manifest = JsonSerializer.Deserialize<Manifest>(vault.Unseal(File.ReadAllBytes(path)))
            ?? throw new IOException("The uninstall cleanup backup manifest is invalid.");
        if (manifest.Version != 1 || manifest.Info.Id != id || manifest.Entries.Length != manifest.Info.Items ||
            manifest.Entries.Select(entry => entry.Finding.Id).Distinct().Count() != manifest.Entries.Length ||
            manifest.Entries.Any(entry => entry.File != null && !manifest.Hashes.ContainsKey(entry.Finding.Id)))
            throw new IOException("The uninstall cleanup backup is incomplete or unsupported.");
        return manifest;
    }

    private static string Payload(string directory, Guid id)
    {
        var path = Path.Combine(directory, id.ToString("N") + ".bin");
        CleanerPaths.ValidateDescendant(directory, path);
        return path;
    }
}
