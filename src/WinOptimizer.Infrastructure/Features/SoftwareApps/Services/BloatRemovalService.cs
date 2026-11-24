using System.Text.Json;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Exceptions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Core.Features.SoftwareApps.Utilities;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Executes each selection independently of the persistent sign-in removal plan.</summary>
public sealed class BloatRemovalService(ILogService log, IScheduledTaskService tasks, IPowerShellRunner runner,
    IFileSystemService files) : IBloatRemovalService
{
    private const string TaskPath = @"\win-optimizer\" + ScriptPaths.PackageRemovalTaskName;
    internal static SemaphoreSlim PersistentPlanGate { get; } = new(1, 1);

    public async Task<IReadOnlyList<UninstallNativeOutcome>> ExecuteBloatRemovalAsync(IReadOnlyList<ItemDefinition> apps,
        bool saveRemovalScript, IProgress<TaskProgressDetail>? progress = null, CancellationToken ct = default)
    {
        var requests = apps.Select(app => (App: app, Targets: TargetsFor(app))).ToArray();
        var targets = requests.SelectMany(request => request.Targets).DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (targets.Length == 0)
            return requests.Select(request => new UninstallNativeOutcome(request.App.Id, false, false, "No Windows servicing identity is available.")).ToArray();

        IReadOnlyDictionary<string, RemovalReceipt> receipts;
        try
        {
            ct.ThrowIfCancellationRequested();
            var eventName = ct.CanBeCanceled ? $@"Local\WinOptimizer.PackageRemoval.{Guid.NewGuid():N}" : null;
            using var cancelSignal = eventName is null ? null : new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            using var registration = cancelSignal is null ? default : ct.Register(() => cancelSignal.Set());
            // Let servicing finish the active operation and return receipts before the process exits.
            var output = await runner.RunScriptAsync(Generate(targets, reportResults: true, cancellationEventName: eventName),
                progress, CancellationToken.None).ConfigureAwait(false);
            receipts = ReadReceipts(output);
        }
        catch (ExecutionPolicyException ex)
        {
            ct.ThrowIfCancellationRequested();
            log.LogWarning($"PowerShell blocked Windows package servicing: {ex.Message}");
            var result = saveRemovalScript ? await SavePlanAsync(targets, ct).ConfigureAwait(false) : null;
            return requests.Select(request =>
            {
                var scheduled = result?.Success == true && request.Targets.Length > 0;
                return new UninstallNativeOutcome(request.App.Id, scheduled, scheduled, scheduled
                    ? "Windows package removal is scheduled for the next sign-in."
                    : result?.ErrorMessage ?? "PowerShell blocked package removal.");
            }).ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            log.LogError("Windows package servicing could not confirm the result.", ex);
            return requests.Select(request => new UninstallNativeOutcome(request.App.Id, false, false, ex.Message)).ToArray();
        }

        var cancelled = ct.IsCancellationRequested;
        var outcomes = requests.Select(request => BuildOutcome(request.App.Id, request.Targets, receipts, cancelled)).ToArray();
        if (saveRemovalScript)
        {
            var completed = targets.Where(target => receipts.TryGetValue(target.Key, out var receipt) && receipt.Error is null).ToArray();
            if (completed.Length > 0)
            {
                // Native servicing has finished. Publish the requested persistent plan as one unit.
                var result = await SavePlanAsync(completed, CancellationToken.None).ConfigureAwait(false);
                if (!result.Success)
                {
                    for (var index = 0; index < outcomes.Length; index++)
                    {
                        var outcome = outcomes[index];
                        if (outcome.Success)
                            outcomes[index] = outcome with { Message = string.Join(" ", new[] { outcome.Message, result.ErrorMessage }.OfType<string>()) };
                    }
                }
            }
        }
        return outcomes;
    }

    private static UninstallNativeOutcome BuildOutcome(string id, RemovalTarget[] targets,
        IReadOnlyDictionary<string, RemovalReceipt> receipts, bool cancelled)
    {
        if (targets.Length == 0) return new(id, false, false, "No Windows servicing identity is available.");
        var skipped = cancelled && targets.Any(target => !receipts.ContainsKey(target.Key));
        var removed = targets.Select(target => receipts.GetValueOrDefault(target.Key) ??
            new RemovalReceipt(target.Key, skipped ? $"Removal of {target.Name} was cancelled."
                : $"Windows did not confirm the removal of {target.Name}.", false)).ToArray();
        var failures = removed.Where(receipt => receipt.Error is not null).Select(receipt => receipt.Error).ToArray();
        if (failures.Length > 0)
            return new(id, false, false, string.Join(" ", failures)) { Cancelled = skipped };
        var restart = removed.Any(receipt => receipt.RestartRequired);
        return new(id, true, restart, restart ? "Restart Windows to finish removal." : null);
    }

    private static Dictionary<string, RemovalReceipt> ReadReceipts(string output)
    {
        using var lines = new StringReader(output);
        string? report = null;
        while (lines.ReadLine() is { } line)
        {
            if (!line.StartsWith(BloatRemovalScriptGenerator.ResultPrefix, StringComparison.Ordinal)) continue;
            if (report is not null) throw new IOException("Windows returned multiple removal reports.");
            report = line[BloatRemovalScriptGenerator.ResultPrefix.Length..];
        }
        if (report is null) throw new IOException("Windows did not return a package removal report.");
        var values = JsonSerializer.Deserialize<RemovalReceipt[]>(report) ?? throw new IOException("The removal report is empty.");
        var receipts = new Dictionary<string, RemovalReceipt>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
            if (value is null || string.IsNullOrWhiteSpace(value.Key) || !receipts.TryAdd(value.Key, value))
                throw new IOException("The removal report contains an invalid or duplicate identity.");
        return receipts;
    }

    private async Task<OperationResult> SavePlanAsync(RemovalTarget[] selected, CancellationToken ct)
    {
        await PersistentPlanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await ReadPlanAsync(ct).ConfigureAwait(false);
            var merged = existing.Targets.Concat(selected).DistinctBy(target => target.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            var registered = await tasks.IsTaskRegisteredAsync(ScriptPaths.PackageRemovalTaskName).ConfigureAwait(false);
            if (registered && await tasks.IsTaskEnabledAsync(TaskPath).ConfigureAwait(false) is not true)
                return PlanFailure("The existing sign-in removal task is disabled or its state could not be verified.");
            var content = Generate(merged);
            files.CreateDirectory(ScriptPaths.ScriptsDirectory);
            await PublishPlanAsync(files, ScriptPaths.PackageRemovalScript, content, existing.Content is not null, ct).ConfigureAwait(false);
            if (registered) return OperationResult.Succeeded();
            var created = false;
            var enabled = false;
            try
            {
                var result = await tasks.RegisterScheduledTaskAsync(new RemovalScript
                {
                    Name = ScriptPaths.PackageRemovalTaskName, TargetScheduledTaskName = ScriptPaths.PackageRemovalTaskName, Content = content,
                    ActualScriptPath = ScriptPaths.PackageRemovalScript, RunOnStartup = false
                }).ConfigureAwait(false);
                created = result.Success;
                if (!created) return PlanFailure(result.ErrorMessage);
                enabled = await tasks.IsTaskEnabledAsync(TaskPath).ConfigureAwait(false) is true;
                return enabled ? result : PlanFailure("Windows did not confirm that the sign-in removal task is enabled.");
            }
            finally
            {
                if (!enabled) await RestorePreviousPlanAsync(existing.Content, created).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
        {
            return PlanFailure(ex.Message);
        }
        finally { PersistentPlanGate.Release(); }
    }

    private async Task RestorePreviousPlanAsync(string? previous, bool created)
    {
        try
        {
            if (created)
            {
                var result = await tasks.UnregisterScheduledTaskAsync(ScriptPaths.PackageRemovalTaskName).ConfigureAwait(false);
                if (!result.Success) log.LogWarning($"Could not retire the unverified removal task: {result.ErrorMessage}");
            }
        }
        finally
        {
            if (previous is null) files.DeleteFile(ScriptPaths.PackageRemovalScript);
            else await PublishPlanAsync(files, ScriptPaths.PackageRemovalScript, previous, replace: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private OperationResult PlanFailure(string? error)
    {
        var message = $"Could not save Windows package removal for future sign-ins. {error}".TrimEnd();
        log.LogWarning(message);
        return OperationResult.Failed(message);
    }

    private async Task<(string? Content, RemovalTarget[] Targets)> ReadPlanAsync(CancellationToken ct)
    {
        if (!files.FileExists(ScriptPaths.PackageRemovalScript)) return (null, []);
        var content = await files.ReadAllTextAsync(ScriptPaths.PackageRemovalScript, ct).ConfigureAwait(false);
        var targets = BloatRemovalScriptGenerator.ExtractArrayFromScript(content, "packages").Select(name => new RemovalTarget("package", name))
            .Concat(BloatRemovalScriptGenerator.ExtractArrayFromScript(content, "capabilities").Select(name => new RemovalTarget("capability", name)))
            .Concat(BloatRemovalScriptGenerator.ExtractArrayFromScript(content, "optionalFeatures").Select(name => new RemovalTarget("feature", name)))
            .ToArray();
        return (content, targets);
    }

    internal static async Task PublishPlanAsync(IFileSystemService files, string path, string content, bool replace, CancellationToken ct)
    {
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await files.WriteAllTextAsync(pending, content, ct).ConfigureAwait(false);
            if (replace) files.ReplaceFile(pending, path);
            else files.MoveFile(pending, path);
        }
        finally { files.DeleteFile(pending); }
    }

    private static RemovalTarget[] TargetsFor(ItemDefinition app)
    {
        if (!string.IsNullOrWhiteSpace(app.CapabilityName)) return [new("capability", app.CapabilityName)];
        if (!string.IsNullOrWhiteSpace(app.OptionalFeatureName)) return [new("feature", app.OptionalFeatureName)];
        return app.AppxPackageName?.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => new RemovalTarget("package", name)).ToArray() ?? [];
    }

    private static string Generate(IEnumerable<RemovalTarget> targets, bool reportResults = false, string? cancellationEventName = null)
    {
        var byKind = targets.ToLookup(target => target.Kind, target => target.Name);
        return BloatRemovalScriptGenerator.GenerateScript(byKind["package"].ToList(), byKind["capability"].ToList(),
            byKind["feature"].ToList(), reportResults, cancellationEventName);
    }

    private sealed record RemovalTarget(string Kind, string Name)
    {
        public string Key => $"{Kind}:{Name}";
    }

    private sealed record RemovalReceipt(string Key, string? Error, bool RestartRequired);
}
