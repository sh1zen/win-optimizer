using CommunityToolkit.Mvvm.Input;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.AdvancedTools.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Exercises the production wizard and task service with synthetic media and fake OS boundaries.</summary>
public sealed class WimWorkflowRegressionTests : IDisposable
{
    private readonly Mock<IIsoService> _iso = new();
    private readonly Mock<IWimCustomizationService> _customization = new();
    private readonly Mock<IWimImageService> _images = new();
    private readonly Mock<IAutounattendXmlGeneratorService> _xml = new();
    private readonly Mock<IFilePickerService> _picker = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly TaskProgressService _tasks;
    private readonly WimUtilViewModel _vm;

    public WimWorkflowRegressionTests()
    {
        var log = Mock.Of<ILogService>();
        _tasks = new TaskProgressService(log);
        var text = new Mock<ILocalizationService>();
        text.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        var files = new Mock<IFileSystemService>();
        files.Setup(service => service.CombinePath(It.IsAny<string[]>())).Returns((string[] parts) => Path.Combine(parts));
        files.Setup(service => service.GetTempPath()).Returns(@"C:\Example\Temp");
        files.Setup(service => service.GetFileName(It.IsAny<string>())).Returns((string path) => Path.GetFileName(path));
        files.Setup(service => service.DirectoryExists(It.IsAny<string>())).Returns(true);
        files.Setup(service => service.GetFiles(It.IsAny<string>(), "*", SearchOption.AllDirectories)).Returns([@"C:\Example\Drivers\device.inf"]);
        var dispatcher = new Mock<IDispatcherService>();
        dispatcher.Setup(service => service.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => action());
        _dialogs.Setup(service => service.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync((ConfirmationRequest request) => new ConfirmationResponse { Confirmed = request.Title != "WIMUtil_Desc_IsoCreatedSuccess" });
        _iso.Setup(service => service.ExtractIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _iso.Setup(service => service.CreateIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _customization.Setup(service => service.AddDriversAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _xml.Setup(service => service.GenerateFromCurrentSelectionsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>?>()))
            .ReturnsAsync((string path, IReadOnlyList<ConfigurationItem>? _) => path);
        _images.Setup(service => service.DetectAllImageFormatsAsync(It.IsAny<string>())).ReturnsAsync(new ImageDetectionResult
        { WimInfo = new ImageFormatInfo { Format = ImageFormat.Wim, FileSizeBytes = 1024 } });
        _images.Setup(service => service.ConvertImageAsync(It.IsAny<string>(), It.IsAny<ImageFormat>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var selectedApps = new Mock<ISelectedAppsProvider>();
        selectedApps.Setup(service => service.GetSelectedWindowsAppsAsync()).ReturnsAsync([new ConfigurationItem { Id = "Example.App" }]);
        _vm = new WimUtilViewModel(Mock.Of<IOscdimgToolManager>(), _iso.Object, _images.Object, _customization.Object,
            _tasks, _dialogs.Object, log, _xml.Object, selectedApps.Object, text.Object, dispatcher.Object,
            Mock.Of<IProcessExecutor>(), files.Object, _picker.Object, Mock.Of<IResourceService>());
        _vm.Step1.SelectedIsoPath = @"C:\Example\Windows.iso";
        _vm.Step1.WorkingDirectory = @"C:\Example\Media";
        _vm.Step4.OutputIsoPath = @"C:\Example\output.iso";
        _vm.Step4.IsOscdimgAvailable = true;
    }

    /// <summary>Creates all completion evidence through the normal command path.</summary>
    private async Task PrepareMediaAsync()
    {
        await _vm.Step1.StartIsoExtractionCommand.ExecuteAsync(null);
        await _vm.Step2.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);
        await _vm.Step3.ExtractAndAddSystemDriversCommand.ExecuteAsync(null);
        await _vm.Step4.CreateIsoCommand.ExecuteAsync(null);
        Assert.True(_vm.Step1State.IsComplete);
        Assert.True(_vm.Step2State.IsComplete);
        Assert.True(_vm.Step3State.IsComplete);
        Assert.True(_vm.Step4State.IsComplete);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("system")]
    [InlineData("custom")]
    [InlineData("conversion")]
    public async Task EditingPreparedMediaInvalidatesThePreviouslyBuiltIso(string kind)
    {
        await PrepareMediaAsync();
        _picker.Setup(picker => picker.PickFolder(It.IsAny<string>())).Returns(@"C:\Example\Drivers");
        var command = kind == "xml" ? _vm.Step2.GenerateWinOptimizerXmlCommand : DriverCommand(kind);
        await command.ExecuteAsync(null);
        Assert.False(_vm.Step4.IsIsoCreated);
        Assert.False(_vm.Step4State.IsComplete);
    }

    [Fact]
    public async Task PendingOrFailedXmlChangesInvalidateIsoBeforeTheWriteCompletes()
    {
        await PrepareMediaAsync();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _xml.Setup(service => service.GenerateFromCurrentSelectionsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>?>())).Returns(pending.Task);
        var operation = _vm.Step2.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);
        Assert.False(_vm.Step4.IsIsoCreated);
        pending.SetException(new IOException("Write failed"));
        await operation;
        Assert.False(_vm.Step4.IsIsoCreated);
    }

    private void AssertDependentEvidenceCleared()
    {
        Assert.False(_vm.Step2.IsXmlAdded);
        Assert.Empty(_vm.Step2.SelectedXmlPath);
        Assert.False(_vm.Step2.GenerateWinOptimizerXmlCard.IsComplete);
        Assert.False(_vm.Step2.SelectXmlCard.IsComplete);
        Assert.False(_vm.Step3.AreDriversAdded);
        Assert.False(_vm.Step3.ExtractSystemDriversCard.IsComplete);
        Assert.False(_vm.Step3.SelectCustomDriversCard.IsComplete);
        Assert.False(_vm.Step4.IsIsoCreated);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancelled")]
    public async Task ReextractionInvalidatesDependentStepsAndFailuresKeepThemLocked(string outcome)
    {
        await PrepareMediaAsync();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _iso.Setup(service => service.ExtractIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        var operation = _vm.Step1.StartIsoExtractionCommand.ExecuteAsync(null);
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.False(_vm.Step1.IsExtractionComplete);
            AssertDependentEvidenceCleared();
            Assert.False(_vm.Step2State.IsAvailable);
            Assert.False(_vm.Step3State.IsAvailable);
            Assert.False(_vm.Step4State.IsAvailable);
        }
        finally
        {
            if (outcome == "cancelled") pending.TrySetCanceled();
            else if (outcome == "exception") pending.TrySetException(new IOException("Extraction failed."));
            else pending.TrySetResult(false);
        }
        await operation;
        Assert.False(_vm.Step1.IsExtractionComplete);
        Assert.False(_vm.Step1.SelectIsoCard.IsComplete);
        Assert.False(_vm.Step2State.IsAvailable);
        Assert.False(_vm.Step1.IsExtracting);
        Assert.False(_tasks.IsTaskRunning);
        _iso.Setup(service => service.ExtractIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await _vm.Step1.StartIsoExtractionCommand.ExecuteAsync(null);
        Assert.True(_vm.Step2State.IsAvailable);
        AssertDependentEvidenceCleared();
    }

    [Fact]
    public async Task ChoosingAnotherWorkingDirectoryClearsPriorXmlAndDrivers()
    {
        await PrepareMediaAsync();
        _picker.Setup(service => service.PickFolder(It.IsAny<string>())).Returns(@"C:\Example\OtherParent");
        await _vm.Step1.SelectWorkingDirectoryCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\Example\OtherParent\WinOptimizerWIM", _vm.Step1.WorkingDirectory);
        Assert.Equal(_vm.Step1.WorkingDirectory, _vm.Step2.WorkingDirectory);
        Assert.Equal(_vm.Step1.WorkingDirectory, _vm.Step3.WorkingDirectory);
        Assert.False(_vm.Step1.IsExtractionComplete);
        AssertDependentEvidenceCleared();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachStepInvalidatesItsOwnEvidenceOnDirectoryChange(bool xml)
    {
        await PrepareMediaAsync();
        if (xml)
        {
            _vm.Step2.WorkingDirectory = @"C:\Example\OtherMedia";
            Assert.False(_vm.Step2.IsXmlAdded);
            Assert.Empty(_vm.Step2.SelectedXmlPath);
            Assert.False(_vm.Step2.GenerateWinOptimizerXmlCard.IsComplete);
        }
        else
        {
            _vm.Step3.WorkingDirectory = @"C:\Example\OtherMedia";
            Assert.False(_vm.Step3.AreDriversAdded);
            Assert.False(_vm.Step3.ExtractSystemDriversCard.IsComplete);
        }
    }

    [Theory]
    [InlineData("xml", true)]
    [InlineData("system", true)]
    [InlineData("custom", true)]
    [InlineData("iso", true)]
    [InlineData("xml", false)]
    [InlineData("system", false)]
    [InlineData("custom", false)]
    [InlineData("iso", false)]
    public async Task PendingWorkDoesNotPublishEvidenceForOtherMedia(string kind, bool changeDirectory)
    {
        await PrepareMediaAsync();
        var directory = _vm.Step1.WorkingDirectory;
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingXml = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _xml.Setup(service => service.GenerateFromCurrentSelectionsAsync(Path.Combine(directory, "autounattend.xml"), It.IsAny<IReadOnlyList<ConfigurationItem>?>())).Returns(pendingXml.Task);
        _customization.Setup(service => service.AddDriversAsync(directory, It.IsAny<string?>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        _iso.Setup(service => service.CreateIsoAsync(directory, It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        _picker.Setup(service => service.PickFolder(It.IsAny<string>())).Returns(@"C:\Example\Drivers");
        var command = kind == "xml" ? _vm.Step2.GenerateWinOptimizerXmlCommand : kind == "iso" ? _vm.Step4.CreateIsoCommand : DriverCommand(kind);
        var operation = command.ExecuteAsync(null);
        try
        {
            Assert.False(operation.IsCompleted);
            if (changeDirectory) _vm.Step1.WorkingDirectory = @"C:\Example\OtherMedia";
            else _vm.Step1.SelectedIsoPath = @"C:\Example\OtherWindows.iso";
        }
        finally { pending.TrySetResult(true); pendingXml.TrySetResult(Path.Combine(directory, "autounattend.xml")); }
        await operation;
        AssertDependentEvidenceCleared();
    }

    [Theory]
    [InlineData("custom", false)]
    [InlineData("custom", true)]
    [InlineData("system", false)]
    [InlineData("system", true)]
    [InlineData("conversion", false)]
    [InlineData("conversion", true)]
    public async Task PickerOrConfirmationCannotCompleteOrCancelANewerTask(string kind, bool confirmed)
    {
        await PrepareMediaAsync();
        CancellationTokenSource? replacement = null;
        if (kind == "custom")
            _picker.Setup(service => service.PickFolder(It.IsAny<string>())).Returns(() =>
            { replacement = _tasks.StartTask("Other operation"); return confirmed ? @"C:\Example\Drivers" : null; });
        else
            _dialogs.Setup(service => service.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(() =>
            { replacement = _tasks.StartTask("Other operation"); return new ConfirmationResponse { Confirmed = confirmed }; });
        _customization.Invocations.Clear();
        _images.Invocations.Clear();
        await DriverCommand(kind).ExecuteAsync(null);
        Assert.NotNull(replacement);
        Assert.True(_tasks.IsTaskRunning);
        Assert.Same(replacement, _tasks.CurrentTaskCancellationSource);
        Assert.False(replacement.IsCancellationRequested);
        Assert.Equal(0, _tasks.CurrentProgress);
        _customization.Verify(service => service.AddDriversAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()), Times.Never);
        _images.Verify(service => service.ConvertImageAsync(It.IsAny<string>(), It.IsAny<ImageFormat>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("custom")]
    [InlineData("conversion")]
    [InlineData("extraction")]
    public async Task CommandsPreserveAnExistingTask(string kind)
    {
        await PrepareMediaAsync();
        var owner = _tasks.StartTask("Other operation");
        await DriverCommand(kind).ExecuteAsync(null);
        Assert.True(_tasks.IsTaskRunning);
        Assert.Same(owner, _tasks.CurrentTaskCancellationSource);
        Assert.False(owner.IsCancellationRequested);
        Assert.Equal("Other operation", _tasks.CurrentStatusText);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("custom")]
    [InlineData("conversion")]
    [InlineData("extraction")]
    public async Task FinishingAReplacedWorkerLeavesTheCurrentTaskRunning(string kind)
    {
        await PrepareMediaAsync();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _customization.Setup(service => service.AddDriversAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        _iso.Setup(service => service.ExtractIsoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        _images.Setup(service => service.ConvertImageAsync(It.IsAny<string>(), It.IsAny<ImageFormat>(), It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        _picker.Setup(service => service.PickFolder(It.IsAny<string>())).Returns(@"C:\Example\Drivers");
        var operation = DriverCommand(kind).ExecuteAsync(null);
        try
        {
            Assert.False(operation.IsCompleted);
            var replacement = _tasks.StartTask("Other operation");
            pending.SetResult(false);
            await operation;
            Assert.True(_tasks.IsTaskRunning);
            Assert.Same(replacement, _tasks.CurrentTaskCancellationSource);
            Assert.False(replacement.IsCancellationRequested);
        }
        finally { pending.TrySetResult(false); await operation; }
    }

    private IAsyncRelayCommand DriverCommand(string kind) => kind switch
    {
        "system" => _vm.Step3.ExtractAndAddSystemDriversCommand,
        "custom" => _vm.Step3.SelectAndAddCustomDriversCommand,
        "conversion" => _vm.ImageFormat.ConvertImageFormatCommand,
        "extraction" => _vm.Step1.StartIsoExtractionCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Fact]
    public async Task XmlFromBeforeReextractionCannotCompleteTheNewMediaAtTheSamePath()
    {
        await PrepareMediaAsync();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Path.Combine(_vm.Step1.WorkingDirectory, "autounattend.xml");
        _xml.Setup(service => service.GenerateFromCurrentSelectionsAsync(output, It.IsAny<IReadOnlyList<ConfigurationItem>?>())).Returns(pending.Task);
        var operation = _vm.Step2.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);
        try
        {
            Assert.False(operation.IsCompleted);
            await _vm.Step1.StartIsoExtractionCommand.ExecuteAsync(null);
            Assert.True(_vm.Step1.IsExtractionComplete);
        }
        finally { pending.TrySetResult(output); }
        await operation;
        AssertDependentEvidenceCleared();
    }

    public void Dispose() { _vm.Dispose(); _tasks.CompleteTask(); }
}
