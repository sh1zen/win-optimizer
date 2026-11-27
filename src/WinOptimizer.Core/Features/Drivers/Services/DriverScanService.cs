using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Services;

/// <summary>Identifies unassigned packages conservatively and filters official offers using package evidence.</summary>
public sealed class DriverScanService(IDriverInventorySource inventorySource, IEnumerable<IDriverUpdateSource> sources,
    IDriverPackageService? packages = null) : IDriverScanService
{
    public async Task<DriverScanReport> ScanAsync(DriverScanOptions options, IProgress<DriverScanProgress>? progress, CancellationToken ct,
        ConfirmDriverLicenseAsync? confirmLicense = null)
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
        var limitedVerification = new HashSet<DriverSources>();
        var declinedLicenses = new HashSet<DriverSources>();
        if (packages is not null)
        {
            var queued = await QueueVerificationAsync(candidates, inventory, options.AutomaticVerificationLimit,
                confirmLicense, limitedVerification, declinedLicenses, ct).ConfigureAwait(false);
            if (queued.Length > 0)
                await VerifyAsync(packages, candidates, queued, inventory, progress, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new("Drivers_PreparingResults") { Phase = DriverScanPhase.PrepareResults });
        var rejected = candidates.Where(candidate => candidate.Catalog?.Verification is DriverVerification.Failed or DriverVerification.Unsupported)
            .SelectMany(candidate => candidate.Catalog!.References.Where(reference => reference.Source != DriverSources.WindowsUpdate)
                .Select(reference => (reference.Source, candidate.Title, Catalog: candidate.Catalog!)))
            .ToLookup(item => item.Source);
        var outcomes = searches.Select((search, index) =>
        {
            var source = enabled[index].Source;
            var problems = rejected[source].ToArray();
            List<string> details = [.. search.Notices, .. problems.Select(item =>
                $"{item.Title}: {(item.Catalog.VerificationDetail.Length > 0 ? item.Catalog.VerificationDetail : "Package verification did not complete successfully.")}")];
            if (limitedVerification.Contains(source)) details.Add("Some packages could not be automatically verified within the scan limits.");
            if (declinedLicenses.Contains(source)) details.Add("Package verification requires acceptance of the publisher's license.");
            var state = search.State != DriverSourceState.Success ? search.State :
                problems.Any(item => item.Catalog.Verification == DriverVerification.Failed) ? DriverSourceState.VerificationFailed :
                    details.Count > search.Notices.Count ? DriverSourceState.Partial : DriverSourceState.Success;
            return new DriverSourceOutcome(source, state, search.Updates.Count, details, search.SupportPage);
        }).ToArray();
        notices.AddRange(outcomes.SelectMany(outcome => outcome.Notices));
        var (updates, excluded) = DriverCatalogMatching.ClassifyCandidates(candidates, inventory, options, ct);
        return new(inventory, unreferenced, disconnected, updates,
            outcomes.Any(outcome => outcome.State is DriverSourceState.Success or DriverSourceState.Partial or DriverSourceState.VerificationFailed),
            excluded, notices, options) { Sources = outcomes };
    }

    /// <summary>Gives device and distribution groups a turn before trying older offers from the same group.</summary>
    private static async Task<int[]> QueueVerificationAsync(DriverUpdate[] candidates, DriverInventory inventory, int limit,
        ConfirmDriverLicenseAsync? confirmLicense, HashSet<DriverSources> limited, HashSet<DriverSources> declined, CancellationToken ct)
    {
        var identifiers = inventory.Devices.Where(device => device.Present)
            .SelectMany(DriverCatalogMatching.Identifiers).ToLookup(id => id.Value, StringComparer.OrdinalIgnoreCase);
        var eligible = new List<(int Index, DriverSources Source, DriverCatalogRecord Catalog, bool HardwareMatch)>();
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            if (candidate.Catalog is not { References: [var reference, ..] } catalog ||
                reference.Source == DriverSources.WindowsUpdate || !DriverCatalogMatching.Targets(catalog.Target, inventory.System))
                continue;
            var ids = catalog.ListedHardwareIds.SelectMany(id => identifiers[id]).ToArray();
            if (catalog.ListedHardwareIds.Length > 0 && ids.Length == 0) continue;
            if (catalog.Verification is not (DriverVerification.Metadata or DriverVerification.LicenseRequired)) continue;
            if (catalog.Payloads.Length == 0 || candidate.SizeBytes > DriverPackageLimits.AutomaticBytes(reference.Source) ||
                catalog.Payloads.Any(payload =>
                    Path.GetExtension(payload.Url.AbsolutePath).ToLowerInvariant() is not (".cab" or ".zip" or ".7z" or ".exe")))
            {
                limited.Add(reference.Source);
                continue;
            }
            eligible.Add((index, reference.Source, catalog, ids.Any(id => id.Kind == DriverIdentifierKind.Hardware)));
        }

        var ordered = eligible.GroupBy(item => (item.Source, item.Catalog.Family, item.Catalog.Branch))
            .SelectMany(group => group.OrderByDescending(item => item.HardwareMatch)
                .ThenByDescending(item => item.Catalog.ReleaseDate).Select((item, turn) => (Item: item, Turn: turn)))
            .OrderBy(entry => entry.Turn).ThenByDescending(entry => entry.Item.HardwareMatch)
            .ThenByDescending(entry => entry.Item.Catalog.ReleaseDate).ToArray();
        var count = Math.Clamp(limit, 0, 8);
        var queued = new List<int>(count);
        var consent = new Dictionary<Uri, bool>();
        foreach (var (item, _) in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (queued.Count == count) { limited.Add(item.Source); continue; }
            if (item.Catalog.LicenseRequired)
            {
                if (item.Catalog.ScanLicensePage is not { } page || confirmLicense is null)
                { declined.Add(item.Source); continue; }
                if (!consent.TryGetValue(page, out var accepted))
                    consent[page] = accepted = await confirmLicense(page, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!accepted) { declined.Add(item.Source); continue; }
            }
            queued.Add(item.Index);
        }
        return queued.ToArray();
    }

    /// <summary>Inspects independent packages concurrently and reports the stages still active across the queue.</summary>
    private static async Task VerifyAsync(IDriverPackageService service, DriverUpdate[] candidates, int[] queued, DriverInventory inventory,
        IProgress<DriverScanProgress>? progress, CancellationToken ct)
    {
        var transferred = new long[queued.Length];
        var stages = new string?[queued.Length];
        var sync = new object();
        var completed = 0;
        ReportActivity();
        await Parallel.ForEachAsync(Enumerable.Range(0, queued.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (slot, token) =>
            {
                var item = queued[slot];
                var candidate = candidates[item];
                var catalog = candidate.Catalog!;
                var packageProgress = progress is null ? null : new PackageProgress(value =>
                {
                    lock (sync)
                    {
                        stages[slot] = value.Stage;
                        if (value.BytesTransferred is long bytes) transferred[slot] = bytes;
                        ReportActivity();
                    }
                });
                try
                {
                    candidates[item] = (await service.AcquireAsync(candidate, inventory, false, packageProgress, token,
                        licenseAccepted: catalog.LicenseRequired).ConfigureAwait(false)).Update;
                }
                catch (DriverScanException ex)
                {
                    candidates[item] = candidate with { Catalog = catalog with { Verification = DriverVerification.Failed, VerificationDetail = ex.Message } };
                }
                lock (sync)
                {
                    completed++;
                    stages[slot] = null;
                    ReportActivity();
                }
            }).ConfigureAwait(false);

        void ReportActivity()
        {
            if (progress is null) return;
            var active = stages.OfType<string>().Distinct().ToArray();
            var bytes = active.Contains("Drivers_Downloading") ? transferred.Sum() : 0;
            progress.Report(new(active.FirstOrDefault() ?? "Drivers_VerifyingPackages", completed, queued.Length)
            {
                ActivePackageStages = active,
                BytesTransferred = bytes > 0 ? bytes : null
            });
        }
    }

    private sealed class PackageProgress(Action<DriverScanProgress> report) : IProgress<DriverScanProgress>
    {
        public void Report(DriverScanProgress value) => report(value);
    }
}
