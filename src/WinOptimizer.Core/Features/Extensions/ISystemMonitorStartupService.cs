using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Extensions;

/// <summary>Reconciles the current user's automatic desktop widget launch.</summary>
public interface ISystemMonitorStartupService
{
    Task<OperationResult> ApplyAsync(SystemMonitorOptions options);
}

/// <summary>Identifies automatic launches consistently across scheduling and single-instance activation.</summary>
public static class SystemMonitorLaunch
{
    public const string Argument = "--sysmonitor-startup";

    public static bool IsAutomatic(IEnumerable<string> arguments) =>
        arguments.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    public static bool ShouldStart(SystemMonitorOptions options) => options.Enabled && options.StartWithWindows;
}
