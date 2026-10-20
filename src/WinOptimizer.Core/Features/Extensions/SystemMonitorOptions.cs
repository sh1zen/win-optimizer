namespace WinOptimizer.Core.Features.Extensions;

public enum SystemMonitorTheme { System, Light, Dark }

/// <summary>Saved SysMonitor preferences. Disabled extensions perform no sampling.</summary>
public sealed record SystemMonitorOptions
{
    public const string PreferenceKey = "Extensions.SysMonitor";
    public bool Enabled { get; init; }
    public bool StartWithWindows { get; init; }
    public bool IsCompact { get; init; } = true;
    public bool AlwaysOnTop { get; init; } = true;
    public SystemMonitorTheme Theme { get; init; }
    public double ScalePercent { get; init; } = 100;
    public int RefreshIntervalSeconds { get; init; } = 2;
    public bool ShowTemperatures { get; init; } = true;
    public bool ShowFans { get; init; } = true;
    public bool ShowMemory { get; init; } = true;
    public bool ShowCpu { get; init; } = true;
    public bool ShowNetwork { get; init; } = true;
    public bool ShowGpuUsage { get; init; }
    public bool ShowGpuMemory { get; init; }
    public bool ShowClockSpeed { get; init; }
    public bool ShowPower { get; init; }
    public bool ShowVoltage { get; init; }
    public bool ShowStorageActivity { get; init; }
    public bool ShowStorageThroughput { get; init; }

    /// <summary>Resolves the saved visibility of each optional hardware section.</summary>
    public bool IsMetricVisible(SystemMonitorMetricKind kind) => kind switch
    {
        SystemMonitorMetricKind.GpuUsage => ShowGpuUsage,
        SystemMonitorMetricKind.GpuMemory => ShowGpuMemory,
        SystemMonitorMetricKind.ClockSpeed => ShowClockSpeed,
        SystemMonitorMetricKind.Power => ShowPower,
        SystemMonitorMetricKind.Voltage => ShowVoltage,
        SystemMonitorMetricKind.StorageActivity => ShowStorageActivity,
        SystemMonitorMetricKind.StorageThroughput => ShowStorageThroughput,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Rejects unsupported saved choices before persistence or widget activation.</summary>
    public void Validate()
    {
        if (!double.IsFinite(ScalePercent) || ScalePercent is < 50 or > 200)
            throw new ArgumentException("SysMonitor scale must be between 50% and 200%.");
        if (RefreshIntervalSeconds is not (1 or 2 or 5))
            throw new ArgumentException("SysMonitor refresh interval must be 1, 2 or 5 seconds.");
        if (!Enum.IsDefined(Theme)) throw new ArgumentException("Invalid SysMonitor theme.");
    }
}
