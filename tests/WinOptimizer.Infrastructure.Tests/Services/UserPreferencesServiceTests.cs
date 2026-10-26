using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class UserPreferencesServiceTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly UserPreferencesService _service;

    public UserPreferencesServiceTests()
    {
        _mockInteractiveUserService.Setup(s => s.InteractiveUserSid).Returns("S-1-5-21-111-222-333-1001");
        _mockFileSystemService
            .Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] parts) => string.Join(@"\", parts));

        _mockFileSystemService
            .Setup(f => f.DirectoryExists(It.IsAny<string>()))
            .Returns(true);

        _mockFileSystemService
            .Setup(f => f.GetDirectoryName(It.IsAny<string>()))
            .Returns((string path) =>
            {
                int lastSep = path.LastIndexOf('\\');
                return lastSep > 0 ? path.Substring(0, lastSep) : null;
            });

        _service = new UserPreferencesService(
            _mockLogService.Object,
            _mockInteractiveUserService.Object,
            _mockFileSystemService.Object);
    }

    #region GetPreferenceAsync

    [Fact]
    public async Task PlacementPatchAndConcurrentPreferenceWritePreserveUnrelatedValues()
    {
        var json = "{\"Theme\":\"Dark\",\"WindowWidth\":1000}";
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        _mockFileSystemService.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(fs => fs.ReadAllTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(json));
        _mockFileSystemService.Setup(fs => fs.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string content, CancellationToken _) =>
            {
                if (++writes == 1)
                {
                    firstWrite.SetResult();
                    await releaseWrite.Task;
                }
                json = content;
            });

        var language = _service.SetPreferenceAsync("Language", "it");
        await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var placement = _service.UpdatePreferencesAsync(new Dictionary<string, object> { ["WindowWidth"] = 1400, ["WindowHeight"] = 900 });
        Assert.False(placement.IsCompleted);
        releaseWrite.SetResult();
        var results = await Task.WhenAll(language, placement);

        Assert.All(results, result => Assert.True(result.Success, result.ErrorMessage));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Dark", document.RootElement.GetProperty("Theme").GetString());
        Assert.Equal("it", document.RootElement.GetProperty("Language").GetString());
        Assert.Equal(1400, document.RootElement.GetProperty("WindowWidth").GetInt32());
        Assert.Equal(900, document.RootElement.GetProperty("WindowHeight").GetInt32());
    }

    [Fact]
    public async Task GetPreferenceAsync_KeyMissing_ReturnsDefaultValue()
    {
        // Arrange — file does not exist, so GetPreferencesAsync returns empty dict
        _mockFileSystemService
            .Setup(f => f.FileExists(It.IsAny<string>()))
            .Returns(false);

        // Act
        var result = await _service.GetPreferenceAsync("NonExistentKey", "default_value");

        // Assert
        result.Should().Be("default_value");
    }

    [Fact]
    public async Task GetPreferenceAsync_KeyExists_ReturnsStoredValue()
    {
        // Arrange
        var prefs = new Dictionary<string, object> { { "Theme", "Dark" } };
        string json = JsonSerializer.Serialize(prefs);

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), default))
            .ReturnsAsync(json);

        // Act
        var result = await _service.GetPreferenceAsync("Theme", "Light");

        // Assert — STJ deserializes string values as JsonElement, the conversion logic handles it
        result.Should().Be("Dark");
    }

    [Fact]
    public async Task GetPreferenceAsync_BoolKey_ReturnsBoolValue()
    {
        // Arrange
        var prefs = new Dictionary<string, object> { { "AutoUpdate", true } };
        string json = JsonSerializer.Serialize(prefs);

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), default))
            .ReturnsAsync(json);

        // Act
        var result = await _service.GetPreferenceAsync("AutoUpdate", false);

        // Assert
        result.Should().BeTrue();
    }

    [Theory]
    [InlineData("true", false, true)]
    [InlineData("false", true, false)]
    [InlineData("\"true\"", false, false)]
    [InlineData("\"false\"", true, true)]
    [InlineData("\"1\"", false, false)]
    [InlineData("\"0\"", true, true)]
    [InlineData("1", false, false)]
    [InlineData("0", true, true)]
    public async Task PreferenceReadersRequireTheStoredBooleanType(string storedJson, bool defaultValue, bool expected)
    {
        var json = "{\"Flag\":" + storedJson + "}";
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), default)).ReturnsAsync(json);
        _mockFileSystemService.Setup(f => f.ReadAllText(It.IsAny<string>())).Returns(json);

        Assert.Equal(expected, _service.GetPreference("Flag", defaultValue));
        Assert.Equal(expected, await _service.GetPreferenceAsync("Flag", defaultValue));
        if (storedJson is not ("true" or "false"))
            _mockLogService.Verify(log => log.Log(WinOptimizer.Core.Features.Common.Enums.LogLevel.Error,
                It.Is<string>(message => message.Contains("Flag")), null), Times.Exactly(2));
    }

    [Fact]
    public async Task GetPreferenceAsync_EmptyFile_ReturnsDefaultValue()
    {
        // Arrange
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), default))
            .ReturnsAsync(string.Empty);

        // Act
        var result = await _service.GetPreferenceAsync("AnyKey", 42);

        // Assert
        result.Should().Be(42);
    }

    #endregion

    #region SetPreferenceAsync

    [Fact]
    public async Task SetPreferenceAsync_StoresValue_AndSavesToFile()
    {
        // Arrange — start with empty preferences (file does not exist)
        _mockFileSystemService
            .Setup(f => f.FileExists(It.IsAny<string>()))
            .Returns(false);

        // After writing, file "exists"
        string? writtenContent = null;
        _mockFileSystemService
            .Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .Callback<string, string, System.Threading.CancellationToken>((_, content, _) =>
            {
                writtenContent = content;
                _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
            })
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.SetPreferenceAsync("Theme", "Dark");

        // Assert
        result.Success.Should().BeTrue();
        writtenContent.Should().NotBeNull();
        writtenContent.Should().Contain("Theme");
        writtenContent.Should().Contain("Dark");
    }

    [Fact]
    public async Task SetPreferenceAsync_UpdatesExistingKey()
    {
        // Arrange — file has an existing preference
        var existingPrefs = new Dictionary<string, object> { { "Theme", "Light" } };
        string existingJson = JsonSerializer.Serialize(existingPrefs);

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), default))
            .ReturnsAsync(existingJson);

        string? writtenContent = null;
        _mockFileSystemService
            .Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .Callback<string, string, System.Threading.CancellationToken>((_, content, _) =>
                writtenContent = content)
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.SetPreferenceAsync("Theme", "Dark");

        // Assert
        result.Success.Should().BeTrue();
        writtenContent.Should().NotBeNull();
        writtenContent.Should().Contain("Dark");
        writtenContent.Should().NotContain("\"Light\"");
    }

    #endregion

    #region GetPreferencesAsync

    [Fact]
    public async Task GetPreferencesAsync_FileDoesNotExist_ReturnsEmptyDictionary()
    {
        // Arrange
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _service.GetPreferencesAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPreferencesAsync_FileExists_ReturnsDeserializedPreferences()
    {
        // Arrange
        var prefs = new Dictionary<string, object>
        {
            { "Theme", "Dark" },
            { "FontSize", 14 },
            { "AutoUpdate", true }
        };
        string json = JsonSerializer.Serialize(prefs);

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), default))
            .ReturnsAsync(json);

        // Act
        var result = await _service.GetPreferencesAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(3);
        result.Should().ContainKey("Theme");
        result.Should().ContainKey("FontSize");
        result.Should().ContainKey("AutoUpdate");
    }

    [Fact]
    public async Task GetPreferencesAsync_CorruptJson_ReturnsEmptyDictionary()
    {
        // Arrange
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(It.IsAny<string>(), default))
            .ReturnsAsync("{ this is not valid json }}}");

        // Act
        var result = await _service.GetPreferencesAsync();

        // Assert — the service catches JsonException and returns empty
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    #endregion

    #region SavePreferencesAsync

    [Fact]
    public async Task SavePreferencesAsync_WritesToFile_ReturnsSuccess()
    {
        // Arrange
        var prefs = new Dictionary<string, object>
        {
            { "Theme", "Dark" },
            { "AutoUpdate", true }
        };

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService
            .Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.SavePreferencesAsync(prefs);

        // Assert
        result.Success.Should().BeTrue();
        _mockFileSystemService.Verify(
            f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default),
            Times.Once);
    }

    [Fact]
    public async Task SavePreferencesAsync_FileNotFoundAfterWrite_ReturnsFailure()
    {
        // Arrange — simulate a write that succeeds but the file doesn't exist afterward
        var prefs = new Dictionary<string, object> { { "Key", "Value" } };

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _mockFileSystemService
            .Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.SavePreferencesAsync(prefs);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("File not found after writing");
    }

    [Fact]
    public async Task SavePreferencesAsync_WriteThrows_ReturnsFailure()
    {
        // Arrange
        var prefs = new Dictionary<string, object> { { "Key", "Value" } };

        _mockFileSystemService
            .Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ThrowsAsync(new System.IO.IOException("Disk full"));

        // Act
        var result = await _service.SavePreferencesAsync(prefs);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Disk full");
    }

    #endregion

    #region GetPreference (synchronous)

    [Fact]
    public void GetPreference_KeyMissing_ReturnsDefaultValue()
    {
        // Arrange — file does not exist
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = _service.GetPreference("MissingKey", 99);

        // Assert
        result.Should().Be(99);
    }

    [Fact]
    public void GetPreference_KeyExists_ReturnsValue()
    {
        // Arrange
        var prefs = new Dictionary<string, object> { { "Volume", 75 } };
        string json = JsonSerializer.Serialize(prefs);

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllText(It.Is<string>(s => s.Contains("UserPreferences"))))
            .Returns(json);

        // Act
        var result = _service.GetPreference("Volume", 50);

        // Assert — STJ deserializes numbers as JsonElement, the JsonElement.Deserialize<int>() handles it
        result.Should().Be(75);
    }

    [Fact]
    public void GetPreference_EmptyFile_ReturnsDefaultValue()
    {
        // Arrange
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllText(It.Is<string>(s => s.Contains("UserPreferences"))))
            .Returns(string.Empty);

        // Act
        var result = _service.GetPreference("AnyKey", "fallback");

        // Assert
        result.Should().Be("fallback");
    }

    [Fact]
    public void GetPreference_BoolFromJson_ReturnsCorrectBool()
    {
        // Arrange
        var prefs = new Dictionary<string, object> { { "DarkMode", true } };
        string json = JsonSerializer.Serialize(prefs);

        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.ReadAllText(It.Is<string>(s => s.Contains("UserPreferences"))))
            .Returns(json);

        // Act
        var result = _service.GetPreference("DarkMode", false);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Constructor Validation

    [Fact]
    public void Constructor_NullLogService_ThrowsArgumentNullException()
    {
        var act = () => new UserPreferencesService(
            null!, _mockInteractiveUserService.Object, _mockFileSystemService.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logService");
    }

    [Fact]
    public void Constructor_NullInteractiveUserService_ThrowsArgumentNullException()
    {
        var act = () => new UserPreferencesService(
            _mockLogService.Object, null!, _mockFileSystemService.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("interactiveUserService");
    }

    [Fact]
    public void Constructor_NullFileSystemService_ThrowsArgumentNullException()
    {
        var act = () => new UserPreferencesService(
            _mockLogService.Object, _mockInteractiveUserService.Object, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("fileSystemService");
    }

    #endregion
}
