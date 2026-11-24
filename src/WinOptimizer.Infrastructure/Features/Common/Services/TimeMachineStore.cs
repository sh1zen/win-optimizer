using System.Text.Json;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Drivers;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Owns account-scoped, protected snapshot and recovery storage.</summary>
public sealed class TimeMachineStore(IInteractiveUserService user, TimeProvider clock, IChangeHistoryService history, ILogService log)
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    private readonly object _gate = new();
    private readonly string? _storageDirectory;
    public string AccountSid => AppDataPaths.InteractiveUserSid(user);
    private string Root => Path.Combine(_storageDirectory ?? AppDataPaths.TimeMachine, AccountSid);

    internal TimeMachineStore(IInteractiveUserService user, TimeProvider clock, IChangeHistoryService history,
        ILogService log, string storageDirectory)
        : this(user, clock, history, log) => _storageDirectory = Path.GetFullPath(storageDirectory);

    public TimeMachineSnapshot SaveSnapshot(IReadOnlyList<TimeMachineState> states)
    {
        var snapshot = new TimeMachineSnapshot(Guid.NewGuid(), clock.GetUtcNow(), states);
        lock (_gate) Write(PathFor("Snapshots", snapshot.Id), snapshot);
        return snapshot;
    }

    public IReadOnlyList<TimeMachineSnapshot> ListSnapshots(int days)
    {
        lock (_gate)
            return Entries("Snapshots").Select(path => ReadForSweep<TimeMachineSnapshot>(path, out var value) ? value : null)
                .Where(x => x is not null && !Expired(x.CreatedUtc, days))
                .OfType<TimeMachineSnapshot>().OrderByDescending(x => x.CreatedUtc).ToArray();
    }

    public TimeMachineSnapshot ReadSnapshot(Guid id, int days)
    {
        lock (_gate)
        {
            var value = Read<TimeMachineSnapshot>(PathFor("Snapshots", id));
            return value is not null && !Expired(value.CreatedUtc, days)
                ? value : throw new IOException("The selected snapshot is unavailable or has expired.");
        }
    }

    /// <summary>Deletes a snapshot belonging to the interactive account.</summary>
    public void DeleteSnapshot(Guid id)
    {
        lock (_gate) DeleteEntry(PathFor("Snapshots", id));
    }

    public TimeMachineUndo PrepareUndo(TimeMachineState before, TimeMachineValue after, Guid? id = null)
    {
        var undo = new TimeMachineUndo(id ?? Guid.NewGuid(), clock.GetUtcNow(), before, after);
        lock (_gate) Write(PathFor("Changes", undo.Id), undo);
        return undo;
    }

    public TimeMachineUndo? ReadUndo(Guid id, int days)
    {
        lock (_gate)
        {
            var undo = Read<TimeMachineUndo>(PathFor("Changes", id));
            return undo is not null && !Expired(undo.CreatedUtc, days) ? undo : null;
        }
    }

    public void Record(LogTopic topic, string name, string action, TimeMachineUndo? undo = null) =>
        history.LogMaintenance(topic, name, action, undo?.Id, AccountSid);

    public string DriverPayload(Guid id) => Path.Combine(PathFor("Changes", id), "Driver");

    public void DiscardUndo(Guid id)
    {
        lock (_gate) DeleteEntry(PathFor("Changes", id));
    }

    /// <summary>Expires committed records and abandoned, unpublished payloads.</summary>
    public void Prune(int days)
    {
        lock (_gate)
        {
            PruneEntries<TimeMachineSnapshot>("Snapshots", days, snapshot => snapshot.CreatedUtc);
            PruneEntries<TimeMachineUndo>("Changes", days, undo => undo.CreatedUtc);
        }
    }

    /// <summary>Isolates damaged manifests and keeps them available for recovery diagnostics.</summary>
    private bool ReadForSweep<T>(string entry, out T? value, bool reportErrors = false) where T : class
    {
        try
        {
            value = Read<T>(entry);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (reportErrors) log.LogWarning($"TimeMachine retained unreadable record '{Path.GetFileName(entry)}': {ex.Message}");
            value = null;
            return false;
        }
    }

    private void PruneEntries<T>(string kind, int days, Func<T, DateTimeOffset> createdUtc) where T : class
    {
        foreach (var entry in Entries(kind).ToArray())
        {
            if (!ReadForSweep<T>(entry, out var value, reportErrors: true)) continue;
            try
            {
                if (Expired(value is null ? Directory.GetLastWriteTimeUtc(entry) : createdUtc(value), days))
                    DeleteEntry(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log.LogWarning($"TimeMachine could not expire record '{Path.GetFileName(entry)}': {ex.Message}");
            }
        }
    }

    private bool Expired(DateTimeOffset createdUtc, int days) =>
        TimeMachineRetention.IsExpired(createdUtc, clock.GetUtcNow(), days);

    private string PathFor(string kind, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A recovery entry requires an identifier.", nameof(id));
        return Path.Combine(Root, kind, id.ToString("N"));
    }

    private IEnumerable<string> Entries(string kind)
    {
        var folder = Path.Combine(Root, kind);
        AppDataStorage.ValidatePath(folder);
        return Directory.Exists(folder)
            ? Directory.EnumerateDirectories(folder).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
            : [];
    }

    private static T? Read<T>(string entry) where T : class
    {
        var path = Path.Combine(entry, "item.json");
        AppDataStorage.ValidatePath(path);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        var value = JsonSerializer.Deserialize<T>(stream, ManifestJson) ?? throw new IOException("The TimeMachine record is empty.");
        var id = value switch { TimeMachineSnapshot snapshot => snapshot.Id, TimeMachineUndo undo => undo.Id, _ => Guid.Empty };
        if (id == Guid.Empty || !Path.GetFileName(entry).Equals(id.ToString("N"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The TimeMachine record does not match its storage identity.");
        return value;
    }

    private static void Write<T>(string entry, T value)
    {
        var destination = Path.Combine(entry, "item.json");
        var pending = Path.Combine(entry, "item.partial");
        AppDataStorage.ValidatePath(destination);
        AppDataStorage.ValidatePath(pending);
        Directory.CreateDirectory(entry);
        try
        {
            using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, destination);
        }
        finally { File.Delete(pending); }
    }

    private static void DeleteEntry(string entry)
    {
        AppDataStorage.ValidatePath(entry);
        if (!Directory.Exists(entry)) return;
        foreach (var _ in DriverScanWorkspace.EnumerateFiles(entry, null)) { } // Validate every descendant.
        Directory.Delete(entry, recursive: true);
    }
}
