using System.Collections.Generic;

namespace WinOptimizer.Core.Features.Common.Models;

public sealed record ImportOptions
{
    public bool ProcessWindowsAppsRemoval { get; init; }
    public bool ProcessExternalAppsRemoval { get; init; }
    public bool ApplyThemeWallpaper { get; init; }
    public bool ApplyCleanTaskbar { get; init; }
    public bool ApplyCleanStartMenu { get; init; }
    public bool IsWindowsDefaults { get; init; }
    public IReadOnlyCollection<string> ActionOnlySubsections { get; init; } = new HashSet<string>();
}
