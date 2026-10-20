using WinOptimizer.Core.Features.Common.Constants;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Services;

/// <summary>Writes structured diagnostic sessions and owns their in-app reading, retention and clearing.</summary>
public class LogService : ILogService, IDisposable
{
    private readonly string _logPath;
    private readonly string _logDirectory;
    private StreamWriter? _logWriter;
    private Task? _startLogTask;
    private readonly object _lockObject = new object();
    private ISystemInfoProvider? _systemInfoProvider;
    private readonly AsyncLocal<LogTopic?> _topic = new();
    private bool _disposed;

    public LogService() : this(AppDataPaths.Logs) { }

    /// <summary>Selects a storage directory without creating it or starting logging.</summary>
    public LogService(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = Path.GetFullPath(logDirectory);
        _logPath = Path.Combine(_logDirectory, $"WinOptimizer_Log_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jsonl");
    }

    public void SetSystemInfoProvider(ISystemInfoProvider systemInfoProvider)
    {
        _systemInfoProvider = systemInfoProvider;
    }

    /// <summary>Scopes metadata to the current execution flow without changing other concurrent operations.</summary>
    public IDisposable BeginTopic(LogTopic topic)
    {
        if (!Enum.IsDefined(topic)) throw new ArgumentOutOfRangeException(nameof(topic));
        var previous = _topic.Value;
        _topic.Value = topic;
        return new TopicScope(this, previous);
    }

