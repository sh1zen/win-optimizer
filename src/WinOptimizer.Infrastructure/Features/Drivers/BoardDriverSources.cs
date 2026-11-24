using System.Text.Json;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Scopes manufacturer driver listings to the identified PC or motherboard.</summary>
internal abstract class BoardDriverSource(OfficialDriverHttp http, DriverSources source, string manufacturerToken)
    : OfficialDriverSource(http, source)
{
    protected override Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var system = inventory.System;
        var onBoard = Contains(system.BoardManufacturer, manufacturerToken);
        var oem = onBoard ? system.BoardManufacturer : system.Manufacturer;
        var model = onBoard ? system.BoardProduct : system.Model;
        if (!Client(system) || system.Architecture != "X64" || !Contains(oem, manufacturerToken) ||
            model.Length < 3 || Contains(model, "to be filled") || Contains(model, "default string"))
        {
            query.Unsupported("No supported model from this OEM was detected for the current Windows platform.");
            return Task.CompletedTask;
        }
        return CollectModelAsync(query, system, model, ClientTarget(system, oem, model), ct);
    }

    protected abstract Task CollectModelAsync(Query query, DriverSystemContext system, string model, DriverTarget target, CancellationToken ct);
    protected static bool DriverCategory(string category) =>
        !Rx(@"bios|firmware|utility|utilities|software|manual|diagnostic|armoury|app shop").IsMatch(category);
    protected static string Silicon(string name) => Group(name, @"\b(Intel|AMD|NVIDIA|Realtek|MediaTek|Qualcomm|Broadcom|Marvell|Synaptics|ELAN|Killer)\b");
}

internal sealed class AsusDriverSource(OfficialDriverHttp http) : BoardDriverSource(http, DriverSources.Asus, "ASUS")
{
    protected override Uri SupportPage { get; } = new("https://www.asus.com/support/");

    protected override async Task CollectModelAsync(Query query, DriverSystemContext system, string model, DriverTarget target, CancellationToken ct)
    {
        var page = new Uri(SupportPage, "/supportonly/" + Arg(model) + "/helpdesk_download/");
        query.Manual = page;
        var html = await query.HtmlAsync(page, ct).ConfigureAwait(false);
        var published = Group(html, """(https://www\.asus\.com/support/webapi/ProductV2/GetPDDrivers\?[^"\s]+)""");
        if (published.Length == 0) { query.Unsupported($"{model}: ASUS's support page did not publish a driver service."); return; }
        var expected = Windows11(system) ? "52" : "45";
        var endpoint = Link(page, Rx(@"([?&]osid=)\d+").Replace(published, match => match.Groups[1].Value + expected));
        using var response = await query.JsonAsync(endpoint, ct).ConfigureAwait(false);
        var groups = response.RootElement.GetProperty("Result").GetProperty("Obj");
        if (groups.ValueKind != JsonValueKind.Array) throw new DriverScanException("ASUS returned an unrecognized driver listing.");
        foreach (var group in groups.EnumerateArray())
        {
            var family = Text(group, "Name");
            if (!DriverCategory(family)) continue;
            var files = group.GetProperty("Files");
            if (files.GetArrayLength() > 5) query.Partial($"{family}: ASUS listings are limited to the first five releases.");
            foreach (var file in files.EnumerateArray().Take(5))
            {
                var title = Text(file, "Title");
                var raw = Text(file.GetProperty("DownloadUrl"), "Global");
                var payload = OptionalLink(new Uri("https://dlcdnets.asus.com/"), raw);
                if (payload is null) { query.Partial($"{title}: ASUS did not publish a distribution URL."); continue; }
                query.Add(Text(file, "Id"), title, Silicon(title), Text(file, "Version"), Text(file, "ReleaseDate"), family,
                    target, page, [new(payload, Hashes(("SHA256", Text(file, "sha256"))))], Size(Text(file, "FileSize")),
                    Contains(title, "beta") ? "Beta" : "OEM", endpoint, whql: Contains(title, "WHQL"));
            }
        }
        if (query.Updates.Count == 0) query.Unsupported($"{model}: ASUS published no driver packages for this Windows platform.");
    }
}

