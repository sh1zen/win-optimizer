using System;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>
/// Provides logging functionality for the application.
/// </summary>
public interface ILogService
{
    /// <summary>
    /// Starts logging to a file.
    /// </summary>
    void StartLog();

    /// <summary>Opens logging immediately while collecting session diagnostics on a worker thread.</summary>
    Task StartLogAsync();

    /// <summary>Tags diagnostics in the current asynchronous operation and restores the enclosing topic on disposal.</summary>
    IDisposable BeginTopic(LogTopic topic);

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void LogInformation(string message);

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void LogWarning(string message);

    /// <summary>
    /// Logs an error message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="exception">The exception associated with the error, if any.</param>
    void LogError(string message, Exception? exception = null);

    /// <summary>
    /// Logs a debug message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    void LogDebug(string message);

    /// <summary>
    /// Gets the path to the current log file.
    /// </summary>
    /// <returns>The path to the log file.</returns>
    string GetLogPath();

    /// <summary>Lists saved application log sessions, including existing text files.</summary>
    Task<IReadOnlyList<LogFileInfo>> GetFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded page identified by a file name returned by GetFilesAsync.</summary>
    Task<LogPage<AppLogEntry>> ReadAsync(string fileName, long offset = 0, int pageSize = 200, CancellationToken cancellationToken = default);

    /// <summary>Reads newest records first, continuing before the returned byte cursor.</summary>
    Task<LogPage<AppLogEntry>> ReadRecentAsync(string fileName, long? beforeOffset = null, int pageSize = 200, CancellationToken cancellationToken = default);

    /// <summary>Clears application log files while keeping the active writer usable.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs a message with the specified level.
    /// </summary>
    /// <param name="level">The log level.</param>
    /// <param name="message">The message to log.</param>
    /// <param name="exception">The exception associated with the message, if any.</param>
    void Log(LogLevel level, string message, Exception? exception = null);
}