    /// <summary>Restores the enclosing topic once, including when a workflow exits through an exception.</summary>
    private sealed class TopicScope(LogService owner, LogTopic? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner._topic.Value = previous;
        }
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        switch (level)
        {
            case LogLevel.Info:
                LogInformation(message);
                break;
            case LogLevel.Warning:
                LogWarning(message);
                break;
            case LogLevel.Error:
                LogError(message, exception);
                break;
            case LogLevel.Success:
                LogSuccess(message);
                break;
            case LogLevel.Debug:
                LogDebug(message);
                break;
            default:
                LogInformation(message);
                break;
        }

    }

    /// <summary>Starts a session and waits for its diagnostic header for synchronous callers.</summary>
    public void StartLog() => StartLogAsync().GetAwaiter().GetResult();

    /// <summary>Opens the writer immediately and collects hardware diagnostics without blocking log writes.</summary>
    public Task StartLogAsync()
    {
        lock (_lockObject)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_logWriter is not null) return _startLogTask ?? Task.CompletedTask;
                Directory.CreateDirectory(_logDirectory);
                CleanupOldLogs(_logDirectory, maxAgeDays: 30, maxFiles: 50);
                _logWriter = new StreamWriter(new FileStream(_logPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read | FileShare.Delete), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
                return _startLogTask = WriteDiagnosticHeaderAsync(_systemInfoProvider);
            }
            catch (Exception ex)
            {
                return Task.FromException(new InvalidOperationException(
                    $"Failed to start log at '{_logPath}': {ex.Message}", ex));
            }
        }
    }

    /// <summary>Collects outside the writer lock, then publishes the complete header as one group.</summary>
    private async Task WriteDiagnosticHeaderAsync(ISystemInfoProvider? provider)
    {
        using var topic = BeginTopic(LogTopic.SystemInformation);
        try
        {
            var info = provider is null ? null : await Task.Run(provider.Collect).ConfigureAwait(false);
            lock (_lockObject)
            {
                // Shutdown can close the session while a native diagnostic query finishes.
                if (_disposed) return;
                if (info is not null)
                {
                    LogInformation($"==== win-optimizer {info.AppVersion} Log Started ====");
                    LogInformation($"OS:            {info.OperatingSystem}");
                    LogInformation($"Architecture:  {info.Architecture}");
                    LogInformation($"Device Type:   {info.DeviceType}");
                    LogInformation($"CPU:           {info.Cpu}");
                    LogInformation($"RAM:           {info.Ram}");
                    LogInformation($"GPU:           {info.Gpu}");
                    LogInformation($".NET Runtime:  {info.DotNetRuntime}");
                    LogInformation($"Elevation:     {info.Elevation}");
                    LogInformation($"Firmware:      {info.FirmwareType}");
                    LogInformation($"Secure Boot:   {info.SecureBoot}");
                    LogInformation($"TPM:           {info.Tpm}");
                    LogInformation($"Domain Joined: {info.DomainJoined}");
                }
                else
                {
                    LogInformation("==== win-optimizer Log Started ====");
                    LogInformation("System info unavailable (provider not configured)");
                }
                LogInformation("=====================================");
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start log at '{_logPath}': {ex.Message}", ex);
        }
    }

    private void StopLog()
    {
        lock (_lockObject)
        {
            if (_disposed) return;
            try
            {
                LogInformation("==== win-optimizer Log Ended ====");
                _logWriter?.Close();
                _logWriter?.Dispose();
                _logWriter = null;
            }
            catch (Exception)
            {
                // Error stopping log
            }
            _disposed = true;
        }
    }

    public void LogInformation(string message)
    {
        WriteLog(message, LogLevel.Info);
    }

    public void LogWarning(string message)
    {
        WriteLog(message, LogLevel.Warning);
    }

    public void LogError(string message, Exception? exception = null)
    {
        WriteLog(message, LogLevel.Error, exception);
    }

    public void LogDebug(string message)
    {
        WriteLog(message, LogLevel.Debug);
    }

    private void LogSuccess(string message)
    {
        WriteLog(message, LogLevel.Success);
    }

    public string GetLogPath()
    {
        return _logPath;
    }

    /// <summary>Lists only application-owned diagnostic sessions, newest first.</summary>
    public Task<IReadOnlyList<LogFileInfo>> GetFilesAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<LogFileInfo>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lockObject)
        {
            string[] files;
            try { files = Directory.GetFiles(_logDirectory, "WinOptimizer_Log_*"); }
            catch (DirectoryNotFoundException) { return []; }
            return files
                .Where(IsLogFile).Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => new LogFileInfo(file.Name, file.LastWriteTimeUtc)).ToArray();
        }
    }, cancellationToken);

    /// <summary>Captures a complete-write boundary before asynchronously reading a page.</summary>
    public Task<LogPage<AppLogEntry>> ReadAsync(string fileName, long offset = 0, int pageSize = 200,
        CancellationToken cancellationToken = default) => ReadPageAsync(fileName, offset, pageSize, false, cancellationToken);

    public Task<LogPage<AppLogEntry>> ReadRecentAsync(string fileName, long? beforeOffset = null, int pageSize = 200,
        CancellationToken cancellationToken = default) => ReadPageAsync(fileName, beforeOffset, pageSize, true, cancellationToken);

    /// <summary>Shares file validation, snapshot and handle lifetime for either reading direction.</summary>
    private Task<LogPage<AppLogEntry>> ReadPageAsync(string fileName, long? offset, int pageSize, bool newest,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        var path = ResolveLogPath(fileName);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16384, FileOptions.Asynchronous);
        long length;
        lock (_lockObject)
        {
            _logWriter?.Flush();
            length = stream.Length;
        }
        return newest
            ? await LogPageReader.ReadNewestAsync(stream, offset, length, pageSize, ReadStructuredEntry, cancellationToken).ConfigureAwait(false)
            : await LogPageReader.ReadAsync(stream, offset ?? 0, length, pageSize, ReadStructuredEntry, cancellationToken).ConfigureAwait(false);
    }, cancellationToken);

    private static AppLogEntry ReadStructuredEntry(string line)
    {
        var entry = JsonSerializer.Deserialize<AppLogEntry>(line, LogPageReader.JsonOptions) ?? throw new JsonException("Missing diagnostic record.");
        if (entry.Timestamp is null || entry.Level is null || entry.Message is null)
            throw new JsonException("Incomplete diagnostic record.");
        return entry;
    }

    /// <summary>Clears saved sessions and truncates the active stream under the writer lock.</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        lock (_lockObject)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] files;
            try { files = Directory.GetFiles(_logDirectory, "WinOptimizer_Log_*"); }
            catch (DirectoryNotFoundException) { return; }
            if ((File.GetAttributes(_logDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The log directory is a link; application logs were not cleared.");
            foreach (var file in files.Where(IsLogFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Cannot clear linked log file '{Path.GetFileName(file)}'.");
                if (file.Equals(_logPath, StringComparison.OrdinalIgnoreCase) && _logWriter is not null) continue;
                File.Delete(file);
            }
            if (_logWriter is not null)
            {
                _logWriter.Flush();
                _logWriter.BaseStream.SetLength(0);
                _logWriter.BaseStream.Position = 0;
            }
        }
    }, cancellationToken);

    private string ResolveLogPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetFileName(fileName) != fileName || !IsLogFile(fileName))
            throw new ArgumentException("Select a saved application log file.", nameof(fileName));
        return Path.Combine(_logDirectory, fileName);
    }

    private static bool IsLogFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("WinOptimizer_Log_", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes log files older than <paramref name="maxAgeDays"/> days and
    /// caps the total number of log files to <paramref name="maxFiles"/>,
    /// deleting the oldest files first.
    /// </summary>
    internal static void CleanupOldLogs(string logDirectory, int maxAgeDays = 30, int maxFiles = 50)
    {
        try
        {
            if (!Directory.Exists(logDirectory))
                return;

            var logFiles = Directory.GetFiles(logDirectory, "WinOptimizer_Log_*").Where(IsLogFile)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.CreationTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);

            // Delete files older than maxAgeDays
            for (int i = logFiles.Count - 1; i >= 0; i--)
            {
                if (logFiles[i].CreationTimeUtc < cutoff)
                {
                    try { logFiles[i].Delete(); logFiles.RemoveAt(i); }
                    catch { /* best-effort cleanup */ }
                }
            }

            // If still over maxFiles, delete the oldest
            while (logFiles.Count > maxFiles)
            {
                try { logFiles[0].Delete(); }
                catch { /* best-effort cleanup */ }
                logFiles.RemoveAt(0);
            }
        }
        catch
        {
            // Cleanup is best-effort; don't let it prevent logging from starting
        }
    }

    private void WriteLog(string message, LogLevel level, Exception? exception = null)
    {
        lock (_lockObject)
        {
            try
            {
                var entry = new AppLogEntry
                {
                    Timestamp = DateTimeOffset.Now, Level = level, Message = message,
                    Topic = _topic.Value ?? LogTopics.FromMessage(message),
                    Exception = exception is null ? null : new(exception.GetType().FullName ?? exception.GetType().Name,
                        exception.Message, exception.StackTrace, exception.InnerException?.ToString())
                };

                // Write to file if log writer is available
                if (_logWriter is not null) _logWriter.WriteLine(JsonSerializer.Serialize(entry, LogPageReader.JsonOptions));

            }
            catch (Exception)
            {
                // Logging failed
            }
        }
    }

    // Implement IDisposable pattern to ensure logs are stopped
    public void Dispose()
    {
        StopLog();
        GC.SuppressFinalize(this);
    }
}
