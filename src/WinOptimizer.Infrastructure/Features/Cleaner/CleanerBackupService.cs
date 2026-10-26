using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Durable, account-bound cleanup sessions with verified file payloads and exact-value recovery.</summary>
public sealed class CleanerBackupService(ICleanerBackupVault vault, CleanerFileScanner files,
    ICleanerRegistryRecovery registry) : ICleanerBackupService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private sealed record Entry(Guid Id, CleanerFileTarget? File, CleanerRegistryTarget? Registry, string? Hash);
    private sealed record Manifest(int Version, Guid Id, DateTime CreatedUtc, Entry[] Entries);
    private static CleanerBackupInfo Info(Manifest manifest) => new(manifest.Id, manifest.CreatedUtc,
        manifest.Entries.Count(x => x.File is not null), manifest.Entries.Count(x => x.Registry is not null));
    private string SessionPath(Guid id) => Path.Combine(vault.RootDirectory, id.ToString("N"));

    public Task<CleanerBackupInfo> CreateAsync(IReadOnlyList<CleanerFinding> findings, CancellationToken ct) => Task.Run(async () =>
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var directory = SessionPath(id);
        List<string> created = [];
        try
        {
            if (findings.Count == 0 || findings.Any(x => !x.CanClean || x.Target is not (CleanerFileTarget or CleanerRegistryTarget)))
                throw new ArgumentException("A cleanup backup requires actionable file or registry results.");
            vault.EnsureDirectory(directory);
            List<Entry> entries = [];
            for (int index = 0; index < findings.Count;)
            {
                ct.ThrowIfCancellationRequested();
                var finding = findings[index];
                if (finding.Target is CleanerFileTarget)
                {
                    List<Task<Entry>> pending = [];
                    ExceptionDispatchInfo? preparationFailure = null;
                    // Account/root validation stays ordered and serial. Only independent,
                    // exclusively opened payload copies run together, with at most four sources.
                    try
                    {
                        for (; pending.Count < 4 && index < findings.Count && findings[index].Target is CleanerFileTarget file; index++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var item = findings[index];
                            files.ValidateForCleanup(file);
                            FileStream source;
                            try { source = CleanerFileDeletion.OpenVerified(file, deleting: false, asynchronous: true); }
                            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 32 && pending.Count > 0)
                            {
                                // A repeated path or hard link can refer to a source we
                                // already hold exclusively. Drain it before retrying this item;
                                // a lock that remains after draining is still reported as failure.
                                break;
                            }
                            var payload = Path.Combine(directory, item.Id.ToString("N") + ".bin");
                            created.Add(payload);
                            pending.Add(CopyFileAsync(item.Id, file, source, payload, ct));
                        }
                    }
                    catch (Exception ex) { preparationFailure = ExceptionDispatchInfo.Capture(ex); }
                    try { entries.AddRange(await Task.WhenAll(pending).ConfigureAwait(false)); }
                    catch
                    {
                        // All handles have closed before rollback. Report the first failure
                        // in selection order, independently of which copy completed first.
                        foreach (var copy in pending)
                            if (copy.IsFaulted || copy.IsCanceled) copy.GetAwaiter().GetResult();
                        throw;
                    }
                    preparationFailure?.Throw();
                }
                else if (finding.Target is CleanerRegistryTarget value)
                {
                    registry.Verify(value);
                    entries.Add(new(finding.Id, null, value, null));
                    index++;
                }
            }
            var manifest = new Manifest(1, id, DateTime.UtcNow, entries.ToArray());
            var partial = Path.Combine(directory, "manifest.partial");
            var final = Path.Combine(directory, "manifest.backup");
            created.Add(partial);
            await File.WriteAllBytesAsync(partial, vault.Seal(JsonSerializer.SerializeToUtf8Bytes(manifest)), ct).ConfigureAwait(false);
            using (var committed = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) committed.Flush(true);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, final);
            created.Add(final);
            // The published session is readable and authenticated before callers can clean anything.
            _ = ReadManifest(id);
            return Info(manifest);
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
        finally { _gate.Release(); }
    }, ct);

    /// <summary>Owns an already verified source until its durable payload copy and readback finish.</summary>
    private static Task<Entry> CopyFileAsync(Guid id, CleanerFileTarget file, FileStream source, string payload, CancellationToken ct) => Task.Run(async () =>
    {
        // Do not cancel scheduling: the worker must always dispose its transferred handle.
        using (source)
        {
            ct.ThrowIfCancellationRequested();
            using var destination = new FileStream(payload, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await CopyVerifiedAsync(source, destination, ct).ConfigureAwait(false);
            return new Entry(id, file, null, hash);
        }
    });

    public Task<IReadOnlyList<CleanerBackupInfo>> ListAsync(CancellationToken ct) => Task.Run<IReadOnlyList<CleanerBackupInfo>>(() =>
    {
        if (!Directory.Exists(vault.RootDirectory)) return [];
        vault.ValidateDirectory(vault.RootDirectory);
        List<CleanerBackupInfo> list = [];
        foreach (var directory in Directory.GetDirectories(vault.RootDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) && File.Exists(Path.Combine(directory, "manifest.backup")))
                list.Add(Info(ReadManifest(id)));
        }
        return list.OrderByDescending(x => x.CreatedUtc).ToArray();
    }, ct);

    public Task<CleanerRestoreResult> RestoreAsync(Guid backupId, CancellationToken ct) => Task.Run(async () =>
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = ReadManifest(backupId);
            var directory = SessionPath(backupId);
            // Check every payload before the first restore, including cancellation and corrupt backups.
            foreach (var entry in manifest.Entries.Where(x => x.File is not null))
            {
                ct.ThrowIfCancellationRequested();
                var payload = Path.Combine(directory, entry.Id.ToString("N") + ".bin");
                CleanerPaths.ValidateDescendant(directory, payload);
                using var stream = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != entry.File!.Length || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) != entry.Hash)
                    throw new IOException("Backup integrity verification failed. No resources have been restored.");
            }
            int restored = 0;
            List<string> errors = [];
            foreach (var entry in manifest.Entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (entry.Registry is not null) registry.Restore(entry.Registry);
                    else if (entry.File is not null)
                    {
                        files.ValidateForRestore(entry.File);
                        if (File.Exists(entry.File.Path))
                        {
                            using var existing = new FileStream(entry.File.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (Convert.ToHexString(await SHA256.HashDataAsync(existing, ct).ConfigureAwait(false)) != entry.Hash)
                                throw new IOException("A different file now exists; it has been preserved.");
                            restored++;
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(entry.File.Path)!);
                        CleanerPaths.ValidateDescendant(entry.File.Root, entry.File.Path);
                        var payload = Path.Combine(directory, entry.Id.ToString("N") + ".bin");
                        using var source = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var destination = CleanerFileDeletion.CreateRestoreFile(entry.File.Path);
                        try
                        {
                            CleanerFileDeletion.ValidateHandlePath(destination.SafeFileHandle, entry.File.Path);
                            var hash = await CopyVerifiedAsync(source, destination, ct).ConfigureAwait(false);
                            if (hash != entry.Hash) throw new IOException("The backup payload changed while restoring it.");
                            File.SetLastWriteTimeUtc(destination.SafeFileHandle, entry.File.LastWriteUtc);
                        }
                        catch
                        {
                            CleanerFileDeletion.MarkForDeletion(destination.SafeFileHandle);
                            throw;
                        }
                    }
                    restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { errors.Add($"{entry.File?.Path ?? entry.Registry?.KeyPath}: {ex.Message}"); }
            }
            return new CleanerRestoreResult(restored, errors.AsReadOnly());
        }
        finally { _gate.Release(); }
    }, ct);

    private Manifest ReadManifest(Guid id)
    {
        var directory = SessionPath(id);
        vault.ValidateDirectory(vault.RootDirectory);
        vault.ValidateDirectory(directory);
        var path = Path.Combine(directory, "manifest.backup");
        CleanerPaths.ValidateDescendant(directory, path);
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(vault.Unseal(File.ReadAllBytes(path))) ?? throw new IOException("Missing backup metadata.");
            if (manifest.Version != 1 || manifest.Id != id || manifest.Entries is null || manifest.Entries.Length == 0 || manifest.Entries.Any(x => x is null) ||
                manifest.Entries.Select(x => x.Id).Distinct().Count() != manifest.Entries.Length ||
                manifest.Entries.Any(x => (x.File is null) == (x.Registry is null) || x.File is not null &&
                    (string.IsNullOrWhiteSpace(x.File.Root) || string.IsNullOrWhiteSpace(x.File.Path) || x.File.Length < 0 || x.Hash?.Length != 64)))
                throw new IOException("Invalid cleanup backup metadata.");
            return manifest;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        { throw new IOException("The cleanup backup is damaged or belongs to another Windows account.", ex); }
    }

    /// <summary>Copies with a reusable buffer, flushes to storage, then verifies the persisted bytes against the source hash.</summary>
    internal static async Task<string> CopyVerifiedAsync(Stream source, FileStream destination, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int bufferSize = 131072;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false)) != 0)
            {
                hash.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            destination.Flush(true);
            var expected = hash.GetHashAndReset();
            destination.Position = 0;
            var actual = await SHA256.HashDataAsync(destination, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new IOException("Backup copy verification failed.");
            return Convert.ToHexString(expected);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
