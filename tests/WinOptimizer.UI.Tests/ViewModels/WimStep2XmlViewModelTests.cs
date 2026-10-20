using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WimStep2XmlViewModelTests
{
    private readonly Mock<IAutounattendXmlGeneratorService> _mockXmlGeneratorService = new();
    private readonly Mock<IWimCustomizationService> _mockWimCustomizationService = new();
    private readonly Mock<ISelectedAppsProvider> _mockSelectedAppsProvider = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<IFilePickerService> _mockFilePickerService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IResourceService> _mockResourceService = new();

    private readonly WimStep2XmlViewModel _sut;

    public WimStep2XmlViewModelTests()
    {
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockFileSystemService
            .Setup(f => f.CombinePath(It.IsAny<string[]>()))
            .Returns((string[] parts) => string.Join("\\", parts));

        // Default: return non-empty list so generate doesn't show warning
        _mockSelectedAppsProvider
            .Setup(p => p.GetSelectedWindowsAppsAsync())
            .ReturnsAsync(new List<ConfigurationItem> { new ConfigurationItem { Id = "test" } });

        _sut = new WimStep2XmlViewModel(
            _mockXmlGeneratorService.Object,
            _mockWimCustomizationService.Object,
            _mockSelectedAppsProvider.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockFileSystemService.Object,
            _mockFilePickerService.Object,
            _mockLogService.Object,
            _mockResourceService.Object);
    }

    [Fact]
    public async Task GenerateWinOptimizerXmlCommand_WhenWorkingDirectoryEmpty_ShowsWarningAndReturns()
    {
        // WorkingDirectory defaults to empty
        await _sut.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);

        _mockDialogService.Verify(d => d.ShowWarningAsync(
            "WIMUtil_Msg_WorkingDirectoryRequired",
            It.IsAny<string>()), Times.Once);
        _mockXmlGeneratorService.Verify(s => s.GenerateFromCurrentSelectionsAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>?>()), Times.Never);
        _sut.IsXmlAdded.Should().BeFalse();
    }

    [Fact]
    public async Task SelectXmlFileCommand_WhenWorkingDirectoryEmpty_ShowsWarningAndReturns()
    {
        // WorkingDirectory defaults to empty
        await _sut.SelectXmlFileCommand.ExecuteAsync(null);

        _mockDialogService.Verify(d => d.ShowWarningAsync(
            "WIMUtil_Msg_WorkingDirectoryRequired",
            It.IsAny<string>()), Times.Once);
        _mockWimCustomizationService.Verify(s => s.AddXmlToImageAsync(
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _sut.IsXmlAdded.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateWinOptimizerXmlCommand_WhenUserCancels_DoesNotGenerate()
    {
        _sut.WorkingDirectory = "C:\\WorkDir";
        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false });

        await _sut.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);

        _mockXmlGeneratorService.Verify(s => s.GenerateFromCurrentSelectionsAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>?>()), Times.Never);
        _sut.IsXmlAdded.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateWinOptimizerXmlCommand_OnSuccess_SetsIsXmlAdded()
    {
        _sut.WorkingDirectory = "C:\\WorkDir";
        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });

        _mockXmlGeneratorService
            .Setup(s => s.GenerateFromCurrentSelectionsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>?>()))
            .ReturnsAsync("C:\\WorkDir\\autounattend.xml");

        await _sut.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);

        _sut.IsXmlAdded.Should().BeTrue();
        _sut.SelectedXmlPath.Should().Be("C:\\WorkDir\\autounattend.xml");
        _sut.GenerateWinOptimizerXmlCard.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateWinOptimizerXmlCommand_OnSuccess_ClearsOtherCardCompletions()
    {
        _sut.WorkingDirectory = "C:\\WorkDir";
        _sut.SelectXmlCard.IsComplete = true;

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });

        _mockXmlGeneratorService
            .Setup(s => s.GenerateFromCurrentSelectionsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>?>()))
            .ReturnsAsync("C:\\WorkDir\\autounattend.xml");

        await _sut.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);

        _sut.SelectXmlCard.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task GenerateWinOptimizerXmlCommand_OnException_SetsHasFailed()
    {
        _sut.WorkingDirectory = "C:\\WorkDir";
        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });

        _mockXmlGeneratorService
            .Setup(s => s.GenerateFromCurrentSelectionsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>?>()))
            .ThrowsAsync(new Exception("Generation failed"));

        await _sut.GenerateWinOptimizerXmlCommand.ExecuteAsync(null);

        _sut.GenerateWinOptimizerXmlCard.HasFailed.Should().BeTrue();
    }

    [Fact]
    public async Task SelectXmlFileCommand_ValidLocalXml_AddsToImageAndCompletesSelection()
    {
        var xmlPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(xmlPath, "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\" />");
            _sut.WorkingDirectory = "C:\\WorkDir";
            _sut.GenerateWinOptimizerXmlCard.IsComplete = true;
            _mockFilePickerService
                .Setup(f => f.PickFile(It.IsAny<string[]>(), It.IsAny<string?>()))
                .Returns(xmlPath);
            _mockWimCustomizationService
                .Setup(s => s.AddXmlToImageAsync(xmlPath, _sut.WorkingDirectory))
                .ReturnsAsync(true);

            await _sut.SelectXmlFileCommand.ExecuteAsync(null);

            _mockWimCustomizationService.Verify(s => s.AddXmlToImageAsync(xmlPath, "C:\\WorkDir"), Times.Once);
            _sut.IsXmlAdded.Should().BeTrue();
            _sut.SelectedXmlPath.Should().Be(xmlPath);
            _sut.SelectXmlCard.IsComplete.Should().BeTrue();
            _sut.GenerateWinOptimizerXmlCard.IsComplete.Should().BeFalse();
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    [Fact]
    public async Task SelectXmlFileCommand_WhenCancelled_DoesNothing()
    {
        _sut.WorkingDirectory = "C:\\WorkDir";
        _mockFilePickerService
            .Setup(f => f.PickFile(It.IsAny<string[]>(), It.IsAny<string?>()))
            .Returns((string?)null);

        await _sut.SelectXmlFileCommand.ExecuteAsync(null);

        _sut.IsXmlAdded.Should().BeFalse();
    }

    [Fact]
    public async Task SelectXmlFileCommand_WhenEmptyString_DoesNothing()
    {
        _sut.WorkingDirectory = "C:\\WorkDir";
        _mockFilePickerService
            .Setup(f => f.PickFile(It.IsAny<string[]>(), It.IsAny<string?>()))
            .Returns(string.Empty);

        await _sut.SelectXmlFileCommand.ExecuteAsync(null);

        _sut.IsXmlAdded.Should().BeFalse();
    }

    [Fact]
    public void ClearOtherXmlCardCompletions_ExceptGenerate_ClearsSelect()
    {
        _sut.GenerateWinOptimizerXmlCard.IsComplete = true;
        _sut.SelectXmlCard.IsComplete = true;

        _sut.ClearOtherXmlCardCompletions("generate");

        _sut.GenerateWinOptimizerXmlCard.IsComplete.Should().BeTrue();
        _sut.SelectXmlCard.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void ClearOtherXmlCardCompletions_ExceptSelect_ClearsGenerate()
    {
        _sut.GenerateWinOptimizerXmlCard.IsComplete = true;
        _sut.SelectXmlCard.IsComplete = true;

        _sut.ClearOtherXmlCardCompletions("select");

        _sut.GenerateWinOptimizerXmlCard.IsComplete.Should().BeFalse();
        _sut.SelectXmlCard.IsComplete.Should().BeTrue();
    }
}
