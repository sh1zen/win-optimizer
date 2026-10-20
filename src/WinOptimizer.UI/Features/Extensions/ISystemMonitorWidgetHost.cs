using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions;

public enum SystemMonitorState { Disabled, Starting, Running, Failed }

/// <summary>Desktop lifetime boundary for the optional SysMonitor window.</summary>
public interface ISystemMonitorWidgetHost
{
    SystemMonitorState State { get; }
    string? LastError { get; }
    bool IsRunning { get; }
    event EventHandler? StateChanged;
    event EventHandler? DisableRequested;
    event EventHandler? ToggleSizeRequested;
    event EventHandler? SettingsRequested;
    Task ApplyOptionsAsync(SystemMonitorOptions options);
    Task ShowAsync();
    Task StopAsync();
}
