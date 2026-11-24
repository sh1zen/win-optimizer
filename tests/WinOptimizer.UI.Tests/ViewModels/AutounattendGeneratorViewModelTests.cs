using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class AutounattendGeneratorViewModelTests
{
    private readonly Mock<IAutounattendXmlGeneratorService> _xmlGeneratorService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<ILocalizationService> _localizationService = new();
    private readonly Mock<ILogService> _logService = new();

    private readonly Mock<ISelectedAppsProvider> _selectedAppsProvider = new();
    private readonly Mock<ITaskProgressService> _progressService = new();

    public AutounattendGeneratorViewModelTests()
    {
        _localizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);
    }

    private AutounattendGeneratorViewModel CreateSut() => new(
        _xmlGeneratorService.Object,
        _dialogService.Object,
        _localizationService.Object,
        _logService.Object,
        _selectedAppsProvider.Object,
        _progressService.Object);

    [Fact]
    public async Task GenerateAutounattendXmlCommand_WhenUserCancelsConfirmation_DoesNotGenerate()
    {
        _dialogService.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = false });

        var sut = CreateSut();
        sut.SetMainWindow(null!);

        // Execute the command
        await sut.GenerateAutounattendXmlCommand.ExecuteAsync(null);

        _xmlGeneratorService.Verify(s => s.GenerateFromCurrentSelectionsAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>>()), Times.Never);
        _selectedAppsProvider.Verify(p => p.GetSelectedWindowsAppsAsync(), Times.Never);
    }

    [Fact]
    public async Task GenerateAutounattendXmlCommand_WhenMainWindowIsNull_ReturnsAfterConfirmation()
    {
        _dialogService.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });

        var sut = CreateSut();
        // Do not set main window (it defaults to null)

        await sut.GenerateAutounattendXmlCommand.ExecuteAsync(null);

        _xmlGeneratorService.Verify(s => s.GenerateFromCurrentSelectionsAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<ConfigurationItem>>()), Times.Never);
        _selectedAppsProvider.Verify(p => p.GetSelectedWindowsAppsAsync(), Times.Never);
    }
}
