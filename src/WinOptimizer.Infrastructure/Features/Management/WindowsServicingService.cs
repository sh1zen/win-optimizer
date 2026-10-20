using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Owns phase ordering, restart gates and cancellation of active servicing commands.</summary>
internal sealed class WindowsServicingService(IWindowsServicingBackend backend, IApplicationModeService mode) : IWindowsServicingService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WindowsServicingReport> RunAsync(WindowsServicingRequest request,
        IProgress<WindowsServicingProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Action) || (request.ResetBase && request.Action != WindowsServicingAction.Cleanup))
            throw new ArgumentException("Invalid Windows servicing action or cleanup options.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var reservation = mode.BeginSystemOperation();
            if (mode.CurrentMode != WinOptimizerMode.Normal)
                throw new InvalidOperationException("Windows servicing requires normal application mode.");
            var preparation = await backend.PrepareAsync(request).ConfigureAwait(false);
            request = preparation.Request;
            var results = new List<WindowsServicingStepResult>();
            var health = WindowsImageHealth.Unknown;
            WindowsServicingReport Report(WindowsServicingState state) => new(request, DateTimeOffset.Now, state, health, results.ToArray());
            if (preparation.RestartPending && request.Action is WindowsServicingAction.Repair or WindowsServicingAction.Cleanup)
                return Report(WindowsServicingState.RestartRequired);
            var steps = Plan(request.Action);
            foreach (var step in steps)
            {
                if (cancellationToken.IsCancellationRequested) return Report(WindowsServicingState.Cancelled);
                progress?.Report(new(step, results.Count, steps.Length));
                WindowsServicingStepResult result;
                try { result = await backend.RunStepAsync(step, request, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { return Report(WindowsServicingState.Cancelled); }
                catch (Exception ex) when (ex is UtilityOperationException or IOException or UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or InvalidOperationException)
                { result = new(step, false, null, WindowsImageHealth.Unknown, false, ex.Message); }
                results.Add(result);
                if (!result.Succeeded) return Report(WindowsServicingState.Failed);
                if (step is WindowsServicingStep.CheckHealth or WindowsServicingStep.ScanHealth or WindowsServicingStep.RestoreHealth)
                    health = result.Health;
                if (result.RestartNeeded) return Report(WindowsServicingState.RestartRequired);
                if ((request.Action == WindowsServicingAction.Cleanup && step == WindowsServicingStep.CheckHealth || step == WindowsServicingStep.RestoreHealth)
                    && health != WindowsImageHealth.Healthy)
                    return Report(WindowsServicingState.NeedsAttention);
            }
            if (cancellationToken.IsCancellationRequested) return Report(WindowsServicingState.Cancelled);
            if (preparation.RestartPending) return Report(WindowsServicingState.RestartRequired);
            if (request.Action != WindowsServicingAction.AnalyzeStore && health != WindowsImageHealth.Healthy)
                return Report(WindowsServicingState.NeedsAttention);
            return Report(results.Any(result => result.Step is WindowsServicingStep.VerifyFiles or WindowsServicingStep.RepairFiles)
                ? WindowsServicingState.ReviewFileResults : WindowsServicingState.Completed);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Repair uses the component store before SFC; component cleanup is an independent workflow.</summary>
    private static WindowsServicingStep[] Plan(WindowsServicingAction action) => action switch
    {
        WindowsServicingAction.QuickCheck => [WindowsServicingStep.CheckHealth],
        WindowsServicingAction.Diagnosis => [WindowsServicingStep.ScanHealth, WindowsServicingStep.AnalyzeStore, WindowsServicingStep.VerifyFiles],
        WindowsServicingAction.AnalyzeStore => [WindowsServicingStep.AnalyzeStore],
        WindowsServicingAction.Repair => [WindowsServicingStep.RestoreHealth, WindowsServicingStep.RepairFiles],
        WindowsServicingAction.Cleanup => [WindowsServicingStep.CheckHealth, WindowsServicingStep.AnalyzeStore, WindowsServicingStep.Cleanup],
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

internal sealed record WindowsServicingPreparation(WindowsServicingRequest Request, bool RestartPending);

/// <summary>Isolates privileges, source validation, restart discovery and the built-in servicing tools.</summary>
internal interface IWindowsServicingBackend
{
    Task<WindowsServicingPreparation> PrepareAsync(WindowsServicingRequest request);
    Task<WindowsServicingStepResult> RunStepAsync(WindowsServicingStep step, WindowsServicingRequest request, CancellationToken cancellationToken);
}
