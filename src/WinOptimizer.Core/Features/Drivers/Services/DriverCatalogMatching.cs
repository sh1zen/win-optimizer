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
            if (ids.Any(id => listed.Contains(id.Value))) hinted.Add(device);
            var installed = Array.FindIndex(ids, id => id.Value.Equals(device.MatchingDeviceId, StringComparison.OrdinalIgnoreCase));
            foreach (var component in components)
            {
                // Driver IDs and compatible IDs use their original positions and exact string equality.
                var match = (from deviceId in ids
                             from infId in component.Identifiers
                             where deviceId.Value.Equals(infId.Value, StringComparison.OrdinalIgnoreCase)
                             orderby infId.Kind, deviceId.Kind, deviceId.Order, infId.Order
                             select (deviceId, infId)).FirstOrDefault();
                if (match.deviceId is null) continue;
                var relation = device.DriverProvider.Length > 0 && component.Provider.Length > 0 &&
                    !device.DriverProvider.Equals(component.Provider, StringComparison.OrdinalIgnoreCase)
                    ? DriverVersionRelation.NotComparable : Compare(device, component.ClassName, component.Version, component.Date);
                comparisons.Add(new(device, component, match.deviceId, match.infId, relation,
                    installed >= 0 && Array.IndexOf(ids, match.deviceId) < installed));
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
        var versions = matchingComponents.Select(component => component.Version).Distinct().ToArray();
        return new(update with { Version = versions.Length == 1 ? versions[0] : "", Certification = certification },
            comparisons.Select(match => match.Device).Distinct().ToArray())
        { Applicability = DriverApplicability.PackageMatch, Comparisons = comparisons };
    }

    public static DriverVersionRelation Compare(DriverDevice device, string className, string version, string date)
    {
        if (className.Length == 0 || !className.Equals(device.ClassName, StringComparison.OrdinalIgnoreCase) ||
            className.Equals("Extension", StringComparison.OrdinalIgnoreCase) ||
            className.Equals("SoftwareComponent", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Marks the newest verified release within a device's comparable distribution and match group.</summary>
    public static DriverUpdateMatch[] MarkNewest(IReadOnlyList<DriverUpdateMatch> matches)
    {
        var assignments = matches.SelectMany((match, index) => match.Comparisons
            .Where(item => item.VersionRelation is DriverVersionRelation.Newer or DriverVersionRelation.Current or DriverVersionRelation.Older &&
                match.Update.Catalog?.Verification == DriverVerification.Verified && Version.TryParse(item.Component.Version, out _))
            .Select(item => (Index: index, Match: item, Catalog: match.Update.Catalog!)));
        var groups = assignments.GroupBy(entry => (entry.Match.Device.InstanceId, entry.Match.Component.ClassName.ToUpperInvariant(),
            entry.Match.Component.Provider.ToUpperInvariant(), entry.Match.InfIdentifier.Value.ToUpperInvariant(),
            entry.Catalog.Branch, entry.Catalog.Family, entry.Catalog.Target.Oem, entry.Catalog.Target.Model));
        var newest = new Dictionary<int, HashSet<DriverDeviceMatch>>();
        foreach (var group in groups)
        {
            var winner = group.MaxBy(entry => ReleaseOrder(entry.Match.Component));
            var version = ReleaseOrder(winner.Match.Component);
            foreach (var entry in group.Where(entry => ReleaseOrder(entry.Match.Component) == version))
            {
                if (!newest.TryGetValue(entry.Index, out var marked)) newest.Add(entry.Index, marked = []);
                marked.Add(entry.Match);
            }
        }
        return matches.Select((match, index) => newest.TryGetValue(index, out var marked)
            ? match with { Comparisons = match.Comparisons.Select(item => item with { NewestFound = marked.Contains(item) }).ToArray() }
            : match).ToArray();
    }

    private static (DateOnly, Version) ReleaseOrder(DriverComponent component) =>
        (DateOnly.TryParseExact(component.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : default,
            Normalize(Version.Parse(component.Version)));

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
