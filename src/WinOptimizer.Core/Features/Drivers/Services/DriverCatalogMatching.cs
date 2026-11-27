using System.Globalization;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Core.Features.Drivers.Services;

/// <summary>Evaluates declared targets and exact ordered identifiers from applicable INF Models sections.</summary>
public static class DriverCatalogMatching
{
    public static bool Targets(DriverTarget target, DriverSystemContext system) =>
        (target.Architecture.Length == 0 || target.Architecture.Equals(system.Architecture, StringComparison.OrdinalIgnoreCase)) &&
        (target.MinBuild is null || system.Build >= target.MinBuild) &&
        (target.MaxBuild is null || system.Build > 0 && system.Build <= target.MaxBuild) &&
        (target.Oem.Length == 0 || target.Oem.Equals(system.Manufacturer, StringComparison.OrdinalIgnoreCase) ||
            target.Oem.Equals(system.BoardManufacturer, StringComparison.OrdinalIgnoreCase)) &&
        (target.Model.Length == 0 || target.Model.Equals(system.Model, StringComparison.OrdinalIgnoreCase) ||
            target.Model.Equals(system.Sku, StringComparison.OrdinalIgnoreCase) ||
            target.Model.Equals(system.BoardProduct, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<DriverIdentifier> Identifiers(DriverDevice device) =>
        device.HardwareIds.Select((id, index) => new DriverIdentifier(id, DriverIdentifierKind.Hardware, index))
            .Concat(device.CompatibleIds.Select((id, index) => new DriverIdentifier(id, DriverIdentifierKind.Compatible, index)));

    public static DriverUpdateMatch Evaluate(DriverUpdate update, DriverInventory inventory)
    {
        var catalog = update.Catalog;
        var native = catalog?.References.Any(reference => reference.Source == DriverSources.WindowsUpdate) == true;
        if (catalog is not null && !Targets(catalog.Target, inventory.System))
            return new(update, []) { Applicability = DriverApplicability.Incompatible };

        var present = inventory.Devices.Where(device => device.Present).ToArray();
        var components = catalog is { Verification: DriverVerification.Verified or DriverVerification.Unsupported } ? catalog.Components : [];
        var comparisons = new List<DriverDeviceMatch>();
        var listedIds = catalog?.ListedHardwareIds ?? [];
        var listed = new HashSet<string>(listedIds, StringComparer.OrdinalIgnoreCase);
        if (update.HardwareId.Length != 0) listed.Add(update.HardwareId);
        var hinted = new List<DriverDevice>();
        foreach (var device in present)
        {
            var ids = Identifiers(device).ToArray();
            var isListed = ids.Any(id => listed.Contains(id.Value));
            if (isListed) hinted.Add(device);
            if (native && (listed.Count > 0 && !isListed ||
                update.ClassName.Length > 0 && !update.ClassName.Equals(device.ClassName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var installed = Array.FindIndex(ids, id => id.Value.Equals(device.MatchingDeviceId, StringComparison.OrdinalIgnoreCase));
            foreach (var component in components)
            {
                // Driver IDs and compatible IDs use their original positions and exact string equality.
                var match = (from target in InstallTargets(component)
                             from deviceId in ids
                             from infId in target.Identifiers
                             where deviceId.Value.Equals(infId.Value, StringComparison.OrdinalIgnoreCase)
                             let release = ReleasePreference(target.Section?.Date ?? component.Date, target.Section?.Version ?? component.Version)
                             orderby infId.Kind, deviceId.Kind, deviceId.Order, infId.Order, release descending
                             select (deviceId, infId, target.Section)).FirstOrDefault();
                if (match.deviceId is null) continue;
                var relation = device.DriverProvider.Length > 0 && component.Provider.Length > 0 &&
                    !device.DriverProvider.Equals(component.Provider, StringComparison.OrdinalIgnoreCase)
                    ? DriverVersionRelation.NotComparable : Compare(device, component.ClassName,
                        match.Section?.Version ?? component.Version, match.Section?.Date ?? component.Date);
                var position = Array.IndexOf(ids, match.deviceId);
                comparisons.Add(new(device, component, match.deviceId, match.infId, relation,
                    installed >= 0 && position < installed)
                { InstallSection = match.Section, LessSpecificIdentifier = installed >= 0 && position > installed });
            }
        }

        if (native) return new(update, hinted) { Applicability = DriverApplicability.WindowsUpdate, Comparisons = comparisons };
        if (comparisons.Count == 0)
            return new(update with { Certification = DriverCertification.Unverified }, hinted) { Applicability = catalog?.Verification == DriverVerification.Verified
                ? DriverApplicability.Incompatible : DriverApplicability.Unknown };

        var matchingComponents = comparisons.Select(match => match.Component).Distinct().ToArray();
        var certification = catalog?.Verification == DriverVerification.Verified &&
            matchingComponents.All(component => component.Certification == DriverCertification.Whql)
            ? DriverCertification.Whql : DriverCertification.Unverified;
        var versions = comparisons.Select(match => match.Version).Distinct().ToArray();
        return new(update with { Version = versions.Length == 1 ? versions[0] : "", Certification = certification },
            comparisons.Select(match => match.Device).Distinct().ToArray())
        { Applicability = DriverApplicability.PackageMatch, Comparisons = comparisons };
    }

    /// <summary>Retains INF-wide evidence for legacy components and components without model-level data.</summary>
    private static IEnumerable<(DriverInstallSection? Section, DriverIdentifier[] Identifiers)> InstallTargets(DriverComponent component) =>
        component.InstallSections.Length == 0 ? [(null, component.Identifiers)] :
            component.InstallSections.Select(section => ((DriverInstallSection?)section, section.Identifiers));

    public static DriverVersionRelation Compare(DriverDevice device, string className, string version, string date)
    {
        if (className.Length == 0 || !className.Equals(device.ClassName, StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Extension", StringComparison.OrdinalIgnoreCase))
            return DriverVersionRelation.NotComparable;
        if (!Version.TryParse(version, out var available) || !Version.TryParse(device.Version, out var installed))
            return DriverVersionRelation.Unknown;
        if (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var availableDate) &&
            DateOnly.TryParseExact(device.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var installedDate) &&
            availableDate != installedDate)
            return availableDate > installedDate ? DriverVersionRelation.Newer : DriverVersionRelation.Older;
        var compared = Normalize(available).CompareTo(Normalize(installed));
        return compared > 0 ? DriverVersionRelation.Newer : compared < 0 ? DriverVersionRelation.Older : DriverVersionRelation.Current;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    /// <summary>Recommends the best proven upgrade per device and distribution under the selected certification policy.</summary>
    public static (DriverUpdateMatch[] Updates, int Excluded) ClassifyCandidates(
        IEnumerable<DriverUpdate> candidates, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var matches = Deduplicate(candidates).Select(update =>
        {
            ct.ThrowIfCancellationRequested();
            return Evaluate(update, inventory);
        }).ToArray();
        var assignments = matches.SelectMany((match, index) => UpgradeComparisons(match, options.OnlyWhqlUpdates)
            .Select(item => (Index: index, Match: item, Catalog: match.Update.Catalog!)));
        var winners = assignments.GroupBy(entry => (entry.Match.Device.InstanceId.ToUpperInvariant(),
                entry.Match.Component.Provider.ToUpperInvariant(), DistributionBranch(entry.Catalog),
                entry.Catalog.Target.Oem.ToUpperInvariant(), entry.Catalog.Target.Model.ToUpperInvariant()))
            .Select(group => group.OrderByDescending(entry => Preference(entry.Match))
                .ThenByDescending(entry => entry.Match.Component.Certification)
                .ThenByDescending(entry => entry.Catalog.OemRecommended)
                .ThenByDescending(entry => matches[entry.Index].Applicability == DriverApplicability.WindowsUpdate)
                .First())
            .ToLookup(entry => entry.Index, entry => entry.Match);
        var updates = winners.OrderBy(group => group.Key).Select(group =>
        {
            var match = matches[group.Key];
            var comparisons = group.Select(item => item with { NewestFound = true }).ToArray();
            var versions = comparisons.Select(item => item.Version).Distinct().ToArray();
            return match with
            {
                Devices = comparisons.Select(item => item.Device).Distinct().ToArray(),
                Comparisons = comparisons,
                Update = match.Update with
                {
                    Version = versions.Length == 1 ? versions[0] : "",
                    Certification = comparisons.All(item => item.Component.Certification == DriverCertification.Whql)
                        ? DriverCertification.Whql : DriverCertification.Unverified
                }
            };
        }).ToArray();
        ct.ThrowIfCancellationRequested();
        return (updates, matches.Length - updates.Length);
    }

    // Windows Update and the Microsoft Catalog can deliver the same release to the same device.
    private static string DistributionBranch(DriverCatalogRecord catalog) =>
        catalog.References.Any(reference => reference.Source == DriverSources.WindowsUpdate) ||
        catalog.Branch.Length == 0 && catalog.References.Any(reference => reference.Source == DriverSources.MicrosoftCatalog)
            ? "WINDOWS UPDATE" : catalog.Branch.ToUpperInvariant();

    // Evaluate the strongest signed INF within a package before comparing it with the installed driver.
    private static IEnumerable<DriverDeviceMatch> UpgradeComparisons(DriverUpdateMatch match, bool onlyWhql)
    {
        if (match.Update.Catalog?.Verification != DriverVerification.Verified) return [];
        return match.Comparisons.Where(item => item.Component.SignatureValid && !item.LessSpecificIdentifier &&
                item.Component.ClassName.Equals(item.Device.ClassName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(Preference)!)
            .Where(item => item.VersionRelation == DriverVersionRelation.Newer &&
                (!onlyWhql || item.Component.Certification == DriverCertification.Whql));
    }

    private static (int, int, int, (DateOnly, Version?)) Preference(DriverDeviceMatch match) =>
        (-(int)match.InfIdentifier.Kind, -(int)match.DeviceIdentifier.Kind, -match.DeviceIdentifier.Order,
            ReleasePreference(match.Date, match.Version));

    private static (DateOnly Date, Version? Version) ReleasePreference(string date, string version) =>
        (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ? parsedDate : default,
            Version.TryParse(version, out var parsedVersion) ? Normalize(parsedVersion) : null);

    /// <summary>Uses complete verified payload identity and retains distribution alternatives with different constraints.</summary>
    public static IReadOnlyList<DriverUpdate> Deduplicate(IEnumerable<DriverUpdate> updates)
    {
        var result = new List<DriverUpdate>();
        var records = new HashSet<(DriverSources, string, string)>();
        var packages = new Dictionary<(string, string, DriverTarget, string, string, bool), int>();
        foreach (var update in updates)
        {
            var catalog = update.Catalog;
            if (catalog is null) { result.Add(update); continue; }
            var refs = catalog.References;
            var hasNewReference = false;
            foreach (var reference in refs)
                hasNewReference |= records.Add((reference.Source, reference.Id.ToUpperInvariant(), reference.Revision));
            if (!hasNewReference) continue;
            if (catalog.ContentSha256.Length == 0 || catalog.Verification != DriverVerification.Verified)
            { result.Add(update); continue; }
            var key = (string.Join(",", catalog.ContentSha256), catalog.Branch, catalog.Target,
                string.Join(",", catalog.ListedHardwareIds.Select(id => id.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal)),
                string.Join("\n", catalog.DeclaredPrerequisites), catalog.LicenseRequired);
            if (packages.TryGetValue(key, out var index))
            {
                var existing = result[index];
                var original = existing.Catalog!;
                result[index] = existing with { Catalog = original with { References = [.. original.References, .. refs] } };
            }
            else
            {
                packages.Add(key, result.Count);
                result.Add(update);
            }
        }
        return result;
    }
}
