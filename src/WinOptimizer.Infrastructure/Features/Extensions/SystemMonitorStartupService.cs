using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Owns SysMonitor's per-user scheduled launch and its administrator identity requirement.</summary>
public sealed class SystemMonitorStartupService(IScheduledTaskService tasks, IInteractiveUserService interactiveUser)
    : ISystemMonitorStartupService
{
    private readonly DesktopWidgetStartupRegistration _registration = new(tasks, interactiveUser);

    public Task<OperationResult> ApplyAsync(SystemMonitorOptions options) =>
        _registration.ApplyAsync("SysMonitor", SystemMonitorLaunch.Argument, SystemMonitorLaunch.ShouldStart(options));
}
