namespace WinOptimizer.Core.Features.Drivers.Models;

public enum DriverRemovalKind { Package, Device }

/// <summary>An exact package or device identity selected from a completed scan.</summary>
public sealed record DriverRemovalTarget(DriverRemovalKind Kind, string Id);

/// <summary>Windows' result for one removal, including changes that require a restart.</summary>
public sealed record DriverRemovalOutcome(DriverRemovalTarget Target, bool Success, bool RestartRequired, string Error = "");

/// <summary>Retains completed removals and failures when cancellation stops a batch.</summary>
public sealed record DriverRemovalReport(IReadOnlyList<DriverRemovalOutcome> Outcomes, bool Cancelled);
