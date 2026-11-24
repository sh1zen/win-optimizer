using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Infrastructure.Features.Drivers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Drivers;

public sealed class DriverCatalogProviderTests : IDisposable
{
    private readonly DriverScanWorkspace _workspace = new(Path.GetTempPath());
    private const string Hardware = "PCI\\VEN_10DE&DEV_2684&SUBSYS_51001462";
    private static readonly DriverSystemContext Windows = new() { Major = 10, Build = 26200, ProductType = 1, Architecture = "X64" };
    private static DriverInventory Inventory(DriverSystemContext system) =>
        new([new("gpu", "NVIDIA GeForce RTX 4090", "Display", "oem.inf", true, "31.0", [Hardware])], [], true, []) { System = system };

    [Fact]
    public async Task NvidiaSelectsDetectedSeriesAndProductAndRequestsOnlyStudio()
    {
        var requests = new List<Uri>();
        using var http = Transport(request =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            if (uri.Host == "gfwsl.geforce.com")
                return Response("""
                    {"Success":"1","IDS":[{"downloadInfo":{"ID":"42","Name":"NVIDIA%20Studio%20Driver","Version":"561.3",
                    "Release":"560","IsCRD":"1","IsWHQL":"1","ReleaseDateTime":"Sep 12, 2025",
                    "DownloadURLFileSize":"600 MB","DetailsURL":"https://www.nvidia.com/en-us/drivers/details/42/",
                    "DownloadURL":"https://us.download.nvidia.com/Windows/561.3/driver.exe"}}]}
                    """);
            return Response(uri.Query switch
            {
                "?TypeID=1" => Matrix(("GeForce", "1"), ("NVIDIA RTX PRO / RTX / Quadro", "3")),
                "?TypeID=4" => Matrix(("Windows 10 64-bit", "57"), ("Windows 11", "135")),
                "?TypeID=2&ParentID=1" => Matrix(("GeForce RTX 40 Series (Notebooks)", "129"), ("GeForce RTX 40 Series", "127")),
                "?TypeID=3&ParentID=127" => Matrix(("NVIDIA GeForce RTX 4090", "1000"), ("NVIDIA GeForce RTX 4090 D", "1001")),
                _ => throw new InvalidOperationException(uri.AbsoluteUri)
            });
        });
        var result = await new NvidiaDriverSource(http).SearchAsync(new(Inventory(Windows), new() { NvidiaChannels = NvidiaChannels.Studio }), null, default);
        var record = Assert.Single(result.Updates).Catalog!;
        Assert.Equal(DriverSourceState.Success, result.State);
        Assert.Equal("Studio · R560", record.Branch);
        Assert.Equal("561.3", record.PackageVersion);
        Assert.True(record.ClaimsWhql);
        Assert.True(record.LicenseRequired);
        Assert.Equal(DriverCertification.Unverified, result.Updates[0].Certification);
        var lookup = Assert.Single(requests, uri => uri.Host == "gfwsl.geforce.com").Query;
        Assert.Contains("psid=127&pfid=1000&osID=135", lookup);
        Assert.Contains("upCRD=1", lookup);
    }

    [Fact]
    public void AmdPreservesPackageBranchesAndReportsComponentVersionsSeparately()
    {
        var result = AmdDriverSource.ParseReleases(new("https://www.amd.com/en/support/download/drivers.html"), """
            <button class="accordion-button collapsed">Windows 11 - 64-Bit Edition</button>
            <article class="driver-download-details"><h4>AMD Software: Adrenalin Edition</h4>
              <strong>Revision Number</strong><p>Adrenalin 26.11.2 WHQL</p><strong>Release Date</strong><p>2026-11-18</p>
              <a href="https://drivers.amd.com/drivers/adrenalin-26.11.2-win11-a.exe">Download</a>
            </article>
            <article class="driver-download-details"><h4>AMD Software: Adrenalin Edition</h4>
              <strong>Revision Number</strong><p>Adrenalin 26.11.2 WHQL</p>
              <a href="https://drivers.amd.com/drivers/adrenalin-26.11.2-win11-b.exe">Download</a>
            </article>
            <button class="accordion-button collapsed">Ubuntu</button>
            <article class="driver-download-details"><h4>Linux</h4></article>
            """, true);
        Assert.Equal(["Adrenalin A", "Adrenalin B"], result.Select(row => row.Branch));
        Assert.Equal(result[0].Revision, result[1].Revision);
        var chipset = AmdDriverSource.ParseChipsetComponents("""
            <table><tr><td>AMD PCI Device Driver</td><td>1.0.0.91</td><td>1.0.0.91</td></tr>
            <tr><td>AMD GPIO Driver</td><td>2.2.0.138</td><td>2.2.0.138</td></tr></table>
            """);
        Assert.Equal(["AMD PCI Device Driver · 1.0.0.91 · 1.0.0.91", "AMD GPIO Driver · 2.2.0.138 · 2.2.0.138"], chipset);
    }

    [Fact]
    public async Task IntelUsesScopedDownloadAndKeepsItsOwnPublishedDigest()
    {
        var digest = new string('a', 128);
        using var http = Transport(request =>
        {
            Assert.Contains("/19351/", request.RequestUri!.AbsolutePath);
            return Response($"""
                <p>Version: 24.80.0</p>
                <ul id="button-list-items-0"><li>Windows 11*</li><li>Windows 10, 64-bit*</li></ul>
                <div id="sha-key-panel-0"><p><span>SHA512: </span>{digest}</p></div>
                <p><span class="cmp-button-list__meta-type">EXE</span>72.3 MB</p>
                <a id="button-download-0" href="https://downloadmirror.intel.com/929786/WiFi-24.80.0-Driver64-Win10-Win11.exe">Download</a>
                <button>I accept the terms in the license agreement</button>
                <p>This download is valid for the product(s) listed below.</p><ul><li>Intel® Wi-Fi 6E AX210</li></ul>
                """);
        });
        var inventory = new DriverInventory([new("wireless", "Intel Wi-Fi 6E AX210", "Net", "", true, "", ["PCI\\VEN_8086&DEV_2725"])],
            [], true, []) { System = Windows };
        var result = await new IntelDriverSource(http).SearchAsync(new(inventory, new()), null, default);
        var catalog = Assert.Single(result.Updates).Catalog!;
        Assert.Equal("24.80.0", catalog.PackageVersion);
        Assert.True(catalog.LicenseRequired);
        Assert.Equal(new DriverHash("SHA512", digest), Assert.Single(Assert.Single(catalog.Payloads).Hashes));
    }

    [Theory]
    [InlineData(DriverSources.Msi, "Micro-Star International", "MAG B650 TOMAHAWK WIFI", "https://download.msi.com/driver/test.zip")]
    [InlineData(DriverSources.Gigabyte, "Gigabyte Technology", "B650 AORUS ELITE AX", "https://download.gigabyte.com/driver/test.zip")]
    [InlineData(DriverSources.AsRock, "ASRock", "B650 Pro RS", "https://download.asrock.com/Drivers/AMD/test.zip")]
    public async Task BoardAdaptersResolveOfficialListingsForDetectedModels(DriverSources source, string oem, string model, string payload)
    {
        using var http = Transport(request =>
        {
            var uri = request.RequestUri!;
            if (source == DriverSources.Msi)
            {
                Assert.Contains("product=MAG-B650-TOMAHAWK-WIFI", uri.Query);
                return Response("""
                    {"result":{"downloads":{"LAN":[{"download_id":44,"download_title":"Realtek LAN",
                    "download_url":"FILE_URL","download_version":"1.2.0","download_size":"2 MB"}]}}}
                    """.Replace("FILE_URL", payload, StringComparison.Ordinal));
            }
            if (source == DriverSources.Gigabyte)
            {
                if (uri.AbsolutePath.EndsWith("GetProducts", StringComparison.Ordinal))
                    return Response("""{"data":[{"productName":"B650 AORUS ELITE AX (rev. 1.0)","productId":42}]}""");
                Assert.EndsWith("/Support/42", uri.AbsolutePath);
                return Response("""
                    {"data":[{"key":"download","child":[{"key":"driver","data":[{"filePath":"FILE_URL",
                    "fileName":"Realtek LAN","fileVersion":"1.2.0","info":[{"infoParentId":35,"infoName":"Windows 11 64bit"},
                    {"infoParentId":32,"infoName":"LAN"}]}]}]}]}
                    """.Replace("FILE_URL", payload, StringComparison.Ordinal));
            }
            if (uri.AbsolutePath.EndsWith("index.asp", StringComparison.Ordinal))
                return Response("""<script>var allmodels = [['B650 Pro RS','AM5','AMD B650','ATX']];</script>""");
            Assert.Contains("/AMD/B650%20Pro%20RS/Download.html", uri.AbsoluteUri);
            return Response($"""
                <tr class="osW1164"><td>Realtek LAN driver ver:1.2.0</td><td>Windows 11</td><td>2 MB</td><td>2026/10/05</td>
                <td><a href="{payload}" onclick="ClickRate('LAN','1.2.0');">Download</a></td></tr>
                """);
        });
        OfficialDriverSource adapter = source switch
        {
            DriverSources.Msi => new MsiDriverSource(http),
            DriverSources.Gigabyte => new GigabyteDriverSource(http),
            _ => new AsRockDriverSource(http)
        };
        var snapshot = Inventory(Windows with { BoardManufacturer = oem, BoardProduct = model, BoardVersion = "1.0" });
        var result = await adapter.SearchAsync(new(snapshot, new()), null, default);
        var catalog = Assert.Single(result.Updates).Catalog!;
        Assert.Equal(DriverSourceState.Success, result.State);
        Assert.Equal(model, catalog.Target.Model);
        Assert.Equal(payload, Assert.Single(catalog.Payloads).Url.AbsoluteUri);
    }

    [Fact]
    public async Task MicrosoftCatalogChecksDeclaredSubsystemAndResolvesOfficialDialogFiles()
    {
        const string id = "6c078f26-1eb9-48ce-bb43-6979bb672469";
        using var http = Transport(request => request.RequestUri!.AbsolutePath switch
        {
            "/Search.aspx" => Response($"""
                 <tr id="{id}_R0"><td></td><td>NVIDIA Display Driver Update (32.0.16.1088)</td>
                 <td>Windows 11 Client, version 25H2 and later, Servicing Drivers</td>
                 <td>Drivers (Video)</td><td>7/21/2026</td><td>32.0.16.1088</td>
                 <td><span id="{id}_originalSize">123456</span></td><td>Download</td></tr>
                 """),
            "/ScopedViewInline.aspx" => Response("""
                <div id="archDiv"><span>Architecture:</span>AMD64</div>
                <div id="driverhwIDs"><div>pci\ven_10de&amp;dev_2684&amp;subsys_51001462</div></div>
                """),
            "/DownloadDialog.aspx" when request.Method == HttpMethod.Post => Response($"""
                downloadInformation[0].updateID ='{id}';
                downloadInformation[0].files[0].url = 'https://catalog.s.download.windowsupdate.com/drivers/a.cab';
                downloadInformation[0].files[0].sha256 = 'tQvAkOV2phPJoSYzUCFthi5XCuuoeS9EZd+Zi59AOis=';
                downloadInformation[0].files[1].url = 'https://catalog.s.download.windowsupdate.com/drivers/b.cab';
                """),
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
        });
        var source = new MicrosoftDriverCatalogSource(http);
        var result = await source.SearchAsync(new(Inventory(Windows), new()), null, default);
        var update = Assert.Single(result.Updates);
        Assert.Equal(DriverSourceState.Success, result.State);
        Assert.Equal(26200, update.Catalog!.Target.MinBuild);
        Assert.Equal(Hardware, Assert.Single(update.Catalog.ListedHardwareIds), ignoreCase: true);
        Assert.Equal(2, update.Catalog.Payloads.Length);
        Assert.Equal("SHA256", Assert.Single(update.Catalog.Payloads[0].Hashes).Algorithm);
        Assert.Empty(update.Version);
    }

    [Fact]
    public async Task LenovoResolvesPayloadFromExactMachineTypeAndDescriptorChecksum()
    {
        const string descriptor = """
            <Package id="n3ali04w" version="30.100.2148.1"><Title><Desc id="EN">Intel Serial IO Driver</Desc></Title>
              <Vendor>Lenovo</Vendor><ReleaseDate>2022-05-30</ReleaseDate>
              <Dependencies><_PnPID>ACPI\INTC1055</_PnPID></Dependencies>
              <Files><Installer><File><Name>n3ali04w.exe</Name><CRC>7B523EA91EF8B959EEE41ABD621E04BF1306BE8B26B5AEF05639F9382BAF0C9C</CRC><Size>1418000</Size></File></Installer></Files>
            </Package>
            """;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
        using var http = Transport(request => request.RequestUri!.AbsolutePath switch
        {
            "/cdrt/td/catalogv2.xml" => Response("<ModelList><Model name='ThinkPad Test'><Types><Type>21CB</Type></Types></Model></ModelList>"),
            "/catalog/21cb_win11.xml" => Response($"""
                <packages count="1"><package><location>https://download.lenovo.com/pccbbs/mobiles/n3ali04w_2_.xml</location>
                <category>Motherboard Devices</category><checksum type="sha256">{hash}</checksum></package></packages>
                """),
            "/pccbbs/mobiles/n3ali04w_2_.xml" => Response(descriptor),
            _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath)
        });
        var inventory = Inventory(Windows with { Manufacturer = "LENOVO", Sku = "LENOVO_MT_21CB_BU_Think", Model = "ThinkPad Test" });
        var result = await new LenovoDriverSource(http).SearchAsync(new(inventory, new()), null, default);
        var package = Assert.Single(result.Updates).Catalog!;
        Assert.Equal("ThinkPad Test", package.Target.Model);
        Assert.Equal("https://download.lenovo.com/pccbbs/mobiles/n3ali04w.exe", Assert.Single(package.Payloads).Url.AbsoluteUri);
        Assert.Equal("ACPI\\INTC1055", Assert.Single(package.ListedHardwareIds));
    }

    [Fact]
    public async Task MalformedRefreshKeepsLastUsableCatalogAndReportsStaleCoverage()
    {
        var clock = new TestClock();
        var calls = 0;
        using var http = Transport(_ => ++calls == 1 ? Response("""
            <tr><td><a href="/Download/ToDownload?mode=first&amp;type=direct&amp;downloadid=4103">Fast Download</a></td>
              <td>Win11 Auto Installation Program (NetAdapterCx)</td><td>11.031.50</td><td>2026/10/05</td><td>3 MB</td></tr>
            """) : Response("<html><title>Request Rejected</title></html>"), clock);
        var inventory = new DriverInventory([new("realtek", "Realtek Ethernet", "Net", "", true, "", ["PCI\\VEN_10EC&DEV_8125"])], [], true, [])
        { System = Windows };
        var source = new RealtekDriverSource(http);
        var context = new DriverDiscoveryContext(inventory, new());
        var first = await source.SearchAsync(context, null, default);
        Assert.Equal(DriverSourceState.Partial, first.State);
        var firstRecord = Assert.Single(first.Updates);
        clock.Now += TimeSpan.FromHours(13);
        var offline = await source.SearchAsync(context, null, default);
        Assert.Equal(DriverSourceState.Stale, offline.State);
        Assert.Equal(firstRecord.Id, Assert.Single(offline.Updates).Id);
        Assert.Contains(offline.Notices, reason => reason.Contains("CAPTCHA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MetadataCacheSendsValidatorsAndDistinguishesNetworkFailure()
    {
        var clock = new TestClock();
        var calls = 0;
        using var http = Transport(request =>
        {
            if (++calls == 1)
            {
                var response = Response("cached response");
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
                return response;
            }
            Assert.Equal("\"fixture\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            return new(HttpStatusCode.ServiceUnavailable);
        }, clock);
        var url = new Uri("https://www.asus.com/support/");
        Assert.Null((await http.ReadAsync(DriverSources.Asus, url, default)).StaleReason);
        clock.Now += TimeSpan.FromHours(13);
        var result = await http.ReadAsync(DriverSources.Asus, url, default);
        Assert.Equal("cached response", result.Text);
        Assert.NotNull(result.StaleReason);
    }

    private static string Matrix(params (string Name, string Id)[] values) =>
        "<LookupValueSearch><LookupValues>" + string.Concat(values.Select(value =>
            $"<LookupValue><Name>{value.Name}</Name><Value>{value.Id}</Value></LookupValue>")) + "</LookupValues></LookupValueSearch>";

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private OfficialDriverHttp Transport(Func<HttpRequestMessage, HttpResponseMessage> handler, TimeProvider? clock = null) =>
        new(new HttpClient(new FakeHttp(handler)), Path.Combine(_workspace.Root, "cache"), clock);

    public void Dispose() => _workspace.Dispose();

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
