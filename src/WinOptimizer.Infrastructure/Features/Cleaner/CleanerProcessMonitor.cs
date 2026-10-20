using System.Diagnostics;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>External boundary for application activity checks during scans, backup and recovery.</summary>
public interface ICleanerProcessMonitor { bool IsRunning(string processName); }

public sealed class WindowsCleanerProcessMonitor : ICleanerProcessMonitor
{
    public bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
