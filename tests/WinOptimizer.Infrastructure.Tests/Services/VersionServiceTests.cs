using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using Moq.Protected;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class VersionServiceTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<HttpMessageHandler> _mockHttpHandler = new();
    private readonly HttpClient _httpClient;
    private readonly VersionService _service;

    public VersionServiceTests()
    {
        _httpClient = new HttpClient(_mockHttpHandler.Object);
        _service = new VersionService(
            _mockLogService.Object,
            _mockProcessExecutor.Object,
            _mockFileSystemService.Object,
            _httpClient);
    }

    #region GetCurrentVersion

    [Fact]
    public void GetCurrentVersion_ReturnsNonNullVersionInfo()
    {
        // Act
        var result = _service.GetCurrentVersion();

        // Assert — the method always returns a non-null VersionInfo, even in a test runner
        // context where the assembly version may not be a valid date-based tag.
        // In such cases VersionInfo.FromTag may return a default record with Version = "".
        result.Should().NotBeNull();
    }

    [Fact]
    public void GetCurrentVersion_CalledTwice_ReturnsSameResult()
    {
        // Act — calling multiple times should be deterministic
        var first = _service.GetCurrentVersion();
        var second = _service.GetCurrentVersion();

        // Assert
        first.Version.Should().Be(second.Version);
        first.ReleaseDate.Should().Be(second.ReleaseDate);
    }

    #endregion

    #region CheckForUpdateAsync

    [Fact]
    public async Task CheckForUpdateAsync_NewerVersionAvailable_ReturnsUpdateAvailable()
    {
        // Arrange — simulate a GitHub API response with a very new version
        var releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v99.12.31",
            html_url = "https://github.com/sh1zen/win-optimizer/releases/tag/v99.12.31",
            published_at = "2099-12-31T00:00:00Z",
            prerelease = false,
            draft = false
        });

        SetupHttpResponse(HttpStatusCode.OK, releaseJson);

        // Act
        var result = await _service.CheckForUpdateAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.IsUpdateAvailable.Should().BeTrue();
        result.Version.Should().Be("v99.12.31");
    }

    [Fact]
    public async Task CheckForUpdateAsync_SameOrOlderVersion_ReturnsNoUpdate()
    {
        // Arrange — In the test runner, GetCurrentVersion() returns a VersionInfo with
        // ReleaseDate = DateTime.MinValue (because the assembly version "0.0.0" doesn't
        // parse into a valid date). A version whose ReleaseDate is also DateTime.MinValue
        // (or any invalid date tag) will NOT be "newer", so IsUpdateAvailable = false.
        // We use an invalid tag format that VersionInfo.FromTag will reject, yielding default dates.
        var releaseJson = JsonSerializer.Serialize(new
        {
            tag_name = "v0.0.0",
            html_url = "https://github.com/sh1zen/win-optimizer/releases/tag/v0.0.0",
            published_at = "2000-01-01T00:00:00Z"
        });

        SetupHttpResponse(HttpStatusCode.OK, releaseJson);

        // Act
        var result = await _service.CheckForUpdateAsync(CancellationToken.None);

        // Assert — v0.0.0 has month=0 which is an invalid DateTime, so FromTag returns
        // a default VersionInfo. Both current and latest have the same (default) ReleaseDate,
        // so IsNewerThan returns false, and IsUpdateAvailable is false.
        result.Should().NotBeNull();
        result.IsUpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdateAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The handler will throw OperationCanceledException when the token is already cancelled
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CheckForUpdateAsync(cts.Token));
    }

    [Fact]
    public async Task CheckForUpdateAsync_HttpError_ReturnsNoUpdate()
    {
        // Arrange — simulate a non-transient HTTP error (404)
        SetupHttpResponse(HttpStatusCode.NotFound, "Not Found");

        // Act
        var result = await _service.CheckForUpdateAsync(CancellationToken.None);

        // Assert — on non-retryable error, service returns a default VersionInfo with no update
        result.Should().NotBeNull();
        result.IsUpdateAvailable.Should().BeFalse();
    }

    [Theory]
    [InlineData("v99.06.15-beta", false, false)]
    [InlineData("v99.06.15-rc.1", false, false)]
    [InlineData("v99.06.15-preview", false, false)]
    [InlineData("v99.06.15", true, false)]
    [InlineData("v99.06.15", false, true)]
    public async Task CheckForUpdateAsync_UnsupportedRelease_IsNotOffered(string tag, bool prerelease, bool draft)
    {
        SetupHttpResponse(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            tag_name = tag,
            html_url = "https://github.com/sh1zen/win-optimizer/releases",
            published_at = "2099-06-15T00:00:00Z",
            prerelease,
            draft
        }));

        var result = await _service.CheckForUpdateAsync(CancellationToken.None);

        result.Version.Should().BeEmpty();
        result.IsUpdateAvailable.Should().BeFalse();
    }

    #endregion

    #region DownloadAndInstallUpdateAsync

    [Fact]
    public async Task DownloadAndInstallUpdateAsync_StagesInstallerInApplicationDownloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinOptimizerStorageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "win-optimizer.Installer.exe");
        _mockFileSystemService.Setup(files => files.CombinePath(It.IsAny<string[]>())).Returns(path);
        byte[] payload = [0x4D, 0x5A];
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        try
        {
            await _service.DownloadAndInstallUpdateAsync(CancellationToken.None);
            (await File.ReadAllBytesAsync(path)).Should().Equal(payload);
            _mockFileSystemService.Verify(files => files.CreateDirectory(
                WinOptimizer.Core.Features.Common.Constants.AppDataPaths.Downloads), Times.Once);
            _mockFileSystemService.Verify(files => files.CombinePath(It.Is<string[]>(parts =>
                parts.Length == 2 && parts[0] == WinOptimizer.Core.Features.Common.Constants.AppDataPaths.Downloads &&
                parts[1] == "win-optimizer.Installer.exe")), Times.Once);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    #endregion

    #region BuildInstallerArgs (issue #649 — silent-mode install location)

    [Theory]
    [InlineData(@"D:\win-optimizer", false)]
    [InlineData(@"D:\win-optimizer\", false)]
    [InlineData(@"C:\Program Files\win-optimizer", false)]
    [InlineData(@"D:\My Portable\win-optimizer", true)]
    [InlineData(@"D:\My Portable\win-optimizer\", true)]
    public void BuildInstallerArgs_AlwaysIncludesDirArgPinnedToAppDir(string appDir, bool isPortable)
    {
        // Act
        var args = VersionService.BuildInstallerArgs(appDir, isPortable);

        // Assert — /DIR= must be present, quoted, and equal to the appDir
        // with any trailing path separator stripped. See issue #649: without
        // /DIR=, Inno's silent-install resolution lands {app} at Program Files
        // (regular) or ~\Desktop\win-optimizer (portable) regardless of where the
        // running app actually lives.
        var expectedDirArg = $"/DIR=\"{appDir.TrimEnd('\\', '/')}\"";
        args.Should().Contain(expectedDirArg);
        args.Should().Contain("/SILENT");
        args.Should().Contain("/SUPPRESSMSGBOXES");
    }

    [Fact]
    public void BuildInstallerArgs_Portable_SelectsPortableInstallTaskOnly()
    {
        // Act
        var args = VersionService.BuildInstallerArgs(@"D:\Portable\win-optimizer", isPortable: true);

        // Assert
        args.Should().Contain(@"/MERGETASKS=""portableinstall""");
        args.Should().NotContain("regularinstall");
    }

    [Fact]
    public void BuildInstallerArgs_Regular_SelectsRegularInstallTaskWithShortcuts()
    {
        // Act
        var args = VersionService.BuildInstallerArgs(@"C:\Program Files\win-optimizer", isPortable: false);

        // Assert
        args.Should().Contain(@"/MERGETASKS=""regularinstall\desktopicon,regularinstall\startmenuicon""");
        args.Should().NotContain("portableinstall");
    }

    [Fact]
    public void BuildInstallerArgs_PathWithSpaces_QuotesDirArgCorrectly()
    {
        // Arrange — the actual scenario from issue #649's reporter
        var customPath = @"D:\Windows Tweaks\win-optimizer";

        // Act
        var args = VersionService.BuildInstallerArgs(customPath, isPortable: false);

        // Assert — the double-quotes around /DIR= must survive so Inno parses
        // the path with its embedded space correctly
        args.Should().Contain($"/DIR=\"{customPath}\"");
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NullProcessExecutor_ThrowsArgumentNullException()
    {
        var act = () => new VersionService(
            _mockLogService.Object, null!, _mockFileSystemService.Object, _httpClient);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("processExecutor");
    }

    [Fact]
    public void Constructor_NullFileSystemService_ThrowsArgumentNullException()
    {
        var act = () => new VersionService(
            _mockLogService.Object, _mockProcessExecutor.Object, null!, _httpClient);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("fileSystemService");
    }

    [Fact]
    public void Constructor_NullHttpClient_ThrowsArgumentNullException()
    {
        var act = () => new VersionService(
            _mockLogService.Object, _mockProcessExecutor.Object, _mockFileSystemService.Object, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("httpClient");
    }

    #endregion

    #region Helpers

    private void SetupHttpResponse(HttpStatusCode statusCode, string content)
    {
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
    }

    #endregion
}
