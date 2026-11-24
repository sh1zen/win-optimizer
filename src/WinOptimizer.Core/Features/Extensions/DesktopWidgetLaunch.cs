namespace WinOptimizer.Core.Features.Extensions;

/// <summary>Defines the companion executable and recognizes existing widget logon commands.</summary>
public static class DesktopWidgetLaunch
{
    public const string Executable = "win-optimizer-widgets.exe";
    public const string StartupArgument = "--startup";
    public const string NotchArgument = "--notch-startup";

    public static bool IsAutomatic(IEnumerable<string> arguments) => arguments.Any(argument =>
        string.Equals(argument, SystemMonitorLaunch.Argument, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, NotchArgument, StringComparison.OrdinalIgnoreCase));
}
