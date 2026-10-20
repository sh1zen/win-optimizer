namespace WinOptimizer.Core.Features.SoftwareApps.Models;

/// <summary>A batch's capability and optional-feature installation statuses.</summary>
public sealed record WindowsComponentStatus(
    IReadOnlyDictionary<string, bool> Capabilities,
    IReadOnlyDictionary<string, bool> Features);