internal sealed class MsiDriverSource(OfficialDriverHttp http) : BoardDriverSource(http, DriverSources.Msi, "MSI")
{
    protected override Uri SupportPage { get; } = new("https://www.msi.com/support");
    protected override Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        // MSI often publishes its company name in SMBIOS rather than the short brand.
        var system = inventory.System;
        if (Contains(system.BoardManufacturer, "Micro-Star")) return FromMicroStar(query, system, ct);
        return base.CollectAsync(query, inventory, options, ct);
    }

    private Task FromMicroStar(Query query, DriverSystemContext system, CancellationToken ct)
    {
        if (!Client(system) || system.Architecture != "X64" || system.BoardProduct.Length < 3)
        { query.Unsupported("No supported MSI motherboard and Windows platform were detected."); return Task.CompletedTask; }
        return CollectModelAsync(query, system, system.BoardProduct, ClientTarget(system, system.BoardManufacturer, system.BoardProduct), ct);
    }

    protected override async Task CollectModelAsync(Query query, DriverSystemContext system, string model, DriverTarget target, CancellationToken ct)
    {
        var product = Rx(@"\s*\(MS-[A-Z0-9]+\)\s*$").Replace(model, "");
        if (Rx(@"^MS-[A-Z0-9]+$").IsMatch(product))
        { query.Unsupported($"{model}: the board exposes an internal MSI ID without a public model name."); return; }
        var slug = Rx(@"[^a-z0-9]+").Replace(product, "-").Trim('-');
        var page = new Uri(SupportPage, "/Motherboard/" + Arg(slug) + "/support");
        query.Manual = page;
        var endpoint = new Uri(SupportPage, $"/api/v1/product/support/panel?product={Arg(slug)}&type=driver&os={Arg(Windows11(system) ? "Win11 64" : "Win10 64")}");
        using var data = await query.JsonAsync(endpoint, ct).ConfigureAwait(false);
        var groups = data.RootElement.GetProperty("result").GetProperty("downloads");
        if (groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() == 0)
        { query.Unsupported($"{model}: MSI returned no driver panel for this model and Windows version."); return; }
        foreach (var category in groups.EnumerateObject())
        {
            if (!DriverCategory(category.Name) || category.Value.ValueKind != JsonValueKind.Array) continue;
            if (category.Value.GetArrayLength() > 6) query.Partial($"{category.Name}: MSI listings are limited to the first six releases.");
            foreach (var row in category.Value.EnumerateArray().Take(6))
            {
                var id = Text(row, "download_id");
                var title = Text(row, "download_title");
                var payload = OptionalLink(page, Text(row, "download_url"));
                if (id.Length == 0 || payload is null) continue;
                var version = Text(row, "download_version");
                var sha256 = Group(Text(row, "download_sha256"), @"\b([0-9a-f]{64})\b");
                query.Add(id, title, Silicon(title), version, Text(row, "download_release"), category.Name, target, page,
                    [new(payload, Hashes(("SHA256", sha256)))], Size(Text(row, "download_size")),
                    Contains(version, "beta") ? "Beta" : "OEM", endpoint);
            }
        }
        if (query.Updates.Count == 0) query.Unsupported($"{model}: MSI published no downloadable packages in this driver panel.");
    }
}

internal sealed class GigabyteDriverSource(OfficialDriverHttp http) : BoardDriverSource(http, DriverSources.Gigabyte, "Gigabyte")
{
    protected override Uri SupportPage { get; } = new("https://www.gigabyte.com/Support");
    private const string Api = "https://www.gigabyte.com/iisApplicationNuxt/api/proxy/api/v1.0";

    protected override async Task CollectModelAsync(Query query, DriverSystemContext system, string model, DriverTarget target, CancellationToken ct)
    {
        var slug = Rx(@"[^a-z0-9]+").Replace(model, "-").Trim('-');
        query.Manual = new(SupportPage, "/Motherboard/" + Arg(slug) + "/support");
        using var directory = await query.JsonAsync(new(Api + "/Support/global/DownloadCenter/2/GetProducts"), ct).ConfigureAwait(false);
        var products = directory.RootElement.GetProperty("data").EnumerateArray()
            .Where(item => ModelKey(Rx(@"\s*\(rev\..*\)\s*$").Replace(Text(item, "productName"), "")) == ModelKey(model)).ToArray();
        if (products.Length > 1 && Rx(@"^\d+(?:\.\d+)+$").IsMatch(system.BoardVersion))
            products = products.Where(item => Rx(@"(?<!\d)" + System.Text.RegularExpressions.Regex.Escape(system.BoardVersion) + @"(?!\d)")
                .IsMatch(Text(item, "productName"))).ToArray();
        if (products.Length != 1)
        { query.Unsupported($"{model}: Gigabyte did not identify a unique board revision."); return; }
        var productId = Text(products[0], "productId");
        if (productId.Length == 0) throw new DriverScanException("Gigabyte's product identity is missing.");
        var endpoint = new Uri(Api + "/Consumer/global/GetProductTabDataAsync/Support/" + Arg(productId));
        using var data = await query.JsonAsync(endpoint, ct).ConfigureAwait(false);
        var tabs = data.RootElement.GetProperty("data").EnumerateArray().Where(item => Text(item, "key") == "download").ToArray();
        if (tabs.Length == 0) throw new DriverScanException("Gigabyte's download panel was not recognized.");
        foreach (var category in tabs.SelectMany(tab => tab.GetProperty("child").EnumerateArray()).Where(item => Text(item, "key") == "driver"))
        {
            foreach (var file in category.GetProperty("data").EnumerateArray())
            {
                var info = file.GetProperty("info").EnumerateArray().ToArray();
                var os = info.Where(item => Text(item, "infoParentId") == "35").Select(item => Text(item, "infoName")).ToArray();
                var osValue = Windows11(system) ? "11" : "10";
                if (os.Length == 0 || !os.Any(value => Contains(value, "Windows " + osValue) || Contains(value, "Win" + osValue))) continue;
                var family = info.Where(item => Text(item, "infoParentId") == "32").Select(item => Text(item, "infoName")).FirstOrDefault() ?? "";
                var url = OptionalLink(query.Manual, Text(file, "filePath"));
                if (url is null) continue;
                var title = Text(file, "fileName");
                query.Add($"{productId}/{url.AbsolutePath}", title, Silicon(title), Text(file, "fileVersion"), Text(file, "fileReleaseDate"),
                    family, target with { Os = string.Join(", ", os) }, query.Manual,
                    [new(url, Hashes(("MD5", Group(url.Query, @"[?&]md5=([0-9a-f]{32})"))))], Size(Text(file, "fileSize")), "OEM", endpoint);
            }
        }
        if (query.Updates.Count == 0) query.Unsupported($"{model}: Gigabyte listed no driver packages for this Windows version.");
    }
}

