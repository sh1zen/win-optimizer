using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Follows NVIDIA's product matrix and selects channels for each detected GPU.</summary>
internal sealed class NvidiaDriverSource(OfficialDriverHttp http) : OfficialDriverSource(http, DriverSources.Nvidia)
{
    protected override Uri SupportPage { get; } = new("https://www.nvidia.com/en-us/drivers/");
    private const string Matrix = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx";
    private const string Lookup = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup";

    private sealed record Option(string Name, string Id);

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var system = inventory.System;
        var devices = Devices(inventory, "PCI\\VEN_10DE&", "Display").DistinctBy(device => ProductKey(device.Name)).ToArray();
        if (devices.Length == 0 || !Client(system) || system.Architecture is not ("X64" or "X86"))
        { query.Unsupported("No supported NVIDIA display device and Windows client platform were detected."); return; }

        var types = await OptionsAsync(query, 1, "", ct).ConfigureAwait(false);
        var operatingSystems = await OptionsAsync(query, 4, "", ct).ConfigureAwait(false);
        var osName = Windows11(system) ? "Windows 11" : system.Architecture == "X64" ? "Windows 10 64-bit" : "Windows 10 32-bit";
        var os = operatingSystems.FirstOrDefault(item => item.Name.Equals(osName, StringComparison.OrdinalIgnoreCase));
        if (os is null) { query.Unsupported("NVIDIA's driver matrix does not list this Windows platform."); return; }

        foreach (var device in devices.Take(4))
        {
            try
            {
                var consumer = Contains(device.Name, "GeForce") || Contains(device.Name, "TITAN");
                var type = types.FirstOrDefault(item => consumer ? item.Name == "GeForce"
                    : Contains(device.Name, "RTX") || Contains(device.Name, "Quadro") ? Contains(item.Name, "Quadro") : false);
                if (type is null) { query.Unsupported($"{device.Name}: no supported NVIDIA product type was identified."); continue; }
                var availableSeries = await OptionsAsync(query, 2, type.Id, ct).ConfigureAwait(false);
                var candidates = SelectSeries(device.Name, availableSeries).Take(8);
                Option? series = null;
                Option? product = null;
                foreach (var candidate in candidates)
                {
                    var products = await OptionsAsync(query, 3, candidate.Id, ct).ConfigureAwait(false);
                    product = products.FirstOrDefault(item => ProductKey(item.Name) == ProductKey(device.Name));
                    if (product is not null) { series = candidate; break; }
                }
                if (series is null || product is null)
                { query.Unsupported($"{device.Name}: the NVIDIA matrix did not return an exact product match."); continue; }

                var legacy = consumer && !Rx(@"\b(?:RTX\s+[2-9]\d{3}|GTX\s+16\d{2})\b").IsMatch(device.Name);
                var channels = !consumer || legacy ? new[] { ("null", legacy ? "Legacy" : "Professional") } :
                    Channels(options.NvidiaChannels).ToArray();
                var before = query.Updates.Count;
                foreach (var (studio, branch) in channels)
                {
                    try
                    {
                        var matched = await LookupAsync(query, device, series.Id, product.Id, os.Id, studio, branch, "1", system, ct)
                            .ConfigureAwait(false);
                        if (!matched && (legacy || !Windows11(system)))
                            matched = await LookupAsync(query, device, series.Id, product.Id, os.Id, studio, branch, "0", system, ct)
                                .ConfigureAwait(false);
                        if (!matched) query.Partial($"{device.Name}: NVIDIA returned no {branch} offer for this Windows platform.");
                    }
                    catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{device.Name} ({branch}): {ex.Message}"); }
                }
                if (query.Updates.Count == before) query.Unsupported($"{device.Name}: no matching NVIDIA package was listed.");
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{device.Name}: {ex.Message}"); }
        }
        if (devices.Length > 4) query.Partial("Only the first four distinct NVIDIA models were checked.");
    }

    internal static IEnumerable<(string Studio, string Branch)> Channels(NvidiaChannels preference)
    {
        if (preference.HasFlag(NvidiaChannels.GameReady)) yield return ("0", "Game Ready");
        if (preference.HasFlag(NvidiaChannels.Studio)) yield return ("1", "Studio");
    }

