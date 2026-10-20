namespace WinOptimizer.Core.Features.SoftwareApps.Models;

public enum LeftoverKind { Registration, Msi, Msp, Exe, Folder }

/// <summary>A scan-issued selection; cleanup accepts the ID and never a path supplied by the view.</summary>
public sealed record LeftoverItem(Guid Id, string Name, LeftoverKind Kind, string Location,
    long Bytes, bool CanRemove, string Reason);
public sealed record LeftoverScan(IReadOnlyList<LeftoverItem> Items, IReadOnlyList<string> Notices);
public sealed record LeftoverCleanupResult(int Removed, IReadOnlyList<string> Errors, Guid? BackupId);
