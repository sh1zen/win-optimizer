using WinOptimizer.Core.Features.Common.Constants;
using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Services;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Appends structured change receipts and exposes paged reads of the saved journal.</summary>
public class ChangeHistoryService(
    IFileSystemService fileSystemService,
    ILocalizationService localizationService,
    ILogService logService) : IChangeHistoryService
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly object _lock = new();
    private readonly AsyncLocal<BatchScope?> _batch = new();
    private static string DirectoryPath => AppDataPaths.History;
    private static string FilePath => Path.Combine(DirectoryPath, "ChangeHistory.jsonl");

    public void LogSettingChange(string displayName, string? localizedGroupName, string before, string after) =>
        WriteEntry(new() { Timestamp = DateTimeOffset.Now, Kind = HistoryEntryKind.SettingChange,
            Name = displayName, Group = localizedGroupName, Before = before, After = after });

    public void LogSettingAction(string displayName, string? localizedGroupName) =>
        WriteEntry(new() { Timestamp = DateTimeOffset.Now, Kind = HistoryEntryKind.SettingAction,
            Name = displayName, Group = localizedGroupName });

    public void LogMaintenance(LogTopic topic, string name, string action, Guid? recoveryId = null, string? accountSid = null) =>
        WriteEntry(new() { Timestamp = DateTimeOffset.Now, Kind = HistoryEntryKind.Maintenance,
            Topic = topic, Name = name, ActionLabel = action, RecoveryId = recoveryId, AccountSid = accountSid });

    public void LogAppChange(string appDisplayName, AppChangeKind kind)
    {
        try
        {
            var key = kind == AppChangeKind.Installed ? "ChangeHistory_AppInstalled" : "ChangeHistory_AppRemoved";
            var label = localizationService.GetString(key);
            if (string.IsNullOrEmpty(label)) label = kind == AppChangeKind.Installed ? "App installed" : "App removed";
            WriteEntry(new() { Timestamp = DateTimeOffset.Now,
                Kind = kind == AppChangeKind.Installed ? HistoryEntryKind.AppInstalled : HistoryEntryKind.AppRemoved,
                Name = appDisplayName, ActionLabel = label });
        }
        catch (Exception ex)
        { logService.Log(LogLevel.Warning, $"[ChangeHistoryService] Failed to write entry: {ex.Message}"); }
    }

    /// <summary>Shares an outer batch with its awaited work and keeps independent execution flows separate.</summary>
    public IDisposable BeginBatch(string localizedHeader)
    {
        var previous = GetActiveScope(_batch.Value);
        var scope = new BatchScope(this, previous, previous?.Batch ?? new HistoryBatch(localizedHeader, Guid.NewGuid()));
        _batch.Value = scope;
        return scope;
    }

    private static BatchScope? GetActiveScope(BatchScope? scope)
    {
        while (scope is { IsDisposed: true }) scope = scope.Previous;
        return scope;
    }

    public string GetFilePath()
    {
        lock (_lock)
        {
            try
            {
                EnsureDirectory();
                if (!fileSystemService.FileExists(FilePath)) fileSystemService.AppendAllText(FilePath, "", Utf8);
            }
            catch (Exception ex)
            { logService.Log(LogLevel.Warning, $"[ChangeHistoryService] Failed to create history file: {ex.Message}"); }
        }
        return FilePath;
    }

    /// <summary>Lists the saved change journal without creating storage.</summary>
    public Task<IReadOnlyList<LogFileInfo>> GetFilesAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<LogFileInfo>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            try
            {
                return fileSystemService.GetFiles(DirectoryPath, "ChangeHistory.jsonl")
                    .Select(path => new LogFileInfo(Path.GetFileName(path), fileSystemService.GetLastWriteTime(path))).ToArray();
            }
            catch (DirectoryNotFoundException) { return []; }
        }
    }, cancellationToken);

    public Task<LogPage<ChangeHistoryEntry>> ReadAsync(string fileName, long offset = 0, int pageSize = 200,
        CancellationToken cancellationToken = default) => ReadPageAsync(fileName, offset, pageSize, false, cancellationToken);

    public Task<LogPage<ChangeHistoryEntry>> ReadRecentAsync(string fileName, long? beforeOffset = null, int pageSize = 200,
        CancellationToken cancellationToken = default) => ReadPageAsync(fileName, beforeOffset, pageSize, true, cancellationToken);

    /// <summary>Shares the allowlist, snapshot and stream lifetime for chronological and newest-first reads.</summary>
    private Task<LogPage<ChangeHistoryEntry>> ReadPageAsync(string fileName, long? offset, int pageSize, bool newest,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        if (fileName != "ChangeHistory.jsonl")
            throw new ArgumentException("Select a saved change history file.", nameof(fileName));
        using var stream = fileSystemService.OpenReadShared(Path.Combine(DirectoryPath, fileName));
        long length;
        lock (_lock)
        {
            length = stream.Length;
        }
        return newest
            ? await LogPageReader.ReadNewestAsync(stream, offset, length, pageSize, ReadStructuredEntry, cancellationToken).ConfigureAwait(false)
            : await LogPageReader.ReadAsync(stream, offset ?? 0, length, pageSize, ReadStructuredEntry, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    private static ChangeHistoryEntry ReadStructuredEntry(string line)
    {
        var entry = JsonSerializer.Deserialize<ChangeHistoryEntry>(line, LogPageReader.JsonOptions) ?? throw new JsonException("Missing change receipt.");
        if (entry.Timestamp is null || entry.Name is null ||
            (entry.Kind == HistoryEntryKind.SettingChange && (entry.Before is null || entry.After is null)))
            throw new JsonException("Incomplete change receipt.");
        return entry;
    }

    private void WriteEntry(ChangeHistoryEntry entry)
    {
        lock (_lock)
        {
            try
            {
                var batch = GetActiveScope(_batch.Value)?.Batch;
                var receipt = entry with { Batch = batch?.Label, BatchId = batch?.Id };
                var line = JsonSerializer.Serialize(receipt, LogPageReader.JsonOptions) + "\r\n";
                try { fileSystemService.AppendAllText(FilePath, line, Utf8); }
                catch (DirectoryNotFoundException)
                {
                    EnsureDirectory();
                    fileSystemService.AppendAllText(FilePath, line, Utf8);
                }
            }
            catch (Exception ex)
            { logService.Log(LogLevel.Warning, $"[ChangeHistoryService] Failed to write entry: {ex.Message}"); }
        }
    }

    private void EnsureDirectory()
    {
        if (!fileSystemService.DirectoryExists(DirectoryPath)) fileSystemService.CreateDirectory(DirectoryPath);
    }

    private sealed record HistoryBatch(string Label, Guid Id);

    private sealed class BatchScope(ChangeHistoryService owner, BatchScope? previous, HistoryBatch batch) : IDisposable
    {
        private int _disposed;
        public BatchScope? Previous { get; } = previous;
        public HistoryBatch Batch { get; } = batch;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && ReferenceEquals(owner._batch.Value, this))
                owner._batch.Value = GetActiveScope(Previous);
        }
    }
}
