namespace WinOptimizer.Core.Features.Management;

public enum WindowsServicingAction { QuickCheck, Diagnosis, AnalyzeStore, Repair, Cleanup }
public enum WindowsServicingStep { CheckHealth, ScanHealth, AnalyzeStore, RestoreHealth, VerifyFiles, RepairFiles, Cleanup }
public enum WindowsImageHealth { Unknown, Healthy, Repairable, NonRepairable }
public enum WindowsServicingState { Completed, ReviewFileResults, NeedsAttention, Failed, RestartRequired, Cancelled }

/// <summary>Immutable user intent; custom sources apply only to image repair and ResetBase only to cleanup.</summary>
public sealed record WindowsServicingRequest(WindowsServicingAction Action, string SourcePath = "", int WimIndex = 1,
    bool LimitAccess = false, bool ResetBase = false);
public sealed record WindowsServicingStepResult(WindowsServicingStep Step, bool Succeeded, int? ExitCode,
    WindowsImageHealth Health, bool RestartNeeded, string Output);
public sealed record WindowsServicingProgress(WindowsServicingStep Step, int Completed, int Total);
public sealed record WindowsServicingReport(WindowsServicingRequest Request, DateTimeOffset CompletedAt,
    WindowsServicingState State, WindowsImageHealth ImageHealth, IReadOnlyList<WindowsServicingStepResult> Steps);

/// <summary>Runs explicit local Windows servicing workflows and returns completed steps even when stopped.</summary>
public interface IWindowsServicingService
{
    Task<WindowsServicingReport> RunAsync(WindowsServicingRequest request,
        IProgress<WindowsServicingProgress>? progress, CancellationToken cancellationToken);
}
