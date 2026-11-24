using System.Net;
using System.Text.Json;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Uses AMD's product directory to scope graphics and chipset releases to the detected product.</summary>
internal sealed class AmdDriverSource(OfficialDriverHttp http) : OfficialDriverSource(http, DriverSources.Amd)
{
    protected override Uri SupportPage { get; } = new("https://www.amd.com/en/support/download/drivers.html");
    internal sealed record Listing(string Title, string Revision, string Date, long? Bytes, Uri Url, Uri? Notes, string Branch);

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var system = inventory.System;
        var graphics = Devices(inventory, "PCI\\VEN_1002&", "Display").ToArray();
        var chipset = Group(system.BoardProduct + " " + system.Model, @"\b([ABX]\d{3}(?:E|M)?|TRX\d{2}|WRX\d{2})\b");
        if (chipset.EndsWith('M') && chipset.Length == 5) chipset = chipset[..^1];
        if (!Devices(inventory, "PCI\\VEN_1022&", "System").Any()) chipset = "";
        if (!Client(system) || system.Architecture != "X64" || graphics.Length == 0 && chipset.Length == 0)
        { query.Unsupported("No recognized AMD graphics or chipset model is available for this Windows platform."); return; }

        var html = await query.HtmlAsync(SupportPage, ct).ConfigureAwait(false);
        var data = Group(html, """data-component="coveo-browse-documentation"\s+data-json="([^"]+)""");
        if (data.Length == 0) throw new DriverScanException("AMD's product selector format changed.");
        using var directory = JsonDocument.Parse(WebUtility.HtmlDecode(data));
        var products = Products(directory.RootElement.GetProperty("productTypes")).ToArray();
        var selected = new Dictionary<Uri, (string Family, string[] Ids)>();
        foreach (var gpu in graphics)
        {
            var model = GraphicsKey(gpu.Name);
            var product = products.FirstOrDefault(item => Contains(item.Url.AbsolutePath, "/graphics/") && GraphicsKey(item.Name) == model);
            if (product.Url is null && model is "RADEONGRAPHICS" or "RADEONTMGRAPHICS")
            {
                var cpu = system.ProcessorNames.Select(CpuKey).FirstOrDefault(key => key.Length > 0) ?? "";
                product = products.FirstOrDefault(item => cpu.Length > 0 && Contains(item.Url.AbsolutePath, "/processors/") && CpuKey(item.Name) == cpu);
            }
            if (product.Url is null) { query.Unsupported($"{gpu.Name}: AMD's directory did not identify an exact graphics product."); continue; }
            selected.TryAdd(product.Url, (product.Name, gpu.HardwareIds));
        }
        if (chipset.Length > 0)
        {
            var chipsetPages = products.Where(item => Contains(item.Url.AbsolutePath, "/chipsets/") &&
                ModelKey(item.Url.Segments[^1].Replace(".html", "", StringComparison.OrdinalIgnoreCase)) == ModelKey(chipset)).DistinctBy(item => item.Url).ToArray();
            if (chipsetPages.Length == 1) selected.TryAdd(chipsetPages[0].Url, (chipsetPages[0].Name, []));
            else query.Unsupported($"{chipset}: AMD's directory did not identify a unique chipset support page.");
        }
        if (selected.Count == 0) { query.Unsupported("No AMD download page could be mapped to the detected products."); return; }
        foreach (var (page, product) in selected.Take(6))
        {
            try
            {
                var body = await query.HtmlAsync(page, ct).ConfigureAwait(false);
                var releaseDate = query.Retrieved;
                var releases = ParseReleases(page, body, Windows11(system));
                if (releases.Length == 0) { query.Unsupported($"{product.Family}: no driver download is listed for this Windows version."); continue; }
                foreach (var release in releases)
                {
                    string[] componentVersions = [];
                    if (release.Notes is not null && Contains(release.Title, "Chipset"))
                    {
                        try
                        {
                            componentVersions = ParseChipsetComponents(await query.HtmlAsync(release.Notes, ct).ConfigureAwait(false));
                            if (componentVersions.Length == 0) query.Partial($"{release.Title}: chipset component versions could not be read.");
                        }
                        catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{release.Title}: {ex.Message}"); }
                    }
                    query.Add($"{page.AbsolutePath}|{release.Url.AbsolutePath}", release.Title, "AMD", release.Revision, release.Date,
                        product.Family, ClientTarget(system), page, [new(release.Url, [])], release.Bytes, release.Branch, release.Notes,
                        product.Ids, Contains(release.Revision, "WHQL"), license: Contains(body, "agree to be bound by the"),
                        components: componentVersions, retrievedAt: releaseDate);
                }
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{product.Family}: {ex.Message}"); }
        }
    }