    private static async Task<Option[]> OptionsAsync(Query query, int level, string parent, CancellationToken ct)
    {
        var xml = await query.XmlAsync(new($"{Matrix}?TypeID={level}" + (parent.Length > 0 ? "&ParentID=" + Arg(parent) : "")), ct)
            .ConfigureAwait(false);
        if (xml.Root?.Name.LocalName != "LookupValueSearch") throw new DriverScanException("NVIDIA's product matrix format changed.");
        return Descendants(xml, "LookupValue").Select(item => new Option(Field(item, "Name"), Field(item, "Value")))
            .Where(item => item.Name.Length > 0 && item.Id.All(char.IsAsciiDigit) && item.Id.Length > 0).ToArray();
    }

    private static IEnumerable<Option> SelectSeries(string device, Option[] series)
    {
        var mobile = IsMobile(device);
        var generation = Group(device, @"(?:RTX|GTX|GT|GTS|G)\s+([1-9]\d{2,3})\b");
        var prefix = generation.Length == 4 ? generation[..2] : generation.Length == 3 ? generation[..1] : "";
        var names = series.Where(item => IsMobile(item.Name) == mobile || Contains(device, "Quadro"));
        if (prefix.Length > 0)
        {
            var named = names.Where(item => Rx(@"\b" + prefix + @"(?:00)? Series\b").IsMatch(item.Name)).ToArray();
            if (named.Length > 0) return named;
        }
        return names.OrderByDescending(item => item.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(word => word.Length > 2 && Contains(device, word)));
    }

    private static bool IsMobile(string name) => Contains(name, "Notebook") || Contains(name, "Laptop") ||
        Rx(@"\d[MX]\b|Max-Q").IsMatch(name);

    private static string ProductKey(string name) => ModelKey(Rx(@"\b(NVIDIA|Laptop|Notebook|GPU|GeForce|Max-Q|with Max-Q Design)\b")
        .Replace(name, ""));

    private async Task<bool> LookupAsync(Query query, DriverDevice device, string series, string product, string os,
        string studio, string branch, string dch, DriverSystemContext system, CancellationToken ct)
    {
        var endpoint = new Uri($"{Lookup}&psid={series}&pfid={product}&osID={os}&languageCode=1033&isWHQL=0&beta=0&dltype=-1&dch={dch}&upCRD={studio}&numberOfResults=3");
        using var json = await query.JsonAsync(endpoint, ct).ConfigureAwait(false);
        var root = json.RootElement;
        if (!root.TryGetProperty("IDS", out var results) || results.ValueKind != System.Text.Json.JsonValueKind.Array ||
            !int.TryParse(Text(root, "Success"), out var count) || count != results.GetArrayLength())
            throw new DriverScanException("NVIDIA's release response format changed.");
        var any = false;
        foreach (var item in results.EnumerateArray())
        {
            var row = item.GetProperty("downloadInfo");
            var title = Uri.UnescapeDataString(Text(row, "Name"));
            if (studio != "null" && (Text(row, "IsCRD") == "1") != (studio == "1")) continue;
            var id = Text(row, "ID");
            var landing = OptionalLink(SupportPage, Text(row, "DetailsURL")) ?? SupportPage;
            var download = OptionalLink(SupportPage, Text(row, "DownloadURL"));
            var release = Text(row, "Release");
            var channel = branch + (release.Length > 0 ? $" · R{release}" : "") + (dch == "0" ? " · Standard" : "");
            if (download is null) query.Partial($"{title}: NVIDIA did not publish a package URL.");
            query.Add($"{series}/{product}/{id}/{studio}/{dch}", title, "NVIDIA", Text(row, "Version"),
                Text(row, "ReleaseDateTime"), device.Name, ClientTarget(system), landing, download is null ? [] : [new(download, [])],
                Size(Text(row, "DownloadURLFileSize")), channel, endpoint, device.HardwareIds,
                Text(row, "IsWHQL") == "1" || download is not null && Contains(download.AbsolutePath, "-whql.exe"), license: true);
            any = true;
        }
        return any;
    }
}
