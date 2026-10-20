using Microsoft.UI.Xaml;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Owns the native widget resource; enables lifetime tests without opening desktop windows.</summary>
public interface ISystemMonitorWindow
{
    event EventHandler? DisableRequested;
    event EventHandler? ToggleSizeRequested;
    event EventHandler? SettingsRequested;
    event EventHandler? ClosedBySystem;
    void ApplyOptions(SystemMonitorOptions options, ElementTheme effectiveTheme);
    void Show();
    void PlaceInitially();
    void CloseFromHost();
    void Publish(SystemMonitorSnapshot snapshot);
    void ShowError(string error);
    void RefreshLabels();
}
