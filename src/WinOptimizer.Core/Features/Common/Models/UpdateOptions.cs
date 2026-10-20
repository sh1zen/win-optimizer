namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>Controls the independent automatic application and removal-script update checks.</summary>
public sealed record UpdateOptions
{
    public bool CheckApplicationUpdatesAutomatically { get; init; } = true;
    public bool UpdateRemovalScriptsAutomatically { get; init; } = true;
}
