using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WimUtilViewModelTests : IDisposable
{
    private readonly Mock<IOscdimgToolManager> _mockOscdimgToolManager = new();
    private readonly Mock<IIsoService> _mockIsoService = new();
    private readonly Mock<IWimImageService> _mockWimImageService = new();
    private readonly Mock<IWimCustomizationService> _mockWimCustomizationService = new();
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IAutounattendXmlGeneratorService> _mockXmlGeneratorService = new();
    private readonly Mock<ISelectedAppsProvider> _mockSelectedAppsProvider = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IDispatcherService> _mockDispatcherService = new();
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<IFilePickerService> _mockFilePickerService = new();
    private readonly Mock<IResourceService> _mockResourceService = new();

    private readonly WimUtilViewModel _sut;

    public WimUtilViewModelTests()
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

        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());

        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(a => a().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);

        _sut = new WimUtilViewModel(
            _mockOscdimgToolManager.Object,
            _mockIsoService.Object,
            _mockWimImageService.Object,
            _mockWimCustomizationService.Object,
            _mockTaskProgressService.Object,
            _mockDialogService.Object,
            _mockLogService.Object,
            _mockXmlGeneratorService.Object,
            _mockSelectedAppsProvider.Object,
            _mockLocalizationService.Object,
            _mockDispatcherService.Object,
            _mockProcessExecutor.Object,
            _mockFileSystemService.Object,
            _mockFilePickerService.Object,
            _mockResourceService.Object);
    }

    public void Dispose()
    {
        _sut.Dispose();
    }

    [Fact]
    public void NavigateToStepCommand_NullParameter_DoesNotChangeStep()
    {
        _sut.NavigateToStepCommand.Execute(null);

        _sut.Step1State.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void NavigateToStepCommand_EmptyString_DoesNotChangeStep()
    {
        _sut.NavigateToStepCommand.Execute("");

        _sut.Step1State.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void NavigateToStepCommand_NonNumericString_DoesNotChangeStep()
    {
        _sut.NavigateToStepCommand.Execute("abc");

        _sut.Step1State.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void NavigateToStepCommand_Step1_TogglesExpansion()
    {
        _sut.Step1State.IsExpanded.Should().BeTrue();

        _sut.NavigateToStepCommand.Execute("1");
        _sut.Step1State.IsExpanded.Should().BeFalse();

        _sut.NavigateToStepCommand.Execute("1");
        _sut.Step1State.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void NavigateToStepCommand_Step2WhenNotAvailable_DoesNotNavigate()
    {
        // Step2 is not available until extraction is complete
        _sut.NavigateToStepCommand.Execute("2");

        _sut.Step2State.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public async Task OnNavigatedToAsync_ChecksOscdimgAvailabilityAndUpdatesStep4()
    {
        _mockOscdimgToolManager
            .Setup(o => o.IsOscdimgAvailableAsync())
            .ReturnsAsync(true);

        await _sut.OnNavigatedToAsync();

        _sut.Step4.IsOscdimgAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task OnNavigatedToAsync_WhenOscdimgNotAvailable_Step4IsOscdimgAvailableIsFalse()
    {
        _mockOscdimgToolManager
            .Setup(o => o.IsOscdimgAvailableAsync())
            .ReturnsAsync(false);

        await _sut.OnNavigatedToAsync();

        _sut.Step4.IsOscdimgAvailable.Should().BeFalse();
    }

    [Fact]
    public void WhenStep1WorkingDirectoryChanges_PropagatesWorkingDirectoryToAllSubViewModels()
    {
        _sut.Step1.WorkingDirectory = "C:\\NewWorkDir";

        _sut.ImageFormat.WorkingDirectory.Should().Be("C:\\NewWorkDir");
        _sut.Step2.WorkingDirectory.Should().Be("C:\\NewWorkDir");
        _sut.Step3.WorkingDirectory.Should().Be("C:\\NewWorkDir");
        _sut.Step4.WorkingDirectory.Should().Be("C:\\NewWorkDir");
    }

    [Fact]
    public void Dispose_UnsubscribesFromSubVMPropertyChangedEvents()
    {
        _sut.Dispose();

        var expected = _sut.Step2.WorkingDirectory;
        _sut.Step1.WorkingDirectory = "C:\\AfterDispose";

        _sut.ImageFormat.WorkingDirectory.Should().Be(expected);
        _sut.Step2.WorkingDirectory.Should().Be(expected);
        _sut.Step3.WorkingDirectory.Should().Be(expected);
        _sut.Step4.WorkingDirectory.Should().Be(expected);
    }
}