internal sealed class AsRockDriverSource(OfficialDriverHttp http) : BoardDriverSource(http, DriverSources.AsRock, "ASRock")
{
    protected override Uri SupportPage { get; } = new("https://www.asrock.com/support/");

    protected override async Task CollectModelAsync(Query query, DriverSystemContext system, string model, DriverTarget target, CancellationToken ct)
    {
        var hosts = Contains(model, "PG") ? new[] { "pg.asrock.com", "www.asrock.com" } : ["www.asrock.com", "pg.asrock.com"];
        foreach (var host in hosts)
        {
            try
            {
                var index = new Uri($"https://{host}/mb/index.asp");
                var directory = await query.HtmlAsync(index, ct).ConfigureAwait(false);
                var data = Group(directory, @"\ballmodels\s*=\s*(\[\[.*?\]\])\s*;");
                if (data.Length == 0) throw new DriverScanException($"{host} did not return a recognizable product directory.");
                var row = Rx(@"\['([^']+)','[^']*','([^']+)'").Matches(data)
                    .FirstOrDefault(entry => ModelKey(entry.Groups[1].Value) == ModelKey(model));
                if (row is null) continue;
                var vendor = Group(row.Groups[2].Value, @"^(AMD|Intel)\b");
                if (vendor.Length == 0) continue;
                var page = new Uri(index, $"/mb/{vendor}/{Arg(row.Groups[1].Value.Replace("/", ""))}/Download.html");
                query.Manual = page;
                var body = await query.HtmlAsync(page, ct).ConfigureAwait(false);
                var os = Windows11(system) ? "osW1164" : "osW1064";
                var rows = Rx(@"<tr\b[^>]*>(.*?)</tr>").Matches(body);
                if (!rows.Any(item => Contains(item.Value, "download.asrock.com")))
                    throw new DriverScanException("ASRock's driver table was not recognized.");
                foreach (var item in rows.Where(item => Contains(item.Value, os)))
                {
                    var html = item.Groups[1].Value;
                    var cells = Rx(@"<td\b[^>]*>(.*?)</td>").Matches(html).Select(cell => Plain(cell.Groups[1].Value)).ToArray();
                    var url = OptionalLink(page, Group(html, """href=["'](https://download\.asrock\.com/[^"']+)"""));
                    if (url is null || cells.Length < 4 || !DriverCategory(cells[0])) continue;
                    var family = Group(html, @"ClickRate\('([^']+)'");
                    query.Add(url.AbsolutePath, cells[0].Split("SHA256", StringSplitOptions.None)[0].Trim(), Silicon(cells[0]),
                        Group(cells[0], @"\bver:\s*(\S+)"), cells[3], family, target, page,
                        [new(url, Hashes(("SHA256", Group(html, @"\b([0-9a-f]{64})\b"))))], Size(cells[2]), "OEM", index);
                }
                if (query.Updates.Count == 0) query.Unsupported($"{model}: ASRock listed no driver packages for this Windows version.");
                return;
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure(ex.Message); }
        }
        query.Unsupported($"{model}: no accessible ASRock product directory identified the detected model.");
    }
}
