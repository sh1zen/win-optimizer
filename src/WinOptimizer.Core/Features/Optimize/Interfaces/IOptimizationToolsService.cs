using WinOptimizer.Core.Features.Optimize.Models;

namespace WinOptimizer.Core.Features.Optimize.Interfaces;

/// <summary>Explicit feature actions offered by Optimize; individual settings retain their existing owners.</summary>
public enum WindowsFeatureOptimization
{
    OptionalServices,
    Telemetry,
    Cortana,
    Copilot,
    ActivityHistory,
    Advertising,
}

/// <summary>Current global TCP settings, read from the Windows networking cmdlets.</summary>
public sealed record NetworkPerformanceState(string AutoTuningLevel, bool ReceiveSideScaling);

/// <summary>Runs on-demand network and feature operations outside portable configuration presets.</summary>
public interface IOptimizationToolsService
{
    Task<NetworkPerformanceState> ReadNetworkAsync();
    Task<NetworkPerformanceState> EnhanceNetworkAsync();
    Task<IReadOnlyList<NetworkTuningState>> ReadNetworkTuningsAsync();
    /// <summary>Compares the displayed baseline, changes one setting and returns verified native readback.</summary>
    Task<IReadOnlyList<NetworkTuningState>> SetNetworkTuningAsync(string id, string? value, string? expectedValue);
    Task ApplyFeatureAsync(WindowsFeatureOptimization feature);
    /// <summary>Reads whether all applicable restrictions are on; null means no optional targets are installed.</summary>
    Task<bool?> ReadFeatureAsync(WindowsFeatureOptimization feature);
    /// <summary>Applies restrictions when on, or restores enabled policies and default service startup choices when off.</summary>
    Task SetFeatureAsync(WindowsFeatureOptimization feature, bool isOn);
}
