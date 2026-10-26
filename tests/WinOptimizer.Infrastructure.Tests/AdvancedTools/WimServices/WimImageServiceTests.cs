using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Models;
using WinOptimizer.Core.Features.Common.Exceptions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools.WimServices;

public class WimImageServiceTests
{
    private readonly Mock<IFileSystemService> _mockFileSystem = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IDismProcessRunner> _mockDismRunner = new();
    private readonly WimImageService _service;

    public WimImageServiceTests()
    {
        // Default CombinePath behavior: join with backslash
        _mockFileSystem
            .Setup(fs => fs.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] paths) => string.Join("\\", paths));

        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => key);
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _service = new WimImageService(
            _mockFileSystem.Object,
            _mockProcessExecutor.Object,
            _mockLogService.Object,
            _mockLocalization.Object,
            _mockDismRunner.Object);
    }

    #region DetectImageFormatAsync

    [Theory]
    [InlineData(5, "Index : 1\nName : Home\n")]
    [InlineData(0, "Unrecognized inventory")]
    [InlineData(0, "Index : 1\nName : Home\nIndex : 3\nName : Pro\n")]
    public async Task ConversionWithUnreliableInventoryNeverExportsOrDeletes(int exitCode, string output)
    {
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockProcessExecutor.Setup(pe => pe.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult { ExitCode = exitCode, StandardOutput = output });

        Assert.False(await _service.ConvertImageAsync(@"C:\work", ImageFormat.Esd));

        _mockDismRunner.VerifyNoOtherCalls();
        _mockFileSystem.Verify(fs => fs.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionVerifiesEveryExportBeforeReplacingTheSource(bool incompleteTarget)
    {
        const string source = @"C:\work\sources\install.wim";
        const string target = @"C:\work\sources\install.esd";
        var targetExists = false;
        var inventoryVerified = false;
        var exports = new List<string>();
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(source)).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(target)).Returns(() => targetExists);
        _mockProcessExecutor.Setup(pe => pe.ExecuteAsync("dism.exe", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string arguments, CancellationToken _) =>
            {
                Assert.Contains("/English /Get-ImageInfo", arguments);
                var targetQuery = arguments.Contains("install.esd");
                if (targetQuery) inventoryVerified = true;
                return Task.FromResult(new ProcessExecutionResult { StandardOutput = targetQuery && incompleteTarget
                    ? "Index : 1\nName : Home\n" : "Index : 1\nName : Home\nIndex : 2\nName : Pro\n" });
            });
        _mockDismRunner.Setup(runner => runner.RunProcessWithProgressAsync("dism.exe", It.IsAny<string>(),
            It.IsAny<IProgress<TaskProgressDetail>?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IProgress<TaskProgressDetail>?, CancellationToken>((_, args, _, _) => { exports.Add(args); targetExists = true; })
            .ReturnsAsync((0, ""));
        _mockFileSystem.Setup(fs => fs.DeleteFile(source)).Callback(() => Assert.True(inventoryVerified));

        Assert.Equal(!incompleteTarget, await _service.ConvertImageAsync(@"C:\work", ImageFormat.Esd));

        Assert.Equal(2, exports.Count);
        Assert.Contains("/SourceIndex:1 ", exports[0]);
        Assert.Contains("/SourceIndex:2 ", exports[1]);
        _mockFileSystem.Verify(fs => fs.DeleteFile(source), incompleteTarget ? Times.Never() : Times.Once());
        _mockFileSystem.Verify(fs => fs.DeleteFile(target), incompleteTarget ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task CancellingConversionCancelsOnlyTheOwnedDismRunnerAndPreservesTheSource()
    {
        using var cancellation = new CancellationTokenSource();
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns((string path) => path.EndsWith(".wim"));
        _mockProcessExecutor.Setup(pe => pe.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult { StandardOutput = "Index : 1\nName : Home\n" });
        _mockDismRunner.Setup(runner => runner.RunProcessWithProgressAsync("dism.exe", It.IsAny<string>(),
            It.IsAny<IProgress<TaskProgressDetail>?>(), cancellation.Token))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ConvertImageAsync(@"C:\work", ImageFormat.Esd, cancellationToken: cancellation.Token));

        _mockProcessExecutor.Verify(pe => pe.KillProcessesByName(It.IsAny<string>()), Times.Never);
        _mockFileSystem.Verify(fs => fs.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DetectImageFormatAsync_SourcesDirectoryNotFound_ReturnsNull()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _service.DetectImageFormatAsync(@"C:\work");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DetectImageFormatAsync_WimFileExists_ReturnsWimFormat()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(It.Is<string>(p => p.Contains("install.wim")))).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetFileSize(It.IsAny<string>())).Returns(5_000_000_000L);
        _mockProcessExecutor.Setup(pe => pe.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "Index : 1\nName : Windows 11 Pro\nIndex : 2\nName : Windows 11 Home\n"
            });

        // Act
        var result = await _service.DetectImageFormatAsync(@"C:\work");

        // Assert
        result.Should().NotBeNull();
        result!.Format.Should().Be(ImageFormat.Wim);
        result.ImageCount.Should().Be(2);
        result.EditionNames.Should().Contain("Windows 11 Pro");
    }

    [Fact]
    public async Task DetectImageFormatAsync_OnlyEsdFileExists_ReturnsEsdFormat()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(It.Is<string>(p => p.Contains("install.wim")))).Returns(false);
        _mockFileSystem.Setup(fs => fs.FileExists(It.Is<string>(p => p.Contains("install.esd")))).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetFileSize(It.IsAny<string>())).Returns(3_000_000_000L);
        _mockProcessExecutor.Setup(pe => pe.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "Index : 1\nName : Windows 11 Pro\n"
            });

        // Act
        var result = await _service.DetectImageFormatAsync(@"C:\work");

        // Assert
        result.Should().NotBeNull();
        result!.Format.Should().Be(ImageFormat.Esd);
    }

    [Fact]
    public async Task DetectImageFormatAsync_NoImageFiles_ReturnsNull()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _service.DetectImageFormatAsync(@"C:\work");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region DetectAllImageFormatsAsync

    [Fact]
    public async Task DetectAllImageFormatsAsync_SourcesDirectoryNotFound_ReturnsEmptyResult()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _service.DetectAllImageFormatsAsync(@"C:\work");

        // Assert
        result.NeitherExists.Should().BeTrue();
    }

    [Fact]
    public async Task DetectAllImageFormatsAsync_BothFormatsExist_ReturnsBothAndLogsWarning()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockFileSystem.Setup(fs => fs.GetFileSize(It.IsAny<string>())).Returns(1_000_000L);
        _mockProcessExecutor.Setup(pe => pe.ExecuteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "Index : 1\nName : Windows 11 Pro\n"
            });

        // Act
        var result = await _service.DetectAllImageFormatsAsync(@"C:\work");

        // Assert
        result.BothExist.Should().BeTrue();
        _mockLogService.Verify(
            l => l.LogWarning(It.Is<string>(s => s.Contains("Both install.wim and install.esd"))),
            Times.Once);
    }

    #endregion

    #region DeleteImageFileAsync

    [Fact]
    public async Task DeleteImageFileAsync_FileNotFound_ReturnsFalse()
    {
        // Arrange
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(false);

        // Act
        var result = await _service.DeleteImageFileAsync(@"C:\work", ImageFormat.Wim);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteImageFileAsync_FileDeletedSuccessfully_ReturnsTrue()
    {
        // Arrange
        var deleteCallCount = 0;
        _mockFileSystem.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(() =>
        {
            // First call: file exists; after delete: file doesn't exist
            return deleteCallCount == 0;
        });
        _mockFileSystem.Setup(fs => fs.GetFileSize(It.IsAny<string>())).Returns(5_000_000_000L);
        _mockFileSystem.Setup(fs => fs.SetFileAttributes(It.IsAny<string>(), It.IsAny<System.IO.FileAttributes>()));
        _mockFileSystem.Setup(fs => fs.DeleteFile(It.IsAny<string>())).Callback(() => deleteCallCount++);

        // Act
        var result = await _service.DeleteImageFileAsync(@"C:\work", ImageFormat.Wim);

        // Assert
        result.Should().BeTrue();
        _mockFileSystem.Verify(fs => fs.DeleteFile(It.IsAny<string>()), Times.Once);
    }

    #endregion
}
