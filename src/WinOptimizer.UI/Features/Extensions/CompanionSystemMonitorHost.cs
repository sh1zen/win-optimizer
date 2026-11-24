using System.ComponentModel;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Starts the desktop companion from the shell's SysMonitor settings.</summary>
internal sealed class CompanionSystemMonitorHost : ISystemMonitorWidgetHost
{
    private bool _enabled;
    private int _request;
    public SystemMonitorState State { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => State == SystemMonitorState.Running;
    public event EventHandler? StateChanged;

    public Task ApplyOptionsAsync(SystemMonitorOptions options)
    {
        options.Validate();
        var wasEnabled = _enabled;
        _enabled = options.Enabled;
        if (!_enabled)
        {
            _request++;
            SetState(SystemMonitorState.Disabled);
            return Task.CompletedTask;
        }
        return LaunchAsync(() =>
        {
            if (!wasEnabled || !DesktopExtensionProcess.AreWidgetsRunning()) DesktopExtensionProcess.LaunchWidgets();
        });
    }

    public Task ShowAsync() => _enabled ? LaunchAsync(() => DesktopExtensionProcess.LaunchWidgets(showMonitor: true)) : Task.CompletedTask;

    public Task StopAsync() => DesktopExtensionProcess.StopWidgetsAsync();

    internal void UpdateObservedState(bool enabled)
    {
        _enabled = enabled;
        if (!_enabled)
        {
            _request++;
            SetState(SystemMonitorState.Disabled);
        }
        else if (SystemMonitorAvailability.IsRunning()) SetState(SystemMonitorState.Running);
        else if (State == SystemMonitorState.Running) SetState(SystemMonitorState.Disabled);
    }

    private async Task LaunchAsync(Action action)
    {
        var request = ++_request;
        try
        {
            action();
            if (!SystemMonitorAvailability.IsRunning())
            {
                SetState(SystemMonitorState.Starting);
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (request == _request && wait.Elapsed < TimeSpan.FromSeconds(10) && !SystemMonitorAvailability.IsRunning())
                    await Task.Delay(150);
            }
            if (request != _request) return;
            if (SystemMonitorAvailability.IsRunning()) SetState(SystemMonitorState.Running);
            else SetState(SystemMonitorState.Failed, "SysMonitor did not open in the desktop widget companion.");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        { if (request == _request) SetState(SystemMonitorState.Failed, ex.Message); }
    }

    private void SetState(SystemMonitorState state, string? error = null)
    {
        if (state == State && error == LastError) return;
        State = state;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
