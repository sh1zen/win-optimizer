using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Services;

/// <summary>Identifies unassigned packages conservatively and filters official offers using package evidence.</summary>
public sealed class DriverScanService(IDriverInventorySource inventorySource, IEnumerable<IDriverUpdateSource> sources,
    IDriverPackageService? packages = null) : IDriverScanService
{
    public async Task<DriverScanReport> ScanAsync(DriverScanOptions options, IProgress<DriverScanProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("Drivers_Inventory") { Phase = DriverScanPhase.DetectDevices });
        var inventory = await inventorySource.ReadAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var boundInfs = inventory.Devices.Select(device => device.InfName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unreferenced = inventory.BindingsComplete
            ? inventory.Packages.Where(package => !package.Inbox && !package.BootCritical && !boundInfs.Contains(package.PublishedName)).ToArray()
            : [];
        var disconnected = inventory.Devices.Where(device => !device.Present).ToArray();
        var context = new DriverDiscoveryContext(inventory, options);
        var enabled = sources.Where(source => options.Sources.HasFlag(source.Source)).ToArray();
        var searches = new DriverUpdateSearchResult[enabled.Length];
        progress?.Report(new("Drivers_SearchingUpdates") { Phase = DriverScanPhase.CheckSources });
        await Parallel.ForEachAsync(Enumerable.Range(0, enabled.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (index, token) =>
            {
                try { searches[index] = await enabled[index].SearchAsync(context, progress, token).ConfigureAwait(false); }
                catch (DriverScanException ex) { searches[index] = new([], [ex.Message]) { State = DriverSourceState.Unavailable }; }
            }).ConfigureAwait(false);

        var candidates = DriverCatalogMatching.Deduplicate(searches.SelectMany(search => search.Updates)).ToArray();
        List<string> notices = [.. inventory.Notices];
        var failedVerification = new HashSet<DriverSources>();
        if (options.OnlyWhqlUpdates && packages is not null)
        {
            var queued = Enumerable.Range(0, candidates.Length).Where(index =>
                candidates[index].Catalog is { Verification: DriverVerification.Metadata, LicenseRequired: false, Payloads.Length: > 0 } catalog &&
                catalog.References.All(reference => reference.Source != DriverSources.WindowsUpdate) &&
                DriverCatalogMatching.Targets(catalog.Target, inventory.System) &&
                candidates[index].SizeBytes is > 0 and <= 256L * 1024 * 1024 &&
                catalog.Payloads.All(payload => payload.Url.AbsolutePath.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) ||
                    payload.Url.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    payload.Url.AbsolutePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                    payload.Url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(index => candidates[index].Catalog!.ReleaseDate)
                .DistinctBy(index => (candidates[index].Catalog!.References[0].Source, candidates[index].Catalog!.Family, candidates[index].Catalog!.Branch))
                .Take(Math.Clamp(options.AutomaticVerificationLimit, 0, 8)).ToArray();
            for (var index = 0; index < queued.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new("Drivers_VerifyingPackages", index, queued.Length));
                var item = queued[index];
                try
                {
                    candidates[item] = (await packages.AcquireAsync(candidates[item], inventory, false, null, ct)
                        .ConfigureAwait(false)).Update;
                    if (candidates[item].Catalog is { Verification: DriverVerification.Failed } failed)
                        failedVerification.Add(failed.References[0].Source);
                }
                catch (DriverScanException ex)
                {
                    var candidate = candidates[item];
                    var catalog = candidate.Catalog!;
                    failedVerification.Add(catalog.References[0].Source);
                    candidates[item] = candidate with { Catalog = catalog with { Verification = DriverVerification.Failed, VerificationDetail = ex.Message } };
                    notices.Add($"{candidate.Title}: {ex.Message}");
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new("Drivers_PreparingResults") { Phase = DriverScanPhase.PrepareResults });
        var outcomes = searches.Select((search, index) => new DriverSourceOutcome(enabled[index].Source,
            search.State == DriverSourceState.Success && failedVerification.Contains(enabled[index].Source)
                ? DriverSourceState.VerificationFailed : search.State,
            search.Updates.Count, search.Notices, search.SupportPage)).ToArray();
        notices.AddRange(searches.SelectMany(search => search.Notices));
        List<DriverUpdateMatch> updates = [];
        List<DriverUpdateMatch> filtered = [];
        foreach (var candidate in DriverCatalogMatching.Deduplicate(candidates))
        {
            ct.ThrowIfCancellationRequested();
            var match = DriverCatalogMatching.Evaluate(candidate, inventory);
            if (match.Applicability == DriverApplicability.Incompatible ||
                (options.OnlyWhqlUpdates && match.Update.Certification != DriverCertification.Whql))
            {
                filtered.Add(match);
                continue;
            }
            updates.Add(match);
        }
        ct.ThrowIfCancellationRequested();
        return new(inventory, unreferenced, disconnected, DriverCatalogMatching.MarkNewest(updates),
            outcomes.Any(outcome => outcome.State is DriverSourceState.Success or DriverSourceState.Partial or DriverSourceState.VerificationFailed),
            filtered.Count, notices, options) { Sources = outcomes, Filtered = filtered };
    }

}