    private static string GraphicsKey(string name) => ModelKey(Rx(@"\b(AMD|ATI|Laptop|Mobile|GPU|Series)\b").Replace(name, ""));
    private static string CpuKey(string name) => Group(name, @"\bRyzen(?:.|\s){0,32}?\b(\d{4,5}[A-Z0-9]{0,5})\b").ToUpperInvariant();

    private static IEnumerable<(string Name, Uri Url)> Products(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (Uri.TryCreate(Text(item, "url"), UriKind.Absolute, out var url) && OfficialDriverHttp.Approved(DriverSources.Amd, url))
                yield return (Text(item, "title"), url);
            if (item.TryGetProperty("items", out var children))
                foreach (var child in Products(children)) yield return child;
        }
    }

    internal static Listing[] ParseReleases(Uri page, string html, bool windows11)
    {
        var sections = Rx("""<button\b[^>]*class="accordion-button[^"]*"[^>]*>(.*?)</button>(.*?)(?=<button\b[^>]*class="accordion-button|$)""").Matches(html);
        if (sections.Count == 0) throw new DriverScanException("AMD's driver download page layout changed.");
        var os = windows11 ? "Windows 11" : "Windows 10";
        return sections.Where(section => Contains(Plain(section.Groups[1].Value), os))
            .SelectMany(section => Rx("""<article\b[^>]*driver-download-details[^>]*>(.*?)</article>""").Matches(section.Groups[2].Value))
            .Select(article =>
            {
                var content = article.Groups[1].Value;
                var url = OptionalLink(page, Group(content, "href=\"(https://drivers\\.amd\\.com/drivers/[^\"]+\\.(?:zip|exe))\""));
                if (url is null || Contains(url.AbsolutePath, "_web.") || Contains(url.AbsolutePath, "minimalsetup")) return null;
                var title = Plain(Group(content, @"<h4[^>]*>(.*?)</h4>"));
                var revision = Plain(Group(content, @"<strong>Revision Number</strong>\s*<p[^>]*>(.*?)</p>"));
                var date = Plain(Group(content, @"<strong>Release Date</strong>\s*<p[^>]*>(.*?)</p>"));
                var size = Plain(Group(content, @"<strong>File Size</strong>\s*<p[^>]*>(.*?)</p>"));
                var notes = OptionalLink(page, Group(content, "href=\"([^\"]*/release-notes/[^\"]+)\""));
                var suffix = Group(url.AbsolutePath, @"-win(?:10|11)-([a-z])\.exe");
                var branch = suffix.Length > 0 ? $"Adrenalin {suffix.ToUpperInvariant()}" :
                    Contains(title, "Adrenalin") ? "Adrenalin" : title;
                if (Contains(title, "Adrenalin") && Contains(revision, "Optional")) branch += " · Optional";
                return new Listing(title, revision, date, Size(size), url, notes, branch);
            }).OfType<Listing>().DistinctBy(listing => listing.Url).ToArray();
    }

    internal static string[] ParseChipsetComponents(string html) => Rx(@"<tr\b[^>]*>(.*?)</tr>").Matches(html)
        .Select(row => Rx(@"<td\b[^>]*>(.*?)</td>").Matches(row.Groups[1].Value).Select(cell => Plain(cell.Groups[1].Value)).ToArray())
        .Where(cells => cells.Length >= 2 && cells[0].Length > 0 && cells.Skip(1).Any(cell => Rx(@"\b\d+\.\d+\.\d+\b").IsMatch(cell)))
        .Select(cells => string.Join(" · ", cells.Where(cell => cell.Length > 0))).Distinct().ToArray();
}
