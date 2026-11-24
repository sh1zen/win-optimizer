using System.Security.Principal;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Registers the widget companion for the interactive administrator account.</summary>
internal sealed class DesktopWidgetStartupRegistration(IScheduledTaskService tasks, IInteractiveUserService interactiveUser)
{
    public Task<OperationResult> ApplyAsync(string widget, string argument, bool enabled)
    {
        if (enabled && interactiveUser.IsOtsElevation)
            return Task.FromResult(OperationResult.Failed(
                $"{widget} startup requires signing in with an administrator account. The current desktop account cannot run this elevated application automatically."));
        using var identity = WindowsIdentity.GetCurrent();
        var sid = interactiveUser.InteractiveUserSid ?? identity.User?.Value
            ?? throw new InvalidOperationException($"Unable to identify the desktop account for {widget} startup.");
        return tasks.ConfigureUserLogonApplicationAsync($"{widget}-{sid}",
            Path.Combine(AppContext.BaseDirectory, DesktopWidgetLaunch.Executable), argument, sid, enabled);
    }
}

/// <summary>Maintains Notch's independent sign-in registration for the widget companion.</summary>
public sealed class NotchStartupService(IScheduledTaskService tasks, IInteractiveUserService interactiveUser)
    : INotchStartupService
{
    private readonly DesktopWidgetStartupRegistration _registration = new(tasks, interactiveUser);

    public Task<OperationResult> ApplyAsync(bool enabled) =>
        _registration.ApplyAsync("Notch", DesktopWidgetLaunch.NotchArgument, enabled);
}
