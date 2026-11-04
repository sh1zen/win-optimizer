using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WimStep4IsoViewModelTests : IDisposable
{
    private readonly Mock<IOscdimgToolManager> _mockOscdimgToolManager = new();
    private readonly Mock<IIsoService> _mockIsoService = new();
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<IFilePickerService> _mockFilePickerService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IResourceService> _mockResourceService = new();

    private readonly WimStep4IsoViewModel _sut;
    private CancellationTokenSource? _currentTask;

    public WimStep4IsoViewModelTests()
    {
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockFileSystemService
            .Setup(f => f.GetFileName(It.IsAny<string>()))
            .Returns((string p) => System.IO.Path.GetFileName(p));

        _mockTaskProgressService.Setup(service => service.StartTask(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns(() => _currentTask = new CancellationTokenSource());
        _mockTaskProgressService.SetupGet(service => service.CurrentTaskCancellationSource).Returns(() => _currentTask);
        _mockTaskProgressService.Setup(service => service.CompleteTask()).Callback(() => { _currentTask?.Dispose(); _currentTask = null; });

        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<TaskProgressDetail>());

        _sut = CreateViewModel();
    }

    private WimStep4IsoViewModel CreateViewModel(ITaskProgressService? tasks = null) => new(
            _mockOscdimgToolManager.Object,
            _mockIsoService.Object,
            tasks ?? _mockTaskProgressService.Object,
            _mockProcessExecutor.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockFileSystemService.Object,
            _mockFilePickerService.Object,
            _mockLogService.Object,
            _mockResourceService.Object);

    public void Dispose()
    {
        _sut.Dispose();
        _currentTask?.Dispose();
    }

    [Fact]
    public async Task DownloadOscdimgCommand_OnSuccess_SetsIsOscdimgAvailable()
    {
        _mockOscdimgToolManager
            .Setup(o => o.EnsureOscdimgAvailableAsync(
                It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.DownloadOscdimgCommand.ExecuteAsync(null);

        _sut.IsOscdimgAvailable.Should().BeTrue();
        _sut.DownloadOscdimgCard.IsComplete.Should().BeTrue();
        _sut.DownloadOscdimgCard.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadOscdimgCommand_OnFailure_SetsHasFailed()
    {
        _mockOscdimgToolManager
            .Setup(o => o.EnsureOscdimgAvailableAsync(
                It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.DownloadOscdimgCommand.ExecuteAsync(null);

        _sut.DownloadOscdimgCard.HasFailed.Should().BeTrue();
        _sut.DownloadOscdimgCard.IsEnabled.Should().BeTrue();
        _sut.IsOscdimgAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadOscdimgCommand_OnException_SetsHasFailed()
    {
        _mockOscdimgToolManager
            .Setup(o => o.EnsureOscdimgAvailableAsync(
                It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Install failed"));

        await _sut.DownloadOscdimgCommand.ExecuteAsync(null);

        _sut.DownloadOscdimgCard.HasFailed.Should().BeTrue();
        _sut.DownloadOscdimgCard.IsProcessing.Should().BeFalse();
        _sut.DownloadOscdimgCard.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void SelectIsoOutputLocationCommand_UpdatesPathAndCard()
    {
        _mockFilePickerService
            .Setup(f => f.PickSaveFile(It.IsAny<string[]>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns("D:\\Output\\WinOptimizer_Windows.iso");

        _sut.SelectIsoOutputLocationCommand.Execute(null);

        _sut.OutputIsoPath.Should().Be("D:\\Output\\WinOptimizer_Windows.iso");
        _sut.SelectOutputCard.Description.Should().Contain("WinOptimizer_Windows.iso");
    }

    [Fact]
    public void SelectIsoOutputLocationCommand_WhenCancelled_DoesNotChangeOutputPath()
    {
        _mockFilePickerService
            .Setup(f => f.PickSaveFile(It.IsAny<string[]>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((string?)null);

        _sut.SelectIsoOutputLocationCommand.Execute(null);

        _sut.OutputIsoPath.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateIsoCommand_WhenOscdimgNotAvailable_ShowsWarning()
    {
        _sut.IsOscdimgAvailable = false;

        await _sut.CreateIsoCommand.ExecuteAsync(null);

        _mockDialogService.Verify(d => d.ShowWarningAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockTaskProgressService.Verify(service => service.StartTask(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _mockTaskProgressService.Verify(service => service.CompleteTask(), Times.Never);
    }

    [Fact]
    public async Task CreateIsoCommand_WhenOutputPathEmpty_ShowsWarning()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.OutputIsoPath = string.Empty;

        await _sut.CreateIsoCommand.ExecuteAsync(null);

        _mockDialogService.Verify(d => d.ShowWarningAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockTaskProgressService.Verify(service => service.CompleteTask(), Times.Never);
    }

    [Fact]
    public async Task CreateIsoCommand_WhenWorkingDirectoryEmpty_ShowsWarning()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.OutputIsoPath = "D:\\Output\\test.iso";
        _sut.WorkingDirectory = string.Empty;

        await _sut.CreateIsoCommand.ExecuteAsync(null);

        _mockDialogService.Verify(d => d.ShowWarningAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockIsoService.Verify(i => i.CreateIsoAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IProgress<WinOptimizer.Core.Features.Common.Models.TaskProgressDetail>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _mockTaskProgressService.Verify(service => service.CompleteTask(), Times.Never);
    }

    [Fact]
    public async Task CreateIsoCommand_OnSuccess_SetsIsIsoCreated()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.OutputIsoPath = "D:\\Output\\test.iso";
        _sut.WorkingDirectory = "C:\\WorkDir";

        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<TaskProgressDetail>());

        _mockIsoService
            .Setup(i => i.CreateIsoAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // User clicks "Close" when asked to open folder
        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false });

        await _sut.CreateIsoCommand.ExecuteAsync(null);

        _sut.IsIsoCreated.Should().BeTrue();
    }

    [Fact]
    public async Task CreateIsoCommand_OnFailure_DoesNotSetIsIsoCreated()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.OutputIsoPath = "D:\\Output\\test.iso";
        _sut.WorkingDirectory = "C:\\WorkDir";

        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<TaskProgressDetail>());

        _mockIsoService
            .Setup(i => i.CreateIsoAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.CreateIsoCommand.ExecuteAsync(null);

        _sut.IsIsoCreated.Should().BeFalse();
    }

    [Fact]
    public async Task CreateIsoCommand_CompletesTheTaskItStarted()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.OutputIsoPath = "D:\\Output\\test.iso";
        _sut.WorkingDirectory = "C:\\WorkDir";

        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<TaskProgressDetail>());

        _mockIsoService
            .Setup(i => i.CreateIsoAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false });

        await _sut.CreateIsoCommand.ExecuteAsync(null);

        _mockTaskProgressService.Verify(t => t.CompleteTask(), Times.Once);
    }

    [Fact]
    public async Task CreateIsoCommand_DisablesSelectOutputCardDuringCreation()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.OutputIsoPath = "D:\\Output\\test.iso";
        _sut.WorkingDirectory = "C:\\WorkDir";
        bool wasDisabledDuring = false;

        _mockTaskProgressService
            .Setup(t => t.CreatePowerShellProgress())
            .Returns(new Progress<TaskProgressDetail>());

        _mockIsoService
            .Setup(i => i.CreateIsoAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                wasDisabledDuring = !_sut.SelectOutputCard.IsEnabled;
                return Task.FromResult(true);
            });

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false });

        await _sut.CreateIsoCommand.ExecuteAsync(null);

        wasDisabledDuring.Should().BeTrue();
        // After completion, card should be re-enabled
        _sut.SelectOutputCard.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void UpdateDownloadOscdimgCardState_WhenAvailable_DisablesAndMarkComplete()
    {
        _sut.IsOscdimgAvailable = true;

        _sut.UpdateDownloadOscdimgCardState();

        _sut.DownloadOscdimgCard.IsEnabled.Should().BeFalse();
        _sut.DownloadOscdimgCard.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task CancellingIsoCreationPreservesTheDestinationOwnedByTheService()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.WorkingDirectory = @"C:\Example\Windows";
        _sut.OutputIsoPath = @"C:\Example\output.iso";
        _mockFileSystemService.Setup(files => files.FileExists(_sut.OutputIsoPath)).Returns(true);
        _mockIsoService.Setup(service => service.CreateIsoAsync(_sut.WorkingDirectory, _sut.OutputIsoPath,
            It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await _sut.CreateIsoCommand.ExecuteAsync(null);
        Assert.False(_sut.IsIsoCreated);
        Assert.True(_sut.SelectOutputCard.IsEnabled);
        Assert.Equal("WIMUtil_Desc_IsoCreateCancelled", _sut.SelectOutputCard.Description);
        _mockFileSystemService.Verify(files => files.DeleteFile(It.IsAny<string>()), Times.Never);
        _mockTaskProgressService.Verify(tasks => tasks.CompleteTask(), Times.Once);
    }

    /// <summary>ISO commands leave another operation's progress and cancellation token intact.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandsWaitForAnExistingTaskWithoutCancellingOrCompletingIt(bool download)
    {
        var tasks = new TaskProgressService(_mockLogService.Object);
        var existing = tasks.StartTask("Existing operation");
        using var vm = CreateViewModel(tasks);
        vm.IsOscdimgAvailable = true;
        vm.WorkingDirectory = @"C:\Example\Windows";
        vm.OutputIsoPath = @"C:\Example\output.iso";
        try
        {
            await (download ? vm.DownloadOscdimgCommand : vm.CreateIsoCommand).ExecuteAsync(null);
            Assert.True(tasks.IsTaskRunning);
            Assert.Same(existing, tasks.CurrentTaskCancellationSource);
            Assert.False(existing.IsCancellationRequested);
            Assert.Equal(0, tasks.CurrentProgress);
            _mockIsoService.Verify(service => service.CreateIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockOscdimgToolManager.Verify(service => service.EnsureOscdimgAvailableAsync(It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockDialogService.Verify(service => service.ShowWarningAsync("Cleaner_Wait", "Dialog_Warning", It.IsAny<string>()), Times.Once);
        }
        finally { tasks.CompleteTask(); }
    }

    /// <summary>A command completing after task replacement cannot finish or lose the replacement's cancellation source.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinishingAReplacedTaskPreservesTheNewOwner(bool download)
    {
        var tasks = new TaskProgressService(_mockLogService.Object);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockIsoService.Setup(service => service.CreateIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        _mockOscdimgToolManager.Setup(service => service.EnsureOscdimgAvailableAsync(It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var vm = CreateViewModel(tasks);
        vm.IsOscdimgAvailable = true;
        vm.WorkingDirectory = @"C:\Example\Windows";
        vm.OutputIsoPath = @"C:\Example\output.iso";
        var operation = (download ? vm.DownloadOscdimgCommand : vm.CreateIsoCommand).ExecuteAsync(null);
        try
        {
            var started = tasks.CurrentTaskCancellationSource;
            Assert.NotNull(started);
            if (download) _mockOscdimgToolManager.Verify(service => service.EnsureOscdimgAvailableAsync(It.IsAny<IProgress<TaskProgressDetail>>(), started.Token), Times.Once);
            else _mockIsoService.Verify(service => service.CreateIsoAsync(vm.WorkingDirectory, vm.OutputIsoPath, It.IsAny<IProgress<TaskProgressDetail>>(), started.Token), Times.Once);
            var replacement = tasks.StartTask("Replacement operation");
            Assert.True(started.IsCancellationRequested);
            pending.SetResult(false);
            await operation;
            Assert.True(tasks.IsTaskRunning);
            Assert.Same(replacement, tasks.CurrentTaskCancellationSource);
            Assert.False(replacement.IsCancellationRequested);
            Assert.Equal("Replacement operation", tasks.CurrentStatusText);
        }
        finally
        {
            pending.TrySetResult(false);
            await operation;
            tasks.CompleteTask();
        }
    }

    /// <summary>Each attempt publishes its own completion state, including partial work, failures and cancellation.</summary>
    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancelled")]
    public async Task RegenerationClearsEarlierSuccessBeforeWorkAndRetainsFailureState(string outcome)
    {
        _sut.IsOscdimgAvailable = true;
        _sut.WorkingDirectory = @"C:\Example\Windows";
        _sut.OutputIsoPath = @"C:\Example\output.iso";
        _mockDialogService.Setup(service => service.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        _mockIsoService.Setup(service => service.CreateIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await _sut.CreateIsoCommand.ExecuteAsync(null);
        Assert.True(_sut.IsIsoCreated);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockIsoService.Setup(service => service.CreateIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        var operation = _sut.CreateIsoCommand.ExecuteAsync(null);
        try { Assert.False(_sut.IsIsoCreated); }
        finally
        {
            if (outcome == "exception") pending.SetException(new IOException("ISO write failed."));
            else if (outcome == "cancelled") pending.SetCanceled();
            else pending.SetResult(false);
        }
        await operation;
        Assert.False(_sut.IsIsoCreated);
        Assert.True(_sut.SelectOutputCard.IsEnabled);
        _mockTaskProgressService.Verify(service => service.CompleteTask(), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingAnArtifactPathClearsItsCompletion(bool inputDirectory)
    {
        _sut.WorkingDirectory = @"C:\Example\Windows";
        _sut.OutputIsoPath = @"C:\Example\first.iso";
        _sut.IsIsoCreated = true;
        if (inputDirectory) _sut.WorkingDirectory = @"C:\Example\OtherWindows";
        else
        {
            _mockFilePickerService.Setup(service => service.PickSaveFile(It.IsAny<string[]>(), It.IsAny<string>(), It.IsAny<string>())).Returns(@"C:\Example\second.iso");
            _sut.SelectIsoOutputLocationCommand.Execute(null);
        }
        Assert.False(_sut.IsIsoCreated);
    }

    [Fact]
    public async Task ACompletedAttemptDoesNotMarkAChangedWorkingDirectoryComplete()
    {
        _sut.IsOscdimgAvailable = true;
        _sut.WorkingDirectory = @"C:\Example\Windows";
        _sut.OutputIsoPath = @"C:\Example\output.iso";
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockIsoService.Setup(service => service.CreateIsoAsync(_sut.WorkingDirectory, _sut.OutputIsoPath, It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        _mockDialogService.Setup(service => service.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        var operation = _sut.CreateIsoCommand.ExecuteAsync(null);
        try
        {
            Assert.False(operation.IsCompleted);
            _sut.WorkingDirectory = @"C:\Example\OtherWindows";
        }
        finally { pending.TrySetResult(true); }
        await operation;
        Assert.False(_sut.IsIsoCreated);
    }

    [Fact]
    public void UpdateDownloadOscdimgCardState_WhenNotAvailable_EnablesCard()
    {
        _sut.IsOscdimgAvailable = false;

        _sut.UpdateDownloadOscdimgCardState();

        _sut.DownloadOscdimgCard.IsEnabled.Should().BeTrue();
        _sut.DownloadOscdimgCard.IsComplete.Should().BeFalse();
    }
}
