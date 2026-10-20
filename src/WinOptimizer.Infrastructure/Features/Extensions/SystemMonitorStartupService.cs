using System.Security.Principal;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Owns SysMonitor's per-user scheduled launch and its administrator identity requirement.</summary>
public sealed class SystemMonitorStartupService(IScheduledTaskService tasks, IInteractiveUserService interactiveUser)
    : ISystemMonitorStartupService
{
    public Task<OperationResult> ApplyAsync(SystemMonitorOptions options)
    {
        var enabled = SystemMonitorLaunch.ShouldStart(options);
        if (enabled && interactiveUser.IsOtsElevation)
            return Task.FromResult(OperationResult.Failed(
                "SysMonitor startup requires signing in with an administrator account. The current desktop account cannot run this elevated application automatically."));
        using var identity = WindowsIdentity.GetCurrent();
        var sid = interactiveUser.InteractiveUserSid ?? identity.User?.Value
            ?? throw new InvalidOperationException("Unable to identify the desktop account for SysMonitor startup.");
        return tasks.ConfigureUserLogonApplicationAsync($"SysMonitor-{sid}",
            Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate the application executable."),
            SystemMonitorLaunch.Argument, sid, enabled);
    }
}
