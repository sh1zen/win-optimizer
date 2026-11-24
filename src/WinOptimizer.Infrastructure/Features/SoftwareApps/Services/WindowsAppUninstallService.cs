using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Enums;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Routes Windows catalogue entries through their registered uninstallers or Windows package servicing.</summary>
public class WindowsAppUninstallService(
    ILogService logService,
    IBloatRemovalService bloatRemovalService,
    IExternalAppUninstallService desktop,
    ITaskProgressService taskProgressService,
    ILocalizationService localization,
    IChangeHistoryService changeHistory) : IWindowsAppUninstallService
{
    public async Task<OperationResult<IReadOnlyList<UninstallNativeOutcome>>> UninstallAppsWithOutcomesAsync(
        List<ItemDefinition> apps, bool saveRemovalScripts = true)
    {
        if (apps is null || apps.Count == 0)
            return OperationResult<IReadOnlyList<UninstallNativeOutcome>>.Failed("No apps provided");
        var ownsTask = !taskProgressService.IsTaskRunning;
        try
        {
            if (ownsTask) taskProgressService.StartTask(localization.GetString("Progress_Task_RemovingWindowsApps"), isIndeterminate: true);
            var outcomes = await RemoveAsync(apps, taskProgressService.CreateDetailedProgress(), saveRemovalScripts,
                taskProgressService.GetCurrentCancellationToken()).ConfigureAwait(false);
            return OperationResult<IReadOnlyList<UninstallNativeOutcome>>.Succeeded(outcomes);
        }
        catch (OperationCanceledException) { return OperationResult<IReadOnlyList<UninstallNativeOutcome>>.Cancelled(); }
        catch (Exception ex)
        {
            logService.LogError("Failed to remove Windows apps.", ex);
            return OperationResult<IReadOnlyList<UninstallNativeOutcome>>.Failed(ex.Message, ex);
        }
        finally { if (ownsTask) taskProgressService.CompleteTask(); }
    }

    private async Task<IReadOnlyList<UninstallNativeOutcome>> RemoveAsync(List<ItemDefinition> apps,
        IProgress<TaskProgressDetail>? progress, bool saveRemovalScripts, CancellationToken ct)
    {
        var outcomes = new UninstallNativeOutcome?[apps.Count];
        List<ItemDefinition> packages = [];
        List<int> packageIndices = [];
        try
        {
            for (var i = 0; i < apps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var app = apps[i];
                OperationResult<bool>? result = null;
                if (string.IsNullOrEmpty(app.CapabilityName) && string.IsNullOrEmpty(app.OptionalFeatureName))
                {
                    if (app.AppxPackageName is not { Length: > 0 } || app.RegistryUninstallKeyPath is not null ||
                        app.IsInstalled && app.DetectedVia == DetectionSource.Registry)
                        result = await desktop.UninstallAsync(app, progress, ct).ConfigureAwait(false);
                    else if (app.RegistryDisplayName is not null || app.RegistrySubKeyName is not null ||
                        app.IsInstalled && app.DetectedVia == DetectionSource.WinGet && app.WinGetPackageId is { Length: > 0 })
                        result = await desktop.TryUninstallRegisteredAsync(app, ct).ConfigureAwait(false);
                }

                if (result is null)
                {
                    packages.Add(app);
                    packageIndices.Add(i);
                    continue;
                }
                outcomes[i] = new(app.Id, result.Success && result.Result, result.Success && result.InfoMessage is not null,
                    result.InfoMessage ?? result.ErrorMessage) { Cancelled = result.IsCancelled && ct.IsCancellationRequested };
                if (outcomes[i] is { Success: true, Deferred: false }) changeHistory.LogAppChange(app.Name, AppChangeKind.Removed);
            }

            if (packages.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var packageOutcomes = await bloatRemovalService.ExecuteBloatRemovalAsync(packages, saveRemovalScripts, progress, ct)
                    .ConfigureAwait(false);
                var byApp = packageOutcomes.ToDictionary(outcome => outcome.AppId, StringComparer.Ordinal);
                foreach (var index in packageIndices)
                {
                    var app = apps[index];
                    var outcome = byApp[app.Id];
                    outcomes[index] = outcome;
                    if (outcome.Success && !outcome.Deferred) changeHistory.LogAppChange(app.Name, AppChangeKind.Removed);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Keep completed desktop outcomes when a later item or the package batch is interrupted.
            return outcomes.Select((outcome, index) => outcome ?? new UninstallNativeOutcome(apps[index].Id, false, false,
                localization.GetString("Restore_Cancelled")) { Cancelled = true }).ToArray();
        }

        return outcomes.Select(outcome => outcome ?? throw new InvalidOperationException("Missing native uninstall outcome.")).ToArray();
    }
}
