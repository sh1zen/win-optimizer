using WinOptimizer.Core.Features.Common.Constants;
using System;
using System.IO;

namespace WinOptimizer.Core.Features.Common.Services;

/// <summary>
/// Static logger for pre-DI startup diagnostics.
/// Writes to %ProgramData%\win-optimizer\Logs\WinOptimizerStartupLog.txt.
/// Overwrites on first call per app run, appends subsequent calls. Thread-safe.
/// </summary>
public static class StartupLogger
{
    private static readonly string LogPath = Path.Combine(AppDataPaths.Logs, "WinOptimizerStartupLog.txt");

    private static readonly object Lock = new object();
    private static bool _firstCall = true;

    public static void Log(string message)
    {
        lock (Lock)
        {
            try
            {
                if (_firstCall)
                {
                    var dir = Path.GetDirectoryName(LogPath);
                    if (dir != null) Directory.CreateDirectory(dir);
                    File.WriteAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
                    _firstCall = false;
                }
                else
                {
                    File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch { /* Static pre-DI logger — nowhere to log the failure, and throwing would crash startup */ }
        }
    }

    public static void Log(string source, string message)
    {
        Log($"[{source}] {message}");
    }
}
