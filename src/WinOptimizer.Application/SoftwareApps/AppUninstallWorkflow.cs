using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Application.SoftwareApps;

/// <summary>Coordinates capture, native uninstall, outcome verification and an explicit residue review.</summary>
public sealed class AppUninstallWorkflow(IUninstallResidueService residues, IUninstallResidueReview review,
    IWindowsAppUninstallService windows, IExternalAppsService external, ITaskProgressService tasks,
    IApplicationModeService mode, ILocalizationService localization) : IAppUninstallWorkflow
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AppUninstallResult> UninstallAsync(IReadOnlyList<ItemDefinition> applications,
        AppUninstallKind kind, bool saveRemovalScripts = true, CancellationToken ct = default, Func<AppUninstallResult, Task>? refreshInventory = null)
    {
        if (applications.Count == 0) return new([], []);
        if (applications.Any(app => string.IsNullOrWhiteSpace(app.Id)) ||
            applications.Select(app => app.Id).Distinct(StringComparer.Ordinal).Count() != applications.Count)
            throw new ArgumentException("Uninstall requires distinct application identities.", nameof(applications));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IUninstallResidueSession? session = null;
        var deferred = false;
        try
        {
            using var operation = mode.BeginSystemOperation();
            session = await RunStageAsync("UninstallResidue_Capture", token =>
                residues.CaptureAsync(applications, CreateProgress(), token), ct).ConfigureAwait(false);
            List<UninstallNativeOutcome> outcomes = [];
            if (kind == AppUninstallKind.Windows)
            {
                ct.ThrowIfCancellationRequested();
                using var cancellation = ct.Register(tasks.CancelCurrentTask);
                // The Windows service owns its coordinated multi-script task and cancellation.
                var result = await windows.UninstallAppsWithOutcomesAsync(applications.ToList(), saveRemovalScripts).ConfigureAwait(false);
                if (result.Success && result.Result != null) outcomes.AddRange(result.Result);
                else outcomes.AddRange(applications.Select(app => new UninstallNativeOutcome(app.Id,
                    false, false, result.ErrorMessage)));
            }
            else
            {
                await RunStageAsync("Progress_Task_UninstallingExternalApps", async token =>
                {
                    using var cancellation = token.Register(tasks.CancelCurrentTask);
                    var progress = tasks.CreateDetailedProgress();
                    for (var index = 0; index < applications.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        var app = applications[index];
                        if (tasks.ConsumeSkipNextRequest())
                        {
                            outcomes.Add(new(app.Id, false, false, localization.GetString("UninstallResidue_Skipped")));
                            continue;
                        }
                        tasks.UpdateDetailedProgress(new TaskProgressDetail
                        {
                            StatusText = localization.GetString("Progress_Uninstalling", app.Name),
                            QueueTotal = applications.Count,
                            QueueCurrent = index + 1,
                            QueueNextItemName = index + 1 < applications.Count ? applications[index + 1].Name : null,
                            IsIndeterminate = true
                        });
                        var result = await external.UninstallAppAsync(app, progress).ConfigureAwait(false);
                        outcomes.Add(new(app.Id, result.Success && result.Result, result.InfoMessage != null,
                            result.InfoMessage ?? result.ErrorMessage));
                    }
                    return true;
                }, ct).ConfigureAwait(false);
            }

            var scan = await RunStageAsync("UninstallResidue_Verifying", token =>
                session.InspectAsync(outcomes, CreateProgress(), token), ct).ConfigureAwait(false);
            var verifiedResult = new AppUninstallResult(scan.Applications, scan.Notices);
            // Reuse the verification evidence for the list before opening a long-lived review.
            if (refreshInventory != null) await refreshInventory(verifiedResult).ConfigureAwait(false);
            deferred = await review.ShowAsync(session, scan, ct).ConfigureAwait(false) == UninstallReviewDisposition.Deferred;
            return verifiedResult;
        }
        finally
        {
            try { if (session != null && !deferred) await session.DisposeAsync().ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
    }

    /// <summary>Owns the footer task and links its cancellation only for stages it starts.</summary>
    private async Task<T> RunStageAsync<T>(string title, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var ownsTask = !tasks.IsTaskRunning;
        var taskCancellation = ownsTask ? tasks.StartTask(localization.GetString(title), true) : tasks.CurrentTaskCancellationSource;
        using var linked = taskCancellation is null ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, taskCancellation.Token);
        try { return await action(linked.Token).ConfigureAwait(false); }
        finally { if (ownsTask) tasks.CompleteTask(); }
    }

    private IProgress<UninstallResidueProgress> CreateProgress() => new StageProgress(tasks);

    /// <summary>Reports in stage order; the task service marshals presentation notifications itself.</summary>
    private sealed class StageProgress(ITaskProgressService tasks) : IProgress<UninstallResidueProgress>
    {
        public void Report(UninstallResidueProgress progress) => tasks.UpdateDetailedProgress(new TaskProgressDetail
        {
            Progress = progress.Total > 0 ? 100d * progress.Completed / progress.Total : null,
            IsIndeterminate = progress.Total <= 0,
            StatusText = progress.Message
        });
    }
}
