using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WimStep1ViewModelTests : IDisposable
{
    private readonly Mock<IIsoService> _mockIsoService = new();
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<IFilePickerService> _mockFilePickerService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IResourceService> _mockResourceService = new();

    private readonly WimStep1ViewModel _sut;
    private CancellationTokenSource? _currentTask;

    public WimStep1ViewModelTests()
    {
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockFileSystemService
            .Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] parts) => string.Join("\\", parts));

        _mockFileSystemService
            .Setup(f => f.GetTempPath())
            .Returns("C:\\Temp");

        _mockFileSystemService
            .Setup(f => f.GetFileName(It.IsAny<string>()))
            .Returns((string p) => System.IO.Path.GetFileName(p));

        _mockTaskProgressService.Setup(service => service.StartTask(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(() => _currentTask = new CancellationTokenSource());
        _mockTaskProgressService.SetupGet(service => service.CurrentTaskCancellationSource).Returns(() => _currentTask);
        _mockTaskProgressService.Setup(service => service.CompleteTask()).Callback(() => { _currentTask?.Dispose(); _currentTask = null; });
        _sut = new WimStep1ViewModel(
            _mockIsoService.Object,
            _mockTaskProgressService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockFileSystemService.Object,
            _mockFilePickerService.Object,
            _mockLogService.Object,
            _mockResourceService.Object);
    }

    [Fact]
    public void SelectIsoFileCommand_UpdatesPathCardAndExtractionAvailability()
    {
        _mockFilePickerService
            .Setup(f => f.PickFile(It.IsAny<string[]>(), It.IsAny<string?>()))
            .Returns("C:\\ISOs\\windows.iso");

        _sut.SelectIsoFileCommand.Execute(null);

        _sut.SelectedIsoPath.Should().Be("C:\\ISOs\\windows.iso");
        _sut.SelectIsoCard.Description.Should().Be("C:\\ISOs\\windows.iso");
        _sut.CanStartExtraction.Should().BeTrue();
    }

    [Fact]
    public void SelectIsoFileCommand_WhenCancelled_DoesNotChangeSelectedIsoPath()
    {
        _mockFilePickerService
            .Setup(f => f.PickFile(It.IsAny<string[]>(), It.IsAny<string?>()))
            .Returns((string?)null);

        _sut.SelectIsoFileCommand.Execute(null);

        _sut.SelectedIsoPath.Should().BeEmpty();
    }

    [Fact]
    public async Task SelectWorkingDirectoryCommand_WhenCancelled_DoesNotChangeWorkingDirectory()
    {
        var originalDir = _sut.WorkingDirectory;
        _mockFilePickerService
            .Setup(f => f.PickFolder(It.IsAny<string?>()))
            .Returns((string?)null);

        await _sut.SelectWorkingDirectoryCommand.ExecuteAsync(null);

        _sut.WorkingDirectory.Should().Be(originalDir);
    }

    [Fact]
    public async Task SelectWorkingDirectoryCommand_WhenNotExtractedAlready_CreatesSubDirectory()
    {
        _mockFilePickerService
            .Setup(f => f.PickFolder(It.IsAny<string?>()))
            .Returns("D:\\WorkDir");

        await _sut.SelectWorkingDirectoryCommand.ExecuteAsync(null);

        _mockFileSystemService.Verify(f => f.CreateDirectory("D:\\WorkDir\\WinOptimizerWIM"), Times.Once);
        _sut.WorkingDirectory.Should().Be("D:\\WorkDir\\WinOptimizerWIM");
    }

    [Theory]
    [InlineData("")]
    [InlineData("install.wim")]
    [InlineData("install.esd")]
    public async Task SelectWorkingDirectoryCommand_RequiresUsableExtractedMedia(string installImage)
    {
        var hasImage = installImage.Length > 0;
        var directory = Directory.CreateTempSubdirectory("WinOptimizerSetupMedia");
        try
        {
            var sources = directory.CreateSubdirectory("sources").FullName;
            directory.CreateSubdirectory("boot");
            if (hasImage)
            {
                File.WriteAllBytes(Path.Combine(directory.FullName, "setup.exe"), [1]);
                File.WriteAllBytes(Path.Combine(sources, "boot.wim"), [1]);
                File.WriteAllBytes(Path.Combine(sources, installImage), [1]);
            }
            _sut.HasExtractedIsoAlready = true;
            _mockFilePickerService.Setup(f => f.PickFolder(It.IsAny<string?>())).Returns(directory.FullName);
            _mockFileSystemService.Setup(f => f.GetPathRoot(directory.FullName)).Returns(directory.Root.FullName);
            _mockFileSystemService.Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
            _mockFileSystemService.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns((string path) => Directory.Exists(path));
            _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
            _mockFileSystemService.Setup(f => f.GetFileSize(It.IsAny<string>())).Returns((string path) => new FileInfo(path).Length);

            await _sut.SelectWorkingDirectoryCommand.ExecuteAsync(null);

            Assert.Equal(hasImage ? directory.FullName : string.Empty, _sut.WorkingDirectory);
            Assert.Equal(hasImage, _sut.IsExtractionComplete);
            _mockDialogService.Verify(d => d.ShowErrorAsync("WIMUtil_Msg_InvalidDirectory", It.IsAny<string>()),
                hasImage ? Times.Never() : Times.Once());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task SelectWorkingDirectoryCommand_WhenExtractedAlready_InvalidDirectory_ShowsError()
    {
        _sut.HasExtractedIsoAlready = true;
        _mockFilePickerService
            .Setup(f => f.PickFolder(It.IsAny<string?>()))
            .Returns("D:\\InvalidDir");

        _mockFileSystemService
            .Setup(f => f.GetPathRoot("D:\\InvalidDir"))
            .Returns("D:\\");

        _mockFileSystemService
            .Setup(f => f.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        _mockFileSystemService
            .Setup(f => f.GetDirectories("D:\\InvalidDir"))
            .Returns(new[] { "D:\\InvalidDir\\randomfolder" });

        _mockFileSystemService
            .Setup(f => f.GetFileName("D:\\InvalidDir\\randomfolder"))
            .Returns("randomfolder");

        await _sut.SelectWorkingDirectoryCommand.ExecuteAsync(null);

        _mockDialogService.Verify(d => d.ShowErrorAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SelectWorkingDirectoryCommand_WhenCreateDirectoryFails_SetsWorkingDirectoryToEmpty()
    {
        _mockFilePickerService
            .Setup(f => f.PickFolder(It.IsAny<string?>()))
            .Returns("D:\\WorkDir");

        _mockFileSystemService
            .Setup(f => f.CreateDirectory(It.IsAny<string>()))
            .Throws(new UnauthorizedAccessException("Access denied"));

        await _sut.SelectWorkingDirectoryCommand.ExecuteAsync(null);

        _sut.WorkingDirectory.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateExtractedIsoDirectory_DriveRoot_ReturnsFalse()
    {
        _mockFileSystemService
            .Setup(f => f.GetPathRoot("D:\\"))
            .Returns("D:\\");

        var result = await _sut.ValidateExtractedIsoDirectory("D:\\");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task StartIsoExtractionCommand_OnFailure_DoesNotSetIsExtractionComplete()
    {
        _sut.SelectedIsoPath = "C:\\test.iso";
        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<Core.Features.Common.Models.TaskProgressDetail>());

        _mockIsoService
            .Setup(i => i.ExtractIsoAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<Core.Features.Common.Models.TaskProgressDetail>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.StartIsoExtractionCommand.ExecuteAsync(null);

        _sut.IsExtractionComplete.Should().BeFalse();
    }

    [Fact]
    public async Task StartIsoExtractionCommand_DisablesCardsWhileExtracting()
    {
        _sut.SelectedIsoPath = "C:\\test.iso";
        bool isoCardEnabledDuringExtraction = true;
        bool dirCardEnabledDuringExtraction = true;

        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<Core.Features.Common.Models.TaskProgressDetail>());

        _mockIsoService
            .Setup(i => i.ExtractIsoAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<Core.Features.Common.Models.TaskProgressDetail>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                isoCardEnabledDuringExtraction = _sut.SelectIsoCard.IsEnabled;
                dirCardEnabledDuringExtraction = _sut.SelectDirectoryCard.IsEnabled;
                return Task.FromResult(true);
            });

        await _sut.StartIsoExtractionCommand.ExecuteAsync(null);

        isoCardEnabledDuringExtraction.Should().BeFalse();
        dirCardEnabledDuringExtraction.Should().BeFalse();
    }

    [Fact]
    public async Task StartIsoExtractionCommand_AlwaysCallsCompleteTask()
    {
        _sut.SelectedIsoPath = "C:\\test.iso";
        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<Core.Features.Common.Models.TaskProgressDetail>());

        _mockIsoService
            .Setup(i => i.ExtractIsoAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<Core.Features.Common.Models.TaskProgressDetail>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.StartIsoExtractionCommand.ExecuteAsync(null);

        _mockTaskProgressService.Verify(t => t.CompleteTask(), Times.Once);
    }
    public void Dispose() => _currentTask?.Dispose();
}
