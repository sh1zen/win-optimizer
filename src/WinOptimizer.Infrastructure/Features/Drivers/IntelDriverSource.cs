using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Reads official Intel family pages only for related devices on the current PC.</summary>
internal sealed class IntelDriverSource(OfficialDriverHttp http) : OfficialDriverSource(http, DriverSources.Intel)
{
    protected override Uri SupportPage { get; } = new("https://www.intel.com/content/www/us/en/download-center/home.html");
    private const string Downloads = "https://www.intel.com/content/www/us/en/download/";
    private sealed record Family(string Name, string Page, DriverDevice[] Devices);

    protected override async Task CollectAsync(Query query, DriverInventory inventory, DriverScanOptions options, CancellationToken ct)
    {
        var system = inventory.System;
        if (!Client(system) || system.Architecture != "X64")
        { query.Unsupported("The Intel family pages in this adapter target x64 Windows clients."); return; }
        var families = Families(inventory).ToArray();
        if (families.Length == 0) { query.Unsupported("No Intel devices in a supported driver family were detected."); return; }
        foreach (var family in families)
        {
            var page = new Uri(Downloads + family.Page);
            try
            {
                var html = await query.HtmlAsync(page, ct).ConfigureAwait(false);
                var files = ParseFiles(html);
                var products = Plain(Group(html, """(?:id="ValidFor"|This download is valid for the product)(.*?)</ul>"""));
                var wifi = family.Name == "Wi-Fi";
                var devices = wifi ? family.Devices.Where(device =>
                {
                    var model = Group(device.Name, @"\b((?:AX|BE)\d{3}|\d{4})\b");
                    return model.Length > 0 && Rx(@"\b" + model + @"\b").IsMatch(products.Length > 5 ? products : Plain(html)) &&
                        !Rx(@"\b" + model + @"\b.{0,80}(?:discontinued|no longer included)").IsMatch(Plain(html));
                }).ToArray() : family.Devices;
                if (devices.Length == 0)
                { query.Unsupported($"{family.Name}: no detected model could be confirmed in the published family."); continue; }
                if (devices.Length != family.Devices.Length) query.Partial($"{family.Name}: some detected models are outside the published family.");

                var version = Group(html, """(?:data-version|data-release-version)="([\d.]+)""");
                if (version.Length == 0) version = Group(Plain(html), @"(?:Latest\s+)?Version\s*:?\s*(\d+(?:\.\d+)+)");
                var date = Group(html, """(?:datePublished|dateModified)"\s*:\s*"(\d{4}-\d{2}-\d{2})""");
                if (date.Length == 0) date = Group(Plain(html), @"Version\s*:?\s*[\d.]+\s+([A-Za-z]+ \d{1,2}, \d{4})");
                foreach (var file in files)
                {
                    var os = Rx(@"[®™*]").Replace(file.Os, "");
                    if (Contains(file.Url.AbsolutePath, "32bit") || Contains(file.Url.AbsolutePath, "-32-") ||
                        (!Windows11(system) && Contains(os, "Windows 11") && !Contains(os, "Windows 10")) ||
                        (Windows11(system) && Contains(os, "Windows 10") && !Contains(os, "Windows 11"))) continue;
                    var resolvedVersion = version.Length > 0 ? version : Group(file.Url.AbsolutePath, @"(\d+(?:\.\d+){1,3})");
                    query.Add(file.Url.AbsolutePath, "Intel " + family.Name, "Intel", resolvedVersion, date, family.Name,
                        ClientTarget(system) with { Os = file.Os.Length == 0 ? ClientTarget(system).Os : file.Os }, page,
                        [new(file.Url, file.Hashes)], file.Bytes, family.Name, page,
                        devices.SelectMany(device => device.HardwareIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        license: Contains(html, "I accept the terms in the license agreement"));
                }
                if (!query.Updates.Any(update => update.Catalog?.Family == family.Name))
                    query.Unsupported($"{family.Name}: no download was published for the detected Windows version.");
            }
            catch (Exception ex) when (IsSourceError(ex)) { query.Failure($"{family.Name}: {ex.Message}"); }
        }
    }

    private static IEnumerable<Family> Families(DriverInventory inventory)
    {
        var pci = Devices(inventory, "PCI\\VEN_8086&").ToArray();
        var graphics = pci.Where(device => device.ClassName == "Display").ToArray();
        var processors = string.Join(" ", inventory.System.ProcessorNames);
        if (graphics.Length > 0)
        {
            var arc = graphics.Where(device => Contains(device.Name, "Arc")).ToArray();
            if (arc.Length > 0) yield return new("Arc graphics", "785597/intel-arc-iris-xe-graphics-windows.html", arc);
            var integrated = graphics.Except(arc).ToArray();
            if (integrated.Length > 0)
            {
                var generations = Group(processors, @"\bi[3579]-(\d{1,2})\d{3}[a-z]{0,2}\b");
                if (generations is "7" or "8" or "9" or "10")
                    yield return new("7th–10th generation graphics", "776137/intel-7th-10th-gen-processor-graphics-windows.html", integrated);
                else if (generations is "11" or "12" or "13" or "14" || integrated.Any(device => Contains(device.Name, "Iris Xe")))
                    yield return new("11th–14th generation graphics", "864990/intel-11th-14th-gen-processor-graphics-windows.html", integrated);
                else if (Contains(processors, "Core Ultra"))
                    yield return new("Core Ultra graphics", "785597/intel-arc-iris-xe-graphics-windows.html", integrated);
            }
        }
        var network = pci.Where(device => device.ClassName == "Net").ToArray();
        var wireless = network.Where(device => Contains(device.Name, "Wi-Fi") || Contains(device.Name, "Wireless")).ToArray();
        var legacy = wireless.Where(device => Rx(@"\bAX200\b").IsMatch(device.Name)).ToArray();
        var modern = wireless.Except(legacy).ToArray();
        if (modern.Length > 0)
            yield return new("Wi-Fi", "19351/intel-wireless-wi-fi-drivers-for-windows-10-and-windows-11.html", modern);
        if (legacy.Length > 0)
            yield return new("AX200 Wi-Fi", "915475/intel-wireless-wi-fi-drivers-for-intel-wi-fi-6-ax200.html", legacy);
        var bluetooth = Devices(inventory, "USB\\VID_8087&", "Bluetooth").ToArray();
        var legacyBluetooth = bluetooth.Where(device => device.HardwareIds.Any(id => Contains(id, "VID_8087&PID_0029"))).ToArray();
        var modernBluetooth = bluetooth.Except(legacyBluetooth).ToArray();
        if (modernBluetooth.Length > 0)
            yield return new("Bluetooth", "18649/intel-wireless-bluetooth-for-windows-10-and-windows-11.html", modernBluetooth);
        if (legacyBluetooth.Length > 0)
            yield return new("AX200 Bluetooth", "874349/intel-wireless-bluetooth-driver-for-intel-wi-fi-6-ax200.html", legacyBluetooth);
        var ethernet = network.Except(wireless).Where(device => Contains(device.Name, "Ethernet") || Contains(device.Name, "Gigabit")).ToArray();
        if (ethernet.Length > 0)
            yield return new("Ethernet", "15084/intel-ethernet-adapter-complete-driver-pack.html", ethernet);
        var chipset = pci.Where(device => device.ClassName == "System" &&
            (Contains(device.Name, "Chipset") || Contains(device.Name, "LPC") || Contains(device.Name, "SMBus"))).ToArray();
        if (chipset.Length > 0)
            yield return new("Chipset INF", "19347/chipset-inf-utility.html", chipset);
    }

    internal sealed record FileListing(Uri Url, DriverHash[] Hashes, long? Bytes, string Os);

    internal static FileListing[] ParseFiles(string html)
    {
        var links = Rx("""<a\b[^>]*\bid="button-download-(\d+)"[^>]*\bhref="(https://downloadmirror\.intel\.com/[^"]+\.(?:exe|zip|msi))"[^>]*>""").Matches(html);
        if (links.Count == 0) throw new DriverScanException("Intel's page did not expose a recognized official download.");
        return links.Select(link =>
        {
            var id = link.Groups[1].Value;
            var boundary = html.LastIndexOf("cmp-button-list__meta-type", link.Index, StringComparison.OrdinalIgnoreCase);
            var type = boundary >= 0 ? Plain(html[boundary..link.Index]) : "";
            var checks = Plain(Group(html, $"id=\"sha-key-panel-{id}\"[^>]*>(.*?)</p>"));
            var sha512 = Group(checks, @"SHA\s*-?\s*512\s*:?\s*([0-9a-f]{128})(?![0-9a-f])");
            var sha256 = Group(checks, @"SHA\s*-?\s*256\s*:?\s*([0-9a-f]{64})(?![0-9a-f])");
            var os = Plain(Group(html, $"id=\"button-list-items-{id}\"[^>]*>(.*?)</ul>"));
            return new FileListing(new(System.Net.WebUtility.HtmlDecode(link.Groups[2].Value)),
                Hashes(("SHA512", sha512), ("SHA256", sha256)), Size(Group(type, @"(\d+(?:[.,]\d+)?\s*[KMG]B)")), os);
        }).DistinctBy(file => file.Url).ToArray();
    }
}
