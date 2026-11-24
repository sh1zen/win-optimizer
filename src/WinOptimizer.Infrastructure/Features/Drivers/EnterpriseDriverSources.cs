using System.Xml.Linq;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Filters Dell's complete client catalog and resolves matching per-model manifests from its index.</summary>
internal sealed class DellDriverSource(OfficialDriverHttp http, string? temporaryDirectory = null)
    : OfficialDriverSource(http, DriverSources.Dell, temporaryDirectory)
{
    protected override Uri SupportPage { get; } = new("https://www.dell.com/support/home/");
    private static readonly Uri Distribution = new("https://downloads.dell.com/");
    private static readonly Uri Catalog = new(Distribution, "catalog/CatalogPC.cab");
    private static readonly Uri Index = new(Distribution, "catalog/CatalogIndexPC.cab");

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var system = inventory.System;
        if (!Client(system) || !Contains(system.Manufacturer, "Dell") || system.Model.Length == 0)
        { query.Unsupported("No supported Dell Windows client identity was detected."); return; }
        var indexCatalogFound = false;
        try
        {
            var index = await query.XmlAsync(Index, ct).ConfigureAwait(false);
            if (index.Root?.Name.LocalName != "ManifestIndex") throw new DriverScanException("Dell's client catalog index format changed.");
            foreach (var group in Descendants(index, "GroupManifest").Where(group => Descendants(group, "Model").Any(model => Matches(model, system))).Take(2))
            {
                var latest = Attr(group, "latest");
                var entry = Elements(group, "ManifestInformation").FirstOrDefault(element => Attr(element, "id") == latest);
                if (entry is null || Attr(entry, "path").Length == 0) continue;
                var uri = Link(Distribution, Attr(entry, "path"));
                var manifest = await query.XmlAsync(uri, ct, PublishedHashes(entry)).ConfigureAwait(false);
                ReadManifest(query, manifest, inventory, uri, modelScoped: true);
                indexCatalogFound = true;
            }
        }
        catch (Exception ex) when (IsSourceError(ex)) { query.Failure(ex.Message); }

        if (!indexCatalogFound || query.Updates.Count == 0)
        {
            var manifest = await query.XmlAsync(Catalog, ct).ConfigureAwait(false);
            ReadManifest(query, manifest, inventory, Catalog, modelScoped: false);
        }
        if (query.Updates.Count == 0) query.Unsupported($"{system.Model}: no matching drivers were found in Dell's client catalogs.");
    }

    private static bool Matches(XElement model, DriverSystemContext system) =>
        (system.Sku.Length >= 4 && system.Sku.Equals(Attr(model, "systemID"), StringComparison.OrdinalIgnoreCase)) ||
        ModelKey(Field(model, "Display")) == ModelKey(system.Model);

    private void ReadManifest(Query query, XDocument document, DriverInventory inventory, Uri descriptor, bool modelScoped)
    {
        if (document.Root?.Name.LocalName != "Manifest") throw new DriverScanException("Dell's client driver manifest format changed.");
        var system = inventory.System;
        var ids = inventory.Devices.Where(device => device.Present).SelectMany(DriverCatalogMatching.Identifiers)
            .Select(id => id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Descendants(document, "SoftwareComponent"))
        {
            if (!Elements(entry, "ComponentType").Any(type => Attr(type, "value") == "DRVR")) continue;
            var models = Descendants(entry, "Model").ToArray();
            if (!(modelScoped && models.Length == 0) && !models.Any(model => Matches(model, system))) continue;
            var relevantOs = Descendants(entry, "OperatingSystem").Where(os =>
                Attr(os, "osArch").Equals(system.Architecture, StringComparison.OrdinalIgnoreCase) &&
                Contains(Field(os, "Display"), Windows11(system) ? "Windows 11" : "Windows 10")).ToArray();
            if (relevantOs.Length == 0) continue;
            var hardware = Descendants(entry, "PCIInfo").Select(PciId).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (hardware.Length > 0 && !hardware.Any(ids.Contains)) continue;
            var title = Elements(entry, "Name").Select(name => Field(name, "Display")).FirstOrDefault() ?? "";
            var url = OptionalLink(Distribution, Attr(entry, "path"));
            if (url is null) { query.Partial($"{title}: Dell did not publish a payload path."); continue; }
            var landing = Elements(entry, "ImportantInfo").Select(info => Attr(info, "URL")).FirstOrDefault() ?? "";
            if (landing.StartsWith("http://www.dell.com/", StringComparison.OrdinalIgnoreCase)) landing = "https://" + landing[7..];
            var category = Elements(entry, "Category").Select(item => Field(item, "Display")).FirstOrDefault() ?? "";
            var criticality = Elements(entry, "Criticality").Select(item => Field(item, "Display")).FirstOrDefault() ?? "";
            var sourceId = Attr(entry, "releaseID");
            if (sourceId.Length == 0) sourceId = Attr(entry, "identifier");
            query.Add(sourceId, title, Group(title, @"\b(Intel|AMD|NVIDIA|Realtek|Dell|Qualcomm|Broadcom)\b"),
                Attr(entry, "vendorVersion"), Attr(entry, "releaseDate"), category, ClientTarget(system, system.Manufacturer, system.Model)
                    with { Os = string.Join(", ", relevantOs.Select(os => Field(os, "Display")).Distinct()) },
                OptionalLink(SupportPage, landing) ?? SupportPage, [new(url, PublishedHashes(entry))], Size(Attr(entry, "size")),
                criticality.Length > 0 ? "OEM · " + criticality : "OEM", descriptor, hardware,
                revision: Attr(entry, "dellVersion") + "|" + Attr(entry, "dateTime"),
                oemRecommended: criticality.Equals("Recommended", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static DriverHash[] PublishedHashes(XElement entry) => Elements(entry, "Cryptography")
        .SelectMany(node => Elements(node, "Hash")).Select(node => new DriverHash(Attr(node, "algorithm"), node.Value.Trim()))
        .Concat(Hashes(("MD5", Attr(entry, "hashMD5")))).Distinct().ToArray();

    private static string PciId(XElement item)
    {
        var vendor = Attr(item, "vendorID");
        var device = Attr(item, "deviceID");
        if (!Rx(@"^[0-9a-f]{4}$").IsMatch(vendor) || !Rx(@"^[0-9a-f]{4}$").IsMatch(device)) return "";
        var subsystem = Attr(item, "subDeviceID") + Attr(item, "subVendorID");
        return $"PCI\\VEN_{vendor}&DEV_{device}" + (Rx(@"^[0-9a-f]{8}$").IsMatch(subsystem) ? $"&SUBSYS_{subsystem}" : "");
    }
}

/// <summary>Uses the HPIA platform list and only the matching SoftPaq reference catalog.</summary>
internal sealed class HpDriverSource(OfficialDriverHttp http, string? temporaryDirectory = null)
    : OfficialDriverSource(http, DriverSources.Hp, temporaryDirectory)
{
    protected override Uri SupportPage { get; } = new("https://support.hp.com/us-en/drivers");
    private static readonly Uri Reference = new("https://hpia.hpcloud.hp.com/ref/");

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var system = inventory.System;
        if (!Client(system) || system.Architecture != "X64" ||
            !(Contains(system.Manufacturer, "Hewlett-Packard") || Rx(@"^HP\b").IsMatch(system.Manufacturer)))
        { query.Unsupported("No supported HP x64 Windows client identity was detected."); return; }
        var id = system.BoardProduct.ToLowerInvariant();
        if (!Rx(@"^[0-9a-f]{4}$").IsMatch(id)) { query.Unsupported("HP's SMBIOS platform ID is missing."); return; }
        query.Manual = new(SupportPage, "/us-en/search?q=" + Arg(system.Model));
        var platforms = await query.XmlAsync(new(Reference, "platformList.cab"), ct).ConfigureAwait(false);
        if (platforms.Root?.Name.LocalName != "ImagePal") throw new DriverScanException("HP's reference platform catalog format changed.");
        var platform = Descendants(platforms, "Platform").FirstOrDefault(item => Field(item, "SystemID").Equals(id, StringComparison.OrdinalIgnoreCase));
        if (platform is null) { query.Unsupported($"{system.Model}: the HPIA catalog does not list platform {id}."); return; }
        var os = Elements(platform, "OS").FirstOrDefault(item => Field(item, "OSArchitecture") == "64" &&
            int.TryParse(Field(item, "OSBuildId"), out var build) && build == system.Build);
        if (os is null) { query.Unsupported($"{system.Model}: HPIA does not list this Windows build."); return; }
        var release = Field(os, "OSReleaseIdFilename").ToLowerInvariant();
        if (!Rx(@"^[a-z0-9]{3,6}$").IsMatch(release)) throw new DriverScanException("The HPIA catalog did not publish a usable OS release key.");
        var descriptor = new Uri(Reference, $"{id}/{id}_64_{(Windows11(system) ? "11" : "10")}.0.{release}.cab");
        var catalog = await query.XmlAsync(descriptor, ct).ConfigureAwait(false);
        var softpaqs = Descendants(catalog, "UpdateInfo").Where(item => Attr(item, "ColId").Length > 0).ToArray();
        if (softpaqs.Length == 0) throw new DriverScanException("HP's SoftPaq reference did not return a recognized package list.");
        foreach (var package in softpaqs)
        {
            var category = Field(package, "Category");
            if (!category.StartsWith("Driver", StringComparison.OrdinalIgnoreCase)) continue;
            var idValue = Field(package, "Id");
            var raw = Field(package, "Url");
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw[7..];
            else if (raw.Length > 0 && !raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
            var url = OptionalLink(Reference, raw);
            var title = Field(package, "Name");
            if (url is null) { query.Partial($"{title}: HP did not publish a payload URL."); continue; }
            query.Add(idValue, title, Field(package, "Vendor"), Field(package, "Version"), Field(package, "DateReleased"), category,
                ClientTarget(system, system.Manufacturer, system.Model) with { MinBuild = system.Build, MaxBuild = system.Build },
                query.Manual, [new(url, Hashes(("SHA256", Field(package, "SHA256")), ("MD5", Field(package, "MD5"))))],
                Size(Field(package, "Size")), "OEM", descriptor);
        }
        if (query.Updates.Count == 0) query.Unsupported($"{system.Model}: no driver SoftPaqs were listed for this build.");
    }
}

/// <summary>Resolves Lenovo System Update manifests by machine type and uses the files declared by each package descriptor.</summary>
internal sealed class LenovoDriverSource(OfficialDriverHttp http) : OfficialDriverSource(http, DriverSources.Lenovo)
{
    protected override Uri SupportPage { get; } = new("https://pcsupport.lenovo.com/");
    private static readonly Uri Directory = new("https://download.lenovo.com/cdrt/td/catalogv2.xml");
    private static readonly Uri Distribution = new("https://download.lenovo.com/");

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var system = inventory.System;
        if (!Client(system) || system.Architecture != "X64" || !Contains(system.Manufacturer, "Lenovo"))
        { query.Unsupported("No supported Lenovo x64 Windows client identity was detected."); return; }
        var type = Group(system.Sku, @"(?:^|_)MT_([0-9][A-Z0-9]{3})(?=_|$)").ToLowerInvariant();
        if (type.Length == 0) type = Group(system.Sku, @"^([0-9][A-Z0-9]{3})$").ToLowerInvariant();
        if (type.Length == 0) type = Group(system.Model, @"^([0-9][A-Z0-9]{3})(?:[A-Z0-9]{0,10})$").ToLowerInvariant();
        if (type.Length == 0) { query.Unsupported("Lenovo's four-character machine type is missing."); return; }
        query.Manual = new(SupportPage, "products/" + type + "/downloads/driver-list");
        var directory = await query.XmlAsync(Directory, ct).ConfigureAwait(false);
        if (directory.Root?.Name.LocalName != "ModelList") throw new DriverScanException("Lenovo's product directory format changed.");
        var model = Descendants(directory, "Model").FirstOrDefault(item => Descendants(item, "Type").Any(entry =>
            entry.Value.Equals(type, StringComparison.OrdinalIgnoreCase)));
        var os = Windows11(system) ? "win11" : "win10";
        var catalogUri = new Uri(Distribution, $"catalog/{type}_{os}.xml");
        XDocument catalog;
        try { catalog = await query.XmlAsync(catalogUri, ct).ConfigureAwait(false); }
        catch (DriverScanException ex) when (model is null && ex.InnerException is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
        { query.Unsupported($"{type}: Lenovo's commercial directory does not list the model and its System Update catalog could not be opened."); return; }
        if (catalog.Root?.Name.LocalName != "packages") throw new DriverScanException("Lenovo's System Update model catalog format changed.");

        var entries = Elements(catalog.Root, "package").Where(item =>
        {
            var category = Field(item, "category");
            return category.Length > 0 && !Rx(@"BIOS|Firmware|Utilities|Applications|Recovery|Diagnostics").IsMatch(category);
        }).ToArray();
        if (entries.Length > 45) query.Partial("Lenovo descriptor lookups are limited to 45 listings per scan.");
        foreach (var entry in entries.Take(45))
        {
            ct.ThrowIfCancellationRequested();
            var category = Field(entry, "category");
            var descriptor = OptionalLink(catalogUri, Field(entry, "location"));
            if (descriptor is null) { query.Partial("A Lenovo catalog entry lacks its package descriptor."); continue; }
            try
            {
                var checks = Elements(entry, "checksum").Select(hash => new DriverHash(Attr(hash, "type"), hash.Value.Trim())).ToArray();
                var detail = await query.XmlAsync(descriptor, ct, checks).ConfigureAwait(false);
                var package = detail.Root;
                if (package?.Name.LocalName != "Package") throw new DriverScanException("Unrecognized Lenovo System Update package descriptor.");
                var title = Descendants(package, "Desc").FirstOrDefault(item => Attr(item, "id") == "EN")?.Value ?? "";
                if (Rx(@"firmware|diagnostic|(?:\bBIOS\b)").IsMatch(title)) continue;
                var id = Attr(package, "id");
                var files = Elements(package, "Files").SelectMany(node => Elements(node, "Installer")).SelectMany(node => Elements(node, "File")).ToArray();
                var payloads = new List<DriverPayload>();
                foreach (var file in files)
                {
                    var name = Field(file, "Name");
                    if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':', '?', '#']) >= 0 || name is "." or "..")
                        throw new DriverScanException("Lenovo returned an invalid descriptor payload name.");
                    var hash = Field(file, "CRC");
                    var algorithm = hash.Length switch { 64 => "SHA256", 40 => "SHA1", 32 => "MD5", _ => "CRC" };
                    payloads.Add(new(Link(descriptor, name), Hashes((algorithm, hash))));
                }
                if (payloads.Count == 0) query.Partial($"{title}: the descriptor does not identify an installer payload.");
                var ids = Descendants(package, "_PnPID").Concat(Descendants(package, "HardwareID")).Select(item => item.Value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var prerequisites = Descendants(package, "Dependencies").Select(item => item.ToString(SaveOptions.DisableFormatting)).ToArray();
                long? size = files.Length > 0 && files.All(file => Size(Field(file, "Size")) is not null)
                    ? files.Sum(file => Size(Field(file, "Size"))!.Value) : null;
                query.Add(id, title, Field(package, "Vendor"), Attr(package, "version"), Field(package, "ReleaseDate"), category,
                    ClientTarget(system, system.Manufacturer, system.Model), query.Manual, payloads.ToArray(), size, "OEM", descriptor, ids,
                    prerequisites: prerequisites);
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{descriptor}: {ex.Message}"); }
        }
        if (query.Updates.Count == 0) query.Unsupported($"{type}: Lenovo listed no resolved driver records for this Windows version.");
    }
}
