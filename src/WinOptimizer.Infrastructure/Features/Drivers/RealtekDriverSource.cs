using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Collects public PCIe Ethernet releases and exposes the publisher's interactive download step.</summary>
internal sealed class RealtekDriverSource(OfficialDriverHttp http) : OfficialDriverSource(http, DriverSources.Realtek)
{
    protected override Uri SupportPage { get; } = new("https://www.realtek.com/Download/List?cate_id=584");
    private static readonly Uri AudioPage = new("https://www.realtek.com/Download/List?cate_id=593");

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var devices = Devices(inventory, "VEN_10EC&").ToArray();
        var ethernet = devices.Where(device => device.ClassName == "Net" && device.HardwareIds.Any(id =>
            Rx(@"^PCI\\VEN_10EC&DEV_(?:8125|8126|8127|8168|8169)\b").IsMatch(id))).ToArray();
        var audio = Windows11(inventory.System) ? [] : devices.Where(device =>
            device.ClassName.Equals("Media", StringComparison.OrdinalIgnoreCase) && device.HardwareIds.Any(id =>
                id.StartsWith("HDAUDIO\\FUNC_01&VEN_10EC&", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (ethernet.Length == 0 && audio.Length == 0 || !Client(inventory.System))
        { query.Unsupported("No supported Realtek PCIe Ethernet or Windows 10 legacy HDA device was detected. Other components use OEM or Microsoft sources."); return; }
        if (ethernet.Length > 0)
        {
            try
            {
                await CollectPageAsync(query, inventory.System, SupportPage, ethernet, "PCIe Ethernet", description =>
                    Contains(description, "Auto Installation Program") && (Windows11(inventory.System)
                        ? Contains(description, "Win11") : Contains(description, "Win10")), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure(ex.Message); }
        }
        if (audio.Length > 0)
        {
            query.Manual = ethernet.Length == 0 ? AudioPage : SupportPage;
            try
            {
                await CollectPageAsync(query, inventory.System, AudioPage, audio, "Legacy HDA", description =>
                    Contains(description, "Windows10") && Contains(description, "Driver only") &&
                    Contains(description, inventory.System.Architecture == "X64" ? "64bits" : "32bits"), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure(ex.Message); }
        }
        query.Partial("Realtek's public downloads require interactive verification. Other Realtek components use OEM or Microsoft catalogs.");
    }

    private static async Task CollectPageAsync(Query query, DriverSystemContext system, Uri catalog, DriverDevice[] devices,
        string family, Func<string, bool> relevant, CancellationToken ct)
    {
        var html = await query.HtmlAsync(catalog, ct).ConfigureAwait(false);
        var rows = Rx(@"<tr\b[^>]*>(.*?)</tr>").Matches(html)
            .Select(row => (Html: row.Groups[1].Value, Cells: Rx(@"<td\b[^>]*>(.*?)</td>").Matches(row.Groups[1].Value)
                .Select(cell => Plain(cell.Groups[1].Value)).ToArray()))
            .Where(row => row.Cells.Length == 5 && relevant(row.Cells[1])).ToArray();
        if (rows.Length == 0) throw new DriverScanException($"Realtek's {family} release table was not recognized.");
        foreach (var (row, cells) in rows)
        {
            var link = Group(row, """href="([^"]*/ToDownload\?[^"]+)""");
            var id = Group(link, @"downloadid=(\d+)");
            if (id.Length == 0) { query.Partial($"{cells[1]} has no official download action."); continue; }
            var page = Link(catalog, link);
            query.Add(id, cells[1], "Realtek", cells[2], cells[3], family, ClientTarget(system),
                page, [], Size(cells[4]), cells[1], page,
                devices.SelectMany(device => device.HardwareIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }
}
