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
    Task ApplyOptionsAsync(SystemMonitorOptions options);
    Task ShowAsync();
    Task StopAsync();
}

/// <summary>Input raised by an actual widget in its host process.</summary>
public interface ISystemMonitorWidgetInput
{
    event EventHandler? DisableRequested;
    event EventHandler? ToggleSizeRequested;
    event EventHandler? SettingsRequested;
}
