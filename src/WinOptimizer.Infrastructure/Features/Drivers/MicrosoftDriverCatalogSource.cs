using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Searches a bounded set of Windows hardware IDs and resolves the Catalog's own download dialog.</summary>
internal sealed class MicrosoftDriverCatalogSource(OfficialDriverHttp http)
    : OfficialDriverSource(http, DriverSources.MicrosoftCatalog)
{
    protected override Uri SupportPage { get; } = new("https://www.catalog.update.microsoft.com/");
    private static readonly Regex Row = Rx("""<tr\s+id=["']([0-9a-f-]{36})_R\d+["'][^>]*>(.*?)</tr>""");
    private static readonly Regex Cell = Rx(@"<td\b[^>]*>(.*?)</td>");
    private static readonly Regex Audience = Rx(@"\bWindows\s+(10|11)\b(.*?)(?=\bWindows\s+(?:10|11)\b|$)");

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        if (!Client(inventory.System)) { query.Unsupported("The adapter targets Windows 10 and 11 clients."); return; }
        var hardware = inventory.Devices.Where(device => device.Present)
            .OrderBy(device => DevicePriority(device.ClassName))
            .SelectMany(device => device.HardwareIds.Where(id => Rx(@"^(PCI\\VEN_|USB\\VID_|HDAUDIO\\FUNC_|INTELAUDIO\\FUNC_|ACPI\\)[A-Z0-9_&.\\-]+$").IsMatch(id))
                .OrderBy(id => Contains(id, "&REV_") ? 1 : 0).Take(1))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (hardware.Length == 0) { query.Unsupported("No supported PnP hardware identifiers were detected."); return; }
        if (hardware.Length > 12) query.Partial($"Searched up to 12 of {hardware.Length} relevant hardware IDs.");
        var checkedRecords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var system = inventory.System;
        foreach (var id in hardware.Take(12))
        {
            ct.ThrowIfCancellationRequested();
            var search = new Uri(SupportPage, "Search.aspx?q=" + Arg(id));
            try
            {
                var html = await query.HtmlAsync(search, ct).ConfigureAwait(false);
                var rows = ParseRows(html);
                var candidates = rows.Select(row => (Row: row, Audiences: Audience.Matches(row.Os).Cast<Match>()
                        .Where(audience => audience.Groups[1].Value == (Windows11(system) ? "11" : "10")).ToArray()))
                    .Where(entry => entry.Audiences.Length > 0)
                    .Select(entry => (entry.Row, Target: new DriverTarget(MinBuild: MinimumBuild(entry.Audiences), Os: entry.Row.Os)))
                    .Where(entry => DriverCatalogMatching.Targets(entry.Target, system))
                    .OrderByDescending(entry => DateOnly.TryParse(entry.Row.Date, CultureInfo.InvariantCulture, out var date) ? date : default).ToArray();
                if (Contains(html, "data-commandName=\"next\"") || candidates.Length > 2)
                    query.Partial($"{id}: examined at most six listings on the first page and retained up to two candidates.");
                var found = 0;
                foreach (var (row, windowsTarget) in candidates.Take(6))
                {
                    if (found == 2) break;
                    if (!checkedRecords.Add(row.Id)) continue;
                    var landing = new Uri(SupportPage, "ScopedViewInline.aspx?updateid=" + row.Id);
                    var descriptor = new Uri(SupportPage, "DownloadDialog.aspx");
                    try
                    {
                        var details = await query.HtmlAsync(landing, ct).ConfigureAwait(false);
                        var manufacturer = Plain(Group(details, """id="ScopedViewHandler_manufacturer"[^>]*>(.*?)</span>"""));
                        var architecture = Plain(Group(details, """id="archDiv"[^>]*>.*?</span>(.*?)</div>"""));
                        var declaredIds = Group(details, """id="driverhwIDs"[^>]*>(.*?)</div>\s*</div>""");
                        if (architecture.Length == 0 || declaredIds.Length == 0)
                            throw new DriverScanException("Microsoft Catalog driver details layout was not recognized.");
                        var ids = Rx(@"<div[^>]*>([^<]+)").Matches(declaredIds).Select(match => Plain(match.Groups[1].Value))
                            .Where(value => value.Contains('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (ids.Length == 0) throw new DriverScanException("Microsoft Catalog did not report driver hardware IDs.");
                        var arch = architecture.ToUpperInvariant() switch
                        { "AMD64" or "X64" => "X64", "ARM64" => "Arm64", "X86" => "X86", _ => "" };
                        if (arch.Length == 0) throw new DriverScanException($"Microsoft Catalog reported an unknown architecture: {architecture}.");
                        var target = windowsTarget with { Architecture = arch };
                        if (!DriverCatalogMatching.Targets(target, system) || !inventory.Devices.Where(device => device.Present)
                            .SelectMany(DriverCatalogMatching.Identifiers).Any(deviceId => ids.Contains(deviceId.Value, StringComparer.OrdinalIgnoreCase)))
                            continue;
                        var form = "updateIDs=" + Arg(JsonSerializer.Serialize(new[] { new { size = 0, updateID = row.Id, uidInfo = row.Id } }));
                        var payloads = ParsePayloads(await query.HtmlAsync(descriptor, ct, form).ConfigureAwait(false), row.Id);
                        if (payloads.Any(payload => !OfficialDriverHttp.Approved(Source, payload.Url)))
                            throw new DriverScanException("Microsoft Catalog returned an unapproved distribution URL.");
                        query.Add(row.Id, row.Title, manufacturer, row.Version, row.Date, id, target, landing, payloads,
                            row.Size, descriptor: descriptor, ids: ids, revision: row.Version + "|" + row.Date);
                        found++;
                    }
                    catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{row.Title}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{id}: {ex.Message}"); }
        }
    }

    internal sealed record Listing(string Id, string Title, string Os, string Date, string Version, long? Size);

    internal static Listing[] ParseRows(string html)
    {
        var rows = Row.Matches(html);
        if (rows.Count == 0 && !Rx(@"catalogBody_noResultText|no results (were )?found|did not match any updates").IsMatch(html))
            throw new DriverScanException("Microsoft Catalog search results were blocked or the page layout changed.");
        var listings = rows.Select(row => (Id: row.Groups[1].Value, Cells: Cell.Matches(row.Groups[2].Value).Select(cell => Plain(cell.Groups[1].Value)).ToArray(),
                Bytes: Group(row.Value, """_originalSize["']\s*>\s*(\d+)""")))
            .ToArray();
        if (listings.Any(row => row.Cells.Length < 7)) throw new DriverScanException("Microsoft Catalog search rows have an unrecognized format.");
        return listings.Where(row => row.Cells[3].StartsWith("Drivers", StringComparison.OrdinalIgnoreCase))
            .Select(row => new Listing(row.Id, row.Cells[1], row.Cells[2], row.Cells[4], row.Cells[5], Size(row.Bytes)))
            .ToArray();
    }

    internal static DriverPayload[] ParsePayloads(string response, string id)
    {
        var offer = Rx("""downloadInformation\[(\d+)\]\.updateID\s*=\s*['"]([0-9a-f-]{36})['"]""").Matches(response)
            .FirstOrDefault(match => match.Groups[2].Value.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (offer is null) throw new DriverScanException("Microsoft Catalog did not resolve this update identity.");
        var prefix = @"downloadInformation\[" + offer.Groups[1].Value + @"\]\.files\[(\d+)\]\.";
        var files = Rx(prefix + """(url|digest|sha256)\s*=\s*'([^']*)'""").Matches(response)
            .GroupBy(match => match.Groups[1].Value).Select(group => group.ToDictionary(match => match.Groups[2].Value, match => match.Groups[3].Value))
            .Select(fields => new DriverPayload(new Uri(fields["url"], UriKind.Absolute),
                Hashes(("SHA256", fields.GetValueOrDefault("sha256", "")), ("SHA1", fields.GetValueOrDefault("digest", ""))))).ToArray();
        if (files.Length == 0) throw new DriverScanException("Microsoft Catalog returned no files for this update.");
        return files;
    }

    private static int DevicePriority(string className) => className switch
    { "Display" => 0, "Net" => 1, "Bluetooth" => 2, "Media" => 3, "SCSIAdapter" => 4, _ => 5 };
    private static int? MinimumBuild(Match[] audiences)
    {
        // An unrestricted audience for this Windows generation makes its restricted alternatives optional.
        var matches = audiences.Select(audience => (audience.Groups[1].Value,
            Group(audience.Groups[2].Value, @"version\s+(\d\dH[12])\s+and\s+later").ToUpperInvariant()) switch
        {
            ("10", "21H2") => 19044, ("10", "22H2") => 19045,
            ("11", "21H2") => 22000, ("11", "22H2") => 22621, ("11", "23H2") => 22631,
            ("11", "24H2") => 26100, ("11", "25H2") => 26200, ("11", "26H1") => 28000, _ => (int?)null
        }).ToArray();
        return matches.All(build => build is not null) ? matches.Min() : null;
    }
}
