using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools.WimServices;

public class WimCustomizationServiceTests
{
    private readonly Mock<IFileSystemService> _mockFileSystem = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IDriverCategorizer> _mockDriverCategorizer = new();
    private readonly Mock<IDismProcessRunner> _mockDismRunner = new();
    private readonly WimCustomizationService _service;

    public WimCustomizationServiceTests()
    {
        _mockFileSystem
            .Setup(fs => fs.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] paths) => string.Join("\\", paths));

        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => key);
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _service = new WimCustomizationService(
            _mockFileSystem.Object,
            _mockLogService.Object,
            _mockLocalization.Object,
            _mockDriverCategorizer.Object,
            _mockDismRunner.Object);
    }

    #region AddXmlToImageAsync

    [Fact]
    public async Task AddXmlToImageAsync_XmlFileNotFound_ReturnsFalse()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _service.AddXmlToImageAsync(@"C:\missing.xml", @"C:\work");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddXmlToImageAsync_WorkingDirectoryNotFound_ReturnsFalse()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _service.AddXmlToImageAsync(@"C:\answer.xml", @"C:\missing_dir");

        // Assert
        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddXmlToImageAsync_ValidAnswerFilePreservesTheEncoding(bool utf16)
    {
        var encoding = utf16 ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8;
        var xml = $"<?xml version=\"1.0\" encoding=\"{encoding.WebName}\"?><unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><!-- città --></unattend>";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray();
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadAllBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(bytes);

        var result = await _service.AddXmlToImageAsync(@"C:\answer.xml", @"C:\work");

        result.Should().BeTrue();
        _mockFileSystem.Verify(
            fs => fs.WriteAllBytesAsync(
                It.Is<string>(p => p.EndsWith("autounattend.xml")),
                It.Is<byte[]>(data => data.SequenceEqual(bytes)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("<settings />")]
    [InlineData("<unattend />")]
    [InlineData("<unattend xmlns='urn:schemas-microsoft-com:unattend'>")]
    public async Task AddXmlToImageAsync_InvalidAnswerFileIsRejectedBeforeWriting(string xml)
    {
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadAllBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Encoding.UTF8.GetBytes(xml));

        await Assert.ThrowsAsync<InvalidDataException>(() => _service.AddXmlToImageAsync(@"C:\answer.xml", @"C:\work"));
        _mockFileSystem.Verify(fs => fs.WriteAllBytesAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddXmlToImageAsync_WriteThrows_ReturnsFalse()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadAllBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Disk error"));

        // Act
        var result = await _service.AddXmlToImageAsync(@"C:\answer.xml", @"C:\work");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region AddDriversAsync

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DriverSetupPreservesExistingCommandsAndAddsOneReturningHook(bool utf8Bom)
    {
        var original = System.Text.Encoding.UTF8.GetBytes("@echo OEM setup\r\nexit /b 0\r\n");
        byte[] setup = utf8Bom ? [0xef, 0xbb, 0xbf, .. original] : original;
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.ReadAllBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Task.FromResult(setup));
        _mockFileSystem.Setup(fs => fs.WriteAllBytesAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], CancellationToken>((_, bytes, _) => setup = bytes).Returns(Task.CompletedTask);
        _mockDriverCategorizer.Setup(dc => dc.CategorizeAndCopyDrivers(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(1);

        Assert.True(await _service.AddDriversAsync(@"C:\work", @"C:\drivers"));
        Assert.True(await _service.AddDriversAsync(@"C:\work", @"C:\drivers"));

        var hook = System.Text.Encoding.ASCII.GetBytes("@call \"%~dp0WinOptimizer-Drivers.cmd\"\r\n");
        Assert.Equal(hook.Concat(original), setup);
        _mockFileSystem.Verify(fs => fs.WriteAllBytesAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockFileSystem.Verify(fs => fs.WriteAllTextAsync(It.Is<string>(p => p.EndsWith("WinOptimizer-Drivers.cmd")),
            It.Is<string>(script => script.Contains("exit /b %DriverExit%") && script.Contains("%SystemRoot%")), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DriverSetupWriteFailureIsReportedAsFailure()
    {
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockDriverCategorizer.Setup(dc => dc.CategorizeAndCopyDrivers(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(1);
        _mockFileSystem.Setup(fs => fs.WriteAllBytesAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Setup script is read-only"));

        Assert.False(await _service.AddDriversAsync(@"C:\work", @"C:\drivers"));
        _mockLogService.Verify(log => log.LogError(It.Is<string>(message => message.Contains("Setup script is read-only")), It.IsAny<Exception>()), Times.Once);
    }

    [Fact]
    public async Task AddDriversAsync_DriverSourcePathDoesNotExist_ReturnsFalse()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.Is<string>(p => p == @"C:\drivers")))
            .Returns(false);

        // Act
        var result = await _service.AddDriversAsync(@"C:\work", @"C:\drivers");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddDriversAsync_NoDriversCopied_ReturnsFalse()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockDriverCategorizer.Setup(dc => dc.CategorizeAndCopyDrivers(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(0);

        // Act
        var result = await _service.AddDriversAsync(@"C:\work", @"C:\drivers");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddDriversAsync_DriversSuccessfullyCopied_ReturnsTrue()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockDriverCategorizer.Setup(dc => dc.CategorizeAndCopyDrivers(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(5);
        _mockFileSystem.Setup(fs => fs.CreateDirectory(It.IsAny<string>()));
        _mockFileSystem.Setup(fs => fs.WriteAllText(It.IsAny<string>(), It.IsAny<string>()));

        // Act
        var result = await _service.AddDriversAsync(@"C:\work", @"C:\drivers");

        // Assert
        result.Should().BeTrue();
    }

    #endregion
}
