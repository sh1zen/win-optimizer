using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using WinOptimizer.Infrastructure.Tests.Helpers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class AppIconResolverTests : IDisposable
{
    private readonly Mock<IAppxIconSource> _mockIconSource = new();
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IInstalledProgramSource> _programs = new();
    private readonly Mock<IDesktopAppIconSource> _desktop = new();
    private readonly string _tempCacheDir;
    private readonly AppIconResolver _resolver;

    public AppIconResolverTests()
    {
        _tempCacheDir = Path.Combine(Path.GetTempPath(), "WinOptimizerTest_" + Path.GetRandomFileName());

        Directory.CreateDirectory(_tempCacheDir);
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync(Array.Empty<InstalledProgram>());
        _resolver = new AppIconResolver(
            _mockIconSource.Object,
            _mockLog.Object,
            _tempCacheDir, _programs.Object, _desktop.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempCacheDir))
            Directory.Delete(_tempCacheDir, recursive: true);
    }

    private static ItemDefinition Def(
        string id,
        string? appxName = null) => new()
    {
        Id = id,
        Name = $"App {id}",
        Description = $"Description for {id}",
        AppxPackageName = appxName != null ? new[] { appxName } : null,
        IsInstalled = true,
    };

    private static MemoryStream PngBytes(string label) => new(Encoding.UTF8.GetBytes("PNG-" + label));

    private static string Sha1HexLower(string input)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    /// <summary>Mirrors AppIconResolver.BuildCacheFileName for path assertions.</summary>
    private static string BuildCacheFileName(string defId, string sourceKey) =>
        $"{defId}.{Sha1HexLower(sourceKey).Substring(0, 8)}.png";

    // ===== Candidate selection / no-op cases =====

    [Fact]
    public async Task DesktopCatalogueEntryUsesItsRegisteredDisplayIconAndReusesVersionedCache()
    {
        var def = Def("external-app-editor") with { RegistryDisplayName = "Vendor Editor {version}" };
        var icon = new DesktopIconReference(@"C:\Fixture\editor.exe", -7, 100, DateTime.UnixEpoch);
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync(new[]
        {
            new InstalledProgram(WindowsInstalledProgramSource.Roots[0] + @"\Editor", "Vendor Editor 2.0", null, DisplayIcon: @"C:\Fixture\editor.exe,-7")
        });
        _desktop.Setup(source => source.Resolve(@"C:\Fixture\editor.exe,-7")).Returns(icon);
        _desktop.Setup(source => source.GetIconStreamAsync(icon, It.IsAny<CancellationToken>())).ReturnsAsync(() => PngBytes("desktop"));
        await _resolver.ResolveBatchAsync([def]);
        var path = Path.Combine(_tempCacheDir, BuildCacheFileName(def.Id, icon.CacheKey));
        Assert.Equal(path, def.IconPath); Assert.Equal("PNG-desktop", await File.ReadAllTextAsync(path));
        await _resolver.ResolveBatchAsync([def]);
        _desktop.Verify(source => source.GetIconStreamAsync(icon, It.IsAny<CancellationToken>()), Times.Once);
        _mockIconSource.Verify(source => source.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveredDesktopEntryUsesItsExactRegistrationDespiteDuplicateDisplayNames()
    {
        var root = WindowsInstalledProgramSource.Roots[0];
        var chosen = new InstalledProgram(root + @"\Chosen", "Same name", null, DisplayIcon: "chosen.ico");
        var other = new InstalledProgram(root + @"\Other", "Same name", null, DisplayIcon: "other.ico");
        var def = chosen.ToDefinition(); def.IsInstalled = true;
        var icon = new DesktopIconReference(@"C:\Fixture\chosen.ico", 0, 10, DateTime.UnixEpoch);
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync(new[] { other, chosen });
        _desktop.Setup(source => source.Resolve("chosen.ico")).Returns(icon);
        _desktop.Setup(source => source.GetIconStreamAsync(icon, It.IsAny<CancellationToken>())).ReturnsAsync(() => PngBytes("chosen"));
        await _resolver.ResolveBatchAsync([def]);
        Assert.NotNull(def.IconPath);
        _desktop.Verify(source => source.Resolve("other.ico"), Times.Never);
    }

    [Fact]
    public async Task DesktopIconFileUpdateInvalidatesAndPrunesItsOldCache()
    {
        var def = Def("external-app-updated");
        var icon = new DesktopIconReference(@"C:\Fixture\app.exe", 0, 100, DateTime.UnixEpoch);
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync(new[]
        {
            new InstalledProgram(WindowsInstalledProgramSource.Roots[0] + @"\Updated", def.Name, null, DisplayIcon: "app.exe")
        });
        _desktop.Setup(source => source.Resolve("app.exe")).Returns(() => icon);
        _desktop.Setup(source => source.GetIconStreamAsync(It.IsAny<DesktopIconReference>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => PngBytes("updated"));
        await _resolver.ResolveBatchAsync([def]); var old = def.IconPath;
        icon = icon with { LastWriteUtc = icon.LastWriteUtc.AddDays(1) };
        await _resolver.ResolveBatchAsync([def]);
        Assert.NotEqual(old, def.IconPath); Assert.False(File.Exists(old)); Assert.True(File.Exists(def.IconPath));
    }

    [Fact]
    public async Task PackageInventoryFailureDoesNotBlockRegisteredDesktopArtwork()
    {
        var def = Def("external-app-mixed", "Vendor.Package");
        _mockIconSource.Setup(source => source.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Package inventory unavailable"));
        var icon = new DesktopIconReference(@"C:\Fixture\app.ico", 0, 100, DateTime.UnixEpoch);
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync(new[] { new InstalledProgram(WindowsInstalledProgramSource.Roots[0] + @"\Mixed", def.Name, null, DisplayIcon: "app.ico") });
        _desktop.Setup(source => source.Resolve("app.ico")).Returns(icon);
        _desktop.Setup(source => source.GetIconStreamAsync(icon, It.IsAny<CancellationToken>())).ReturnsAsync(() => PngBytes("mixed"));
        await _resolver.ResolveBatchAsync([def]);
        Assert.NotNull(def.IconPath);
    }

    [Fact]
    public async Task MissingOrUnextractableDesktopArtworkRetainsTheCategoryGlyph()
    {
        var def = Def("external-app-missing");
        var icon = new DesktopIconReference(@"C:\Fixture\no-resources.exe", 0, 100, DateTime.UnixEpoch);
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync(new[] { new InstalledProgram(WindowsInstalledProgramSource.Roots[0] + @"\Missing", def.Name, null, DisplayIcon: "missing.exe") });
        _desktop.Setup(source => source.Resolve("missing.exe")).Returns(icon);
        _desktop.Setup(source => source.GetIconStreamAsync(icon, It.IsAny<CancellationToken>())).ReturnsAsync((Stream?)null);
        await _resolver.ResolveBatchAsync([def]);
        Assert.Null(def.IconPath); Assert.Empty(Directory.GetFiles(_tempCacheDir));
    }

    [Fact]
    public async Task ResolveBatchAsync_SkipsDefinitionWithNoRoutableIdentity()
    {
        // Definitions without an AppX identity retain their category glyph.
        var def = Def("misc1", appxName: null);
        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        await _resolver.ResolveBatchAsync(new[] { def });

        def.IconPath.Should().BeNull();
        _mockIconSource.Verify(
            s => s.GetLogoStreamAsync(It.IsAny<string>(), It.IsAny<Size>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveBatchAsync_EmptyInput_ReturnsWithoutCallingSource()
    {
        await _resolver.ResolveBatchAsync(Array.Empty<ItemDefinition>());

        _mockIconSource.Verify(
            s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("external-app-editor")]
    [InlineData("capability-terminal")]
    [InlineData("feature-components")]
    public async Task ResolveBatchAsync_WithoutAppxIdentity_KeepsGlyphDespiteCachedArtwork(string id)
    {
        var def = Def(id);
        await File.WriteAllTextAsync(Path.Combine(_tempCacheDir, id + ".12345678.png"), "old artwork");

        await _resolver.ResolveBatchAsync(new[] { def });

        def.IconPath.Should().BeNull();
        _mockIconSource.Verify(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveBatchAsync_PackageNotInstalled_KeepsGlyphDespiteCachedArtwork()
    {
        var def = Def("windows-app-editor", appxName: "Vendor.Editor");
        await File.WriteAllTextAsync(Path.Combine(_tempCacheDir, def.Id + ".12345678.png"), "old artwork");
        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        await _resolver.ResolveBatchAsync(new[] { def });

        def.IconPath.Should().BeNull();
        _mockIconSource.Verify(
            s => s.GetLogoStreamAsync(It.IsAny<string>(), It.IsAny<Size>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveBatchAsync_OuterException_LogsErrorAndReturnsWithoutThrowing()
    {
        var def = Def("windows-app-x", appxName: "Microsoft.App1");
        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PackageManager unavailable"));

        var act = async () => await _resolver.ResolveBatchAsync(new[] { def });

        await act.Should().NotThrowAsync();
        def.IconPath.Should().BeNull();
        _mockLog.Verify(l => l.LogError(It.IsAny<string>(), It.IsAny<Exception>()), Times.AtLeastOnce);
    }

    // ===== Installed AppX extraction =====

    [Fact]
    public async Task ResolveBatchAsync_WindowsApp_PackageInInstalledMap_ExtractsViaAppx()
    {
        var def = Def("windows-app-app1", appxName: "Microsoft.App1");
        var fullName = "Microsoft.App1_1.0.0_x64__abc";

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Microsoft.App1"] = fullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(fullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PngBytes("app1"));

        await _resolver.ResolveBatchAsync(new[] { def });

        var expectedPath = Path.Combine(_tempCacheDir, BuildCacheFileName(def.Id, fullName));
        def.IconPath.Should().Be(expectedPath);
        File.Exists(expectedPath).Should().BeTrue();
        File.ReadAllText(expectedPath).Should().Be("PNG-app1");

    }

    [Fact]
    public async Task ResolveBatchAsync_AppxCacheHit_DoesNotCallGetLogoStreamButStampsIconPath()
    {
        var def = Def("windows-app-app1", appxName: "Microsoft.App1");
        var fullName = "Microsoft.App1_1.0.0_x64__abc";
        var cachePath = Path.Combine(_tempCacheDir, BuildCacheFileName(def.Id, fullName));

        Directory.CreateDirectory(_tempCacheDir);
        File.WriteAllBytes(cachePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Microsoft.App1"] = fullName });

        await _resolver.ResolveBatchAsync(new[] { def });

        def.IconPath.Should().Be(cachePath);
        _mockIconSource.Verify(
            s => s.GetLogoStreamAsync(It.IsAny<string>(), It.IsAny<Size>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveBatchAsync_AppxCacheMiss_PrunesOldVersionFiles()
    {
        var def = Def("windows-app-app1", appxName: "Microsoft.App1");
        var oldFullName = "Microsoft.App1_1.0.0_x64__abc";
        var newFullName = "Microsoft.App1_2.0.0_x64__abc";

        Directory.CreateDirectory(_tempCacheDir);
        var oldPath = Path.Combine(_tempCacheDir, BuildCacheFileName(def.Id, oldFullName));
        File.WriteAllText(oldPath, "old version bytes");

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Microsoft.App1"] = newFullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(newFullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PngBytes("v2"));

        await _resolver.ResolveBatchAsync(new[] { def });

        var newPath = Path.Combine(_tempCacheDir, BuildCacheFileName(def.Id, newFullName));
        File.Exists(newPath).Should().BeTrue();
        File.Exists(oldPath).Should().BeFalse();
        def.IconPath.Should().Be(newPath);
    }

    [Fact]
    public async Task ResolveBatchAsync_PerPackageException_LogsWarningAndContinues()
    {
        var def1 = Def("windows-app-app1", appxName: "Microsoft.App1");
        var def2 = Def("windows-app-app2", appxName: "Microsoft.App2");

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                ["Microsoft.App1"] = "Microsoft.App1_1.0.0_x64__abc",
                ["Microsoft.App2"] = "Microsoft.App2_1.0.0_x64__abc",
            });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync("Microsoft.App1_1.0.0_x64__abc", It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        _mockIconSource.Setup(s => s.GetLogoStreamAsync("Microsoft.App2_1.0.0_x64__abc", It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PngBytes("app2"));

        await _resolver.ResolveBatchAsync(new[] { def1, def2 });

        def1.IconPath.Should().BeNull();
        def2.IconPath.Should().NotBeNull();
        _mockLog.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("app1") || s.Contains("App1"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ResolveBatchAsync_MultipleAppxNames_UsesFirstMatchInInstalledMap()
    {
        // Mirrors the Xbox case: a definition declares BOTH a modern and a
        // legacy AppX identity. The installed map only carries one of them.
        var def = new ItemDefinition
        {
            Id = "windows-app-xbox",
            Name = "Xbox",
            Description = "Game library",
            AppxPackageName = new[] { "Microsoft.GamingApp", "Microsoft.XboxApp" },
            IsInstalled = true,
        };
        var legacyFullName = "Microsoft.XboxApp_48.86.5.0_x64__8wekyb3d8bbwe";

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Microsoft.XboxApp"] = legacyFullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(legacyFullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PngBytes("xbox-legacy"));

        await _resolver.ResolveBatchAsync(new[] { def });

        def.IconPath.Should().NotBeNull();
        def.IconPath!.Should().Contain("windows-app-xbox.");
        _mockIconSource.Verify(
            s => s.GetLogoStreamAsync(legacyFullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ===== Light/dark variant generation (theme adaptation) =====

    [Fact]
    public async Task ResolveBatchAsync_WhiteAppxIcon_WritesLightVariantSibling()
    {
        var def = Def("windows-app-white", appxName: "Vendor.WhiteApp");
        var fullName = "Vendor.WhiteApp_1.0.0_x64__abc";
        var whitePngBytes = await PngTestHelper.MakeSolidPngAsync(16, 16, 0xFF, 0xFF, 0xFF);

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Vendor.WhiteApp"] = fullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(fullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(whitePngBytes));

        await _resolver.ResolveBatchAsync(new[] { def }, applyThemeAdaptation: true);

        def.IconPath.Should().NotBeNull();
        var lightPath = Path.ChangeExtension(def.IconPath!, null) + ".light.png";
        File.Exists(lightPath).Should().BeTrue("a monochrome-white primary must produce a .light.png sibling");

        (await SampleFirstPixelAsync(lightPath)).Should().Be(((byte)0x1F, (byte)0x1F, (byte)0x1F, (byte)0xFF));
    }

    [Fact]
    public async Task ResolveBatchAsync_DarkGreyAppxIcon_WritesBothLightAndDarkVariants()
    {
        var def = Def("windows-app-gamebar", appxName: "Vendor.XboxGameBarLike");
        var fullName = "Vendor.XboxGameBarLike_1.0.0_x64__abc";
        var greyPngBytes = await PngTestHelper.MakeSolidPngAsync(16, 16, 0x33, 0x33, 0x33);

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Vendor.XboxGameBarLike"] = fullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(fullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(greyPngBytes));

        await _resolver.ResolveBatchAsync(new[] { def }, applyThemeAdaptation: true);

        def.IconPath.Should().NotBeNull();
        var stem = Path.ChangeExtension(def.IconPath!, null);
        File.Exists(stem + ".light.png").Should().BeTrue("mono-dark icons need a light-mode variant");
        File.Exists(stem + ".dark.png").Should().BeTrue("mono-dark icons need a dark-mode variant");

        (await SampleFirstPixelAsync(stem + ".light.png")).Should().Be(((byte)0x1F, (byte)0x1F, (byte)0x1F, (byte)0xFF));
        (await SampleFirstPixelAsync(stem + ".dark.png")).Should().Be(((byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF));
    }

    [Fact]
    public async Task ResolveBatchAsync_IconWithUniformBackplate_TrimsBackplateBeforeCaching()
    {
        var def = Def("windows-app-stickynotes", appxName: "Vendor.StickyNotesLike");
        var fullName = "Vendor.StickyNotesLike_1.0.0_x64__abc";
        var input = await PngTestHelper.MakePngAsync(20, 20, (x, y) =>
        {
            bool isYellowShape = x >= 8 && x < 16 && y >= 8 && y < 12;
            return isYellowShape
                ? ((byte)0x00, (byte)0xE0, (byte)0xE0, (byte)0xFF)   // BGRA: vivid yellow
                : ((byte)0x40, (byte)0x40, (byte)0x40, (byte)0xFF);  // dark-grey backplate
        });

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Vendor.StickyNotesLike"] = fullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(fullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(input));

        await _resolver.ResolveBatchAsync(new[] { def }, applyThemeAdaptation: true);

        def.IconPath.Should().NotBeNull();
        var decoder = await DecodeAsync(def.IconPath!);
        decoder.PixelWidth.Should().Be(8, "backplate trim should crop to the inner yellow rectangle's width");
        decoder.PixelHeight.Should().Be(4, "backplate trim should crop to the inner yellow rectangle's height");
    }

    [Fact]
    public async Task ResolveBatchAsync_TransparentBorderedIcon_StillTrimsByAlphaUnchanged()
    {
        var def = Def("windows-app-transparent", appxName: "Vendor.TransparentBorder");
        var fullName = "Vendor.TransparentBorder_1.0.0_x64__abc";
        var input = await PngTestHelper.MakePngAsync(20, 20, (x, y) =>
        {
            bool isOpaqueWhite = x >= 6 && x < 14 && y >= 6 && y < 14;
            return isOpaqueWhite
                ? ((byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF)
                : ((byte)0x00, (byte)0x00, (byte)0x00, (byte)0x00);  // fully transparent
        });

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Vendor.TransparentBorder"] = fullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(fullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(input));

        await _resolver.ResolveBatchAsync(new[] { def }, applyThemeAdaptation: true);

        def.IconPath.Should().NotBeNull();
        var decoder = await DecodeAsync(def.IconPath!);
        decoder.PixelWidth.Should().Be(8);
        decoder.PixelHeight.Should().Be(8);
    }

    [Fact]
    public async Task ResolveBatchAsync_ThemeAdaptationDisabled_DoesNotWriteVariants()
    {
        // External Apps path: applyThemeAdaptation=false. A monochrome-white icon
        // that WOULD normally produce a .light.png must not.
        var def = Def("windows-app-whitelogo", appxName: "Vendor.WhiteLogo");
        var fullName = "Vendor.WhiteLogo_1.0.0_x64__abc";
        var whitePngBytes = await PngTestHelper.MakeSolidPngAsync(16, 16, 0xFF, 0xFF, 0xFF);

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Vendor.WhiteLogo"] = fullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(fullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(whitePngBytes));

        await _resolver.ResolveBatchAsync(new[] { def }, applyThemeAdaptation: false);

        def.IconPath.Should().NotBeNull();
        var stem = Path.ChangeExtension(def.IconPath!, null);
        File.Exists(stem + ".light.png").Should().BeFalse("theme adaptation off → no .light.png");
        File.Exists(stem + ".dark.png").Should().BeFalse("theme adaptation off → no .dark.png");
    }

    [Fact]
    public async Task ResolveBatchAsync_ThemeAdaptationDisabled_DoesNotCropBackplate()
    {
        var def = Def("windows-app-framed", appxName: "Vendor.FramedLogo");
        var fullName = "Vendor.FramedLogo_1.0.0_x64__abc";
        var input = await PngTestHelper.MakePngAsync(20, 20, (x, y) =>
        {
            bool isYellowShape = x >= 8 && x < 16 && y >= 8 && y < 12;
            return isYellowShape
                ? ((byte)0x00, (byte)0xE0, (byte)0xE0, (byte)0xFF)
                : ((byte)0x40, (byte)0x40, (byte)0x40, (byte)0xFF);
        });

        _mockIconSource.Setup(s => s.GetInstalledPackageMapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["Vendor.FramedLogo"] = fullName });
        _mockIconSource.Setup(s => s.GetLogoStreamAsync(fullName, It.IsAny<Size>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(input));

        await _resolver.ResolveBatchAsync(new[] { def }, applyThemeAdaptation: false);

        def.IconPath.Should().NotBeNull();
        var decoder = await DecodeAsync(def.IconPath!);
        decoder.PixelWidth.Should().Be(20, "theme adaptation off → backplate is not cropped");
        decoder.PixelHeight.Should().Be(20, "theme adaptation off → backplate is not cropped");
    }

    // ===== Helpers =====

    private static async Task<BitmapDecoder> DecodeAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return await BitmapDecoder.CreateAsync(stream);
    }

    private static async Task<(byte R, byte G, byte B, byte A)> SampleFirstPixelAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var sw = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
        var buffer = new Windows.Storage.Streams.Buffer((uint)(sw.PixelWidth * sw.PixelHeight * 4));
        sw.CopyToBuffer(buffer);
        var pixels = buffer.ToArray();
        return (pixels[2], pixels[1], pixels[0], pixels[3]);
    }
}
