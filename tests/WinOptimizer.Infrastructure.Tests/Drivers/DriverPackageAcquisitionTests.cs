using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Infrastructure.Features.Drivers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Drivers;

public sealed class DriverPackageAcquisitionTests : IDisposable
{
    private readonly DriverScanWorkspace _workspace = new(Path.GetTempPath());
    private const string Hardware = "PCI\\VEN_1234&DEV_5678";
    private const string OemUrl = "https://dlcdnets.asus.com/pub/drivers.zip";
    private static readonly DriverInventory Inventory = new([new("device", "Device", "Net", "oem.inf", true, "1.0", [Hardware])], [], true, [])
    { System = new() { Major = 10, Build = 26100, Architecture = "X64" } };

    [Fact]
    public async Task CompletePackagePreservesAllInfsAndVerifiedContentIsReused()
    {
        var archive = Zip(("driver.inf", "test"), ("addon.inf", "second"));
        var calls = 0;
        using var http = Transport((_, _) =>
        {
            calls++;
            return Task.FromResult(Payload(archive));
        });
        var service = Service(http);
        var update = Candidate(OemUrl, new DriverHash("SHA256", Convert.ToHexString(SHA256.HashData(archive))));
        var stages = new List<string>();
        var progress = new Mock<IProgress<DriverScanProgress>>();
        progress.Setup(item => item.Report(It.IsAny<DriverScanProgress>())).Callback<DriverScanProgress>(value =>
        {
            stages.Add(value.Stage);
            if (value.Stage == "Drivers_ExtractingPackage") Assert.Empty(Directory.EnumerateFiles(_workspace.Root, "*.inf", SearchOption.AllDirectories));
            if (value.Stage == "Drivers_VerifyingInfs") Assert.Equal(2, Directory.EnumerateFiles(_workspace.Root, "*.inf", SearchOption.AllDirectories).Count());
        });
        var verified = await service.AcquireAsync(update, Inventory, false, progress.Object, default);
        Assert.Equal(["Drivers_Downloading", "Drivers_VerifyingPackages", "Drivers_ExtractingPackage", "Drivers_VerifyingInfs"], stages.Distinct());
        Assert.Null(verified.OutputPath);
        Assert.Equal(DriverVerification.Verified, verified.Update.Catalog!.Verification);
        Assert.Equal("8.9", verified.Update.Catalog.PackageVersion);
        Assert.Equal("2.0", verified.Update.Version);
        Assert.Equal(2, verified.Update.Catalog.Components.Length);
        var downloaded = await service.AcquireAsync(verified.Update, Inventory, true, null, default);
        Assert.Equal(1, calls);
        Assert.Equal(archive, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(downloaded.OutputPath!, "*", SearchOption.AllDirectories))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.Root, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(DriverSources.Asus, "https://dlcdnets.asus.com/pub/drivers.zip", "exceeds its size limit")]
    [InlineData(DriverSources.Nvidia, "https://download.nvidia.com/driver.exe", "ended before its published content length")]
    public async Task AutomaticTransferEnforcesSourceSizeRegardlessOfProgress(DriverSources source, string url, string reason)
    {
        var candidate = Candidate(url);
        candidate = candidate with { Catalog = candidate.Catalog! with
        { References = [candidate.Catalog.References[0] with { Source = source }] } };
        using var http = Transport((_, _) =>
        {
            var response = Payload(new byte[8]);
            response.Content.Headers.ContentLength = 1100L << 20;
            return Task.FromResult(response);
        });

        var error = await Assert.ThrowsAsync<DriverScanException>(() => Service(http).AcquireAsync(candidate, Inventory, false,
            Mock.Of<IProgress<DriverScanProgress>>(), default));

        Assert.Contains(reason, error.Message);
        Assert.Empty(Directory.EnumerateFiles(_workspace.Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AutomaticPackageDownloadsCanOverlapAndReportTransferredBytes()
    {
        var archive = Zip(("driver.inf", "test"));
        var twoRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = Transport(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 2) twoRequests.SetResult();
            await twoRequests.Task.WaitAsync(ct);
            return Payload(archive);
        });
        var service = Service(http);
        var progress = new Mock<IProgress<DriverScanProgress>>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var downloads = await Task.WhenAll(
            service.AcquireAsync(Candidate(OemUrl), Inventory, false, progress.Object, timeout.Token),
            service.AcquireAsync(Candidate(OemUrl.Replace("drivers", "alternate", StringComparison.Ordinal)), Inventory,
                false, progress.Object, timeout.Token));

        Assert.All(downloads, download => Assert.Equal(DriverVerification.Verified, download.Update.Catalog!.Verification));
        progress.Verify(item => item.Report(It.Is<DriverScanProgress>(value =>
            value.BytesTransferred == archive.Length && value.TotalBytes == archive.Length)), Times.Exactly(2));
    }

    [Fact]
    public async Task DownloadRejectsBytesThatDifferFromTheVerifiedCandidate()
    {
        var archive = Zip(("driver.inf", "test"));
        using var http = Transport((_, _) => Task.FromResult(Payload(archive)));
        var update = Candidate(OemUrl, new DriverHash("SHA256", Convert.ToHexString(SHA256.HashData(archive))));
        var selected = update with
        {
            Catalog = update.Catalog! with { Verification = DriverVerification.Verified, ContentSha256 = [new string('0', 64)] }
        };

        var error = await Assert.ThrowsAsync<DriverScanException>(() => Service(http).AcquireAsync(selected, Inventory, true, null, default));

        Assert.Contains("changed since verification", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "downloads")));
    }

    [Theory]
    [InlineData("https://example.org/file.zip")]
    [InlineData("http://dlcdnets.asus.com/file.zip")]
    [InlineData("https://dlcdnets.asus.com.example.org/file.zip")]
    public async Task RedirectOutsideOfficialHttpsBoundaryIsRejected(string location)
    {
        var calls = 0;
        using var http = Transport((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new(location);
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<DriverScanException>(() => Service(http).AcquireAsync(Candidate(OemUrl), Inventory, true, null, default));
        Assert.Contains("Unapproved", error.Message);
        Assert.Equal(1, calls);
        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "downloads")));
    }

    [Fact]
    public async Task HashMismatchNeverPublishesOrCachesThePayload()
    {
        var bytes = Zip(("driver.inf", "test"));
        using var http = Transport((_, _) => Task.FromResult(Payload(bytes)));
        var update = Candidate(OemUrl, new DriverHash("SHA256", new string('0', 64)));
        var error = await Assert.ThrowsAsync<DriverScanException>(() => Service(http).AcquireAsync(update, Inventory, true, null, default));
        Assert.Contains("published SHA256 digest", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_workspace.Root, "payload-cache")));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "downloads")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace.Root, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../outside.inf")]
    [InlineData("C:/outside.inf")]
    [InlineData("dev/COM1.txt")]
    [InlineData("driver.inf:extra")]
    public async Task UnsafeZipPathsAreRejectedBeforePublication(string name)
    {
        using var http = Transport((_, _) => Task.FromResult(Payload(Zip((name, "data")))));
        await Assert.ThrowsAsync<DriverScanException>(() => Service(http).AcquireAsync(Candidate(OemUrl), Inventory, true, null, default));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "downloads")));
    }

    [Fact]
    public async Task ZipLinkEntriesAreRejected()
    {
        var path = Path.Combine(_workspace.Root, "link.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("driver.inf");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("outside");
        }
        var root = Directory.CreateDirectory(Path.Combine(_workspace.Root, "extracted")).FullName;
        await Assert.ThrowsAsync<IOException>(() => DriverArchive.ExtractZipAsync(path, root, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task SevenZipOverlayIsReadAsAnArchiveWithoutLaunchingItsContainer()
    {
        // A small compressed archive with one synthetic INF, stored in a PE-like envelope for the overlay reader.
        var archive = Convert.FromBase64String(
            "N3q8ryccAANggn82FAAAAAAAAAB8AAAAAAAAAEYJww4AOhlKzhz2vSueW/VqfP//bRwAAAEEBgABCRQABwsBAAEjAwEBBV0AAIAADAkACAoBsq4I0wAABQERJwBmAGkAeAB0AHUAcgBlAC0AZAByAGkAdgBlAHIALgBpAG4AZgAAABQKAQAAN2H8pnvdARIKAQCgkDH7pnvdARMKAQAA2NBdnnvdARUGAQAggLaBAAA=");
        var container = Path.Combine(_workspace.Root, "container.exe");
        await File.WriteAllBytesAsync(container, [.. "MZ"u8.ToArray(), .. new byte[512], .. archive, 0xFF, 0xAA]);
        var carved = Path.Combine(_workspace.Root, "carved.7z");
        Assert.True(await DriverArchive.ExtractSevenZipOverlayAsync(container, carved, default));
        Assert.Equal(archive, await File.ReadAllBytesAsync(carved));
        var root = Directory.CreateDirectory(Path.Combine(_workspace.Root, "extracted")).FullName;
        await DriverArchive.ExtractSevenZipAsync(carved, root, default, new DriverArchive.Budget());
        Assert.Equal("test data", await File.ReadAllTextAsync(Path.Combine(root, "fixture-driver.inf")));
    }

    [Fact]
    public void WorkspaceCleansReadOnlyFilesFromNativeExports()
    {
        var workspace = new DriverScanWorkspace(_workspace.Root);
        var catalog = Path.Combine(workspace.Root, "driver.cat");
        File.WriteAllText(catalog, "synthetic native export");
        File.SetAttributes(catalog, FileAttributes.ReadOnly);
        workspace.Dispose();
        Assert.False(Directory.Exists(workspace.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationKeepsVerifiedOutputAndPartialCacheEmpty(bool download)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var http = Transport(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Delay should be cancelled");
        });
        var operation = Service(http).AcquireAsync(Candidate(OemUrl), Inventory, download, null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Empty(Directory.EnumerateFiles(_workspace.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LicenseIsNeverAcceptedByTheAutomaticBoundary()
    {
        using var http = Transport((_, _) => throw new InvalidOperationException("The license should prevent any HTTP payload request."));
        var candidate = Candidate(OemUrl);
        candidate = candidate with { Catalog = candidate.Catalog! with { LicenseRequired = true } };
        var error = await Assert.ThrowsAsync<DriverScanException>(() => Service(http).AcquireAsync(candidate, Inventory, false, null, default));
        Assert.Contains("license", error.Message);
    }

    private static DriverUpdate Candidate(string url, params DriverHash[] hashes)
    {
        var uri = new Uri(url);
        var source = new DriverSourceReference(DriverSources.Asus, "board-driver", "2", DateTimeOffset.UnixEpoch,
            new("https://www.asus.com/support/"), null, [uri]);
        return new("driver", "Driver", "Example", "Net", Hardware, "", "", DriverCertification.Unverified)
        { Catalog = new([source], [new(uri, hashes)]) { PackageVersion = "8.9" } };
    }

    private OfficialDriverHttp Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new Handler(respond)), Path.Combine(_workspace.Root, "metadata-cache"));

    private OfficialDriverPackageService Service(OfficialDriverHttp http)
    {
        var inspector = new Mock<IDriverInfInspector>();
        inspector.Setup(boundary => boundary.InspectAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string root, string[] paths, CancellationToken _) => paths.Select(path =>
                new DriverComponent(Path.GetRelativePath(root, path), "Net", "Example",
                path.EndsWith("addon.inf", StringComparison.Ordinal) ? "5.0" : "2.0", "",
                [new(path.EndsWith("addon.inf", StringComparison.Ordinal) ? "SWC\\COMPONENT" : Hardware, DriverIdentifierKind.Hardware, 0)],
                ["Models.NTamd64"], "fixture", "fixture-cat", "Microsoft", true, DriverCertification.Whql)).ToArray());
        return new(http, new(new(Mock.Of<IProcessExecutor>()), inspector.Object), inspector.Object,
            Path.Combine(_workspace.Root, "payload-cache"), Path.Combine(_workspace.Root, "downloads"), _workspace.Root);
    }

    private static byte[] Zip(params (string Entry, string Body)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, body) in files)
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(body));
            }
        return output.ToArray();
    }

    private static HttpResponseMessage Payload(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    public void Dispose() => _workspace.Dispose();

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
