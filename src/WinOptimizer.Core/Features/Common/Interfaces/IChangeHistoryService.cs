using System;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>
/// Persists structured change receipts. Write failures log a warning without blocking
/// the actual operation; reads expose errors to the viewer. Names and values retain
/// the language active when the change was made.
/// </summary>
public interface IChangeHistoryService
{
    /// <summary>Records a setting's name, optional group, and before/after values.</summary>
    void LogSettingChange(string displayName, string? localizedGroupName, string before, string after);

    /// <summary>Records an Action-type setting that ran.</summary>
    void LogSettingAction(string displayName, string? localizedGroupName);

    /// <summary>Records application installation or removal.</summary>
    void LogAppChange(string appDisplayName, AppChangeKind kind);

    /// <summary>
    /// Starts a batch (config import, bulk action). Receipts retain its label and identifier.
    /// Empty batches write nothing. Dispose to end the batch; nested batches join the outermost one.
    /// </summary>
    IDisposable BeginBatch(string localizedHeader);

    /// <summary>Ensures the structured history file exists and returns its full path. Never throws.</summary>
    string GetFilePath();

    /// <summary>Lists structured history and any existing text receipt.</summary>
    Task<IReadOnlyList<LogFileInfo>> GetFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page from a history file returned by GetFilesAsync.</summary>
    Task<LogPage<ChangeHistoryEntry>> ReadAsync(string fileName, long offset = 0, int pageSize = 200, CancellationToken cancellationToken = default);

    /// <summary>Reads newest receipts first, continuing before the returned byte cursor.</summary>
    Task<LogPage<ChangeHistoryEntry>> ReadRecentAsync(string fileName, long? beforeOffset = null, int pageSize = 200, CancellationToken cancellationToken = default);
}
