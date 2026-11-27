using System.Globalization;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class LocalizationServiceTests
{
    private readonly Mock<IFileSystemService> _mockFileSystem = new();
    private readonly LocalizationService _sut;

    public LocalizationServiceTests()
    {
        // Set up a minimal file system mock so the constructor doesn't fail:
        // - CombinePath returns concatenated path
        // - No language files exist, so it falls back to empty dictionaries
        _mockFileSystem
            .Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] parts) => string.Join("\\", parts));
        _mockFileSystem
            .Setup(f => f.DirectoryExists(It.IsAny<string>()))
            .Returns(false);
        _mockFileSystem
            .Setup(f => f.FileExists(It.IsAny<string>()))
            .Returns(false);

        _sut = new LocalizationService(_mockFileSystem.Object);
    }

    // ── GetString(key) ──

    [Fact]
    public void GetString_UnknownKey_ReturnsBracketedKey()
    {
        var result = _sut.GetString("NonExistentKey");

        result.Should().Be("[NonExistentKey]");
    }

    [Fact]
    public void GetString_WithFallbackAvailable_ReturnsFallbackValue()
    {
        // Create a service with a mock that provides an English fallback file
        var mockFs = new Mock<IFileSystemService>();
        mockFs.Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] parts) => string.Join("\\", parts));
        mockFs.Setup(f => f.DirectoryExists(It.IsAny<string>()))
            .Returns(true);
        mockFs.Setup(f => f.GetFiles(It.IsAny<string>(), "*.json"))
            .Returns(new[] { "en.json" });
        mockFs.Setup(f => f.GetFileNameWithoutExtension(It.Is<string>(s => s.Contains("en"))))
            .Returns("en");
        mockFs.Setup(f => f.FileExists(It.Is<string>(s => s.Contains("en.json"))))
            .Returns(true);
        mockFs.Setup(f => f.ReadAllText(It.Is<string>(s => s.Contains("en.json"))))
            .Returns("{\"Greeting\": \"Hello\"}");

        var sut = new LocalizationService(mockFs.Object);

        var result = sut.GetString("Greeting");

        result.Should().Be("Hello");
    }

    // ── GetString(key, args) ──

    [Fact]
    public void GetString_FormatsArgumentsAndPreservesTheTemplateWhenItCannotFormat()
    {
        var mockFs = new Mock<IFileSystemService>();
        mockFs.Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] parts) => string.Join("\\", parts));
        mockFs.Setup(f => f.DirectoryExists(It.IsAny<string>()))
            .Returns(true);
        mockFs.Setup(f => f.GetFiles(It.IsAny<string>(), "*.json"))
            .Returns(new[] { "en.json" });
        mockFs.Setup(f => f.GetFileNameWithoutExtension(It.Is<string>(s => s.Contains("en"))))
            .Returns("en");
        mockFs.Setup(f => f.FileExists(It.Is<string>(s => s.Contains("en.json"))))
            .Returns(true);
        mockFs.Setup(f => f.ReadAllText(It.Is<string>(s => s.Contains("en.json"))))
            .Returns("{\"Welcome\": \"Hello, {0}!\"}");

        var sut = new LocalizationService(mockFs.Object);

        sut.GetString("Welcome", "World").Should().Be("Hello, World!");
        sut.GetString("Welcome").Should().Be("Hello, {0}!");
        sut.GetString("Welcome", Array.Empty<object>()).Should().Be("Hello, {0}!");
    }

    // ── SetLanguage ──

    [Fact]
    public void ReadTranslations_UsesEveryInstalledLanguageAndPreservesTheCurrentLanguage()
    {
        var files = new Dictionary<string, string>
        {
            ["en.json"] = "{\"Repair\":\"Repair\",\"Other\":\"unrequested\"}",
            ["es.json"] = "{\"Repair\":\"Reparación\"}",
            ["it.json"] = "{\"Repair\":\"Ripara\"}",
            ["ja.json"] = "{\"Repair\":\"修復\"}",
            ["pt.json"] = "{\"Repair\":\"  \"}",
            ["ru.json"] = "{\"Repair\":\"Repair\"}"
        };
        _mockFileSystem.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(f => f.GetFiles(It.IsAny<string>(), "*.json")).Returns(files.Keys.ToArray());
        _mockFileSystem.Setup(f => f.GetFileNameWithoutExtension(It.IsAny<string>())).Returns((string file) => Path.GetFileNameWithoutExtension(file));
        _mockFileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns((string file) => files.ContainsKey(Path.GetFileName(file)));
        _mockFileSystem.Setup(f => f.ReadAllText(It.IsAny<string>())).Returns((string file) => files[Path.GetFileName(file)]);
        var localization = new LocalizationService(_mockFileSystem.Object);
        var originalLanguage = localization.CurrentLanguage;
        var originalText = localization.GetString("Repair");
        var languageChanged = false;
        localization.LanguageChanged += (_, _) => languageChanged = true;

        var translations = localization.ReadTranslations(new HashSet<string> { "Repair", "Missing" });

        Assert.Equal(["Repair"], translations.Keys);
        Assert.Equivalent(new[] { "Repair", "Reparación", "Ripara", "修復" }, translations["Repair"], strict: true);
        Assert.Equal(originalLanguage, localization.CurrentLanguage);
        Assert.Equal(originalText, localization.GetString("Repair"));
        Assert.False(languageChanged);
    }

    [Fact]
    public void SetLanguage_ValidLanguageCode_UpdatesStateAndNotifies()
    {
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        var eventRaised = false;
        _sut.LanguageChanged += (_, _) => eventRaised = true;

        try
        {
            _sut.SetLanguage("it").Should().BeTrue();
            _sut.CurrentLanguage.Should().Be("it");
            _sut.IsRightToLeft.Should().BeFalse();
            eventRaised.Should().BeTrue();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = previousDefaultCulture;
        }
    }
}
