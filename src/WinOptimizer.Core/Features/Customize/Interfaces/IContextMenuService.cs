namespace WinOptimizer.Core.Features.Customize.Interfaces;

/// <summary>A discovered registry registration, with an opaque identity for subsequent edits.</summary>
public sealed record ContextMenuEntry(string Id, string ProgramName, string Name, string Location, bool IsEnabled)
{
    /// <summary>Registered executable or COM server used for local program artwork.</summary>
    public string? ProgramPath { get; init; }
    public string TargetType { get; init; } = "";
    public bool IsMachineWide { get; init; }
}

/// <summary>Discovers registered Explorer menu commands and reversibly enables individual registrations.</summary>
public interface IContextMenuService
{
    Task<IReadOnlyList<ContextMenuEntry>> ReadAsync();
    Task<ContextMenuEntry> SetEnabledAsync(ContextMenuEntry entry, bool enabled);
}
