using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class AutounattendGeneratorViewModelTests
{
    private readonly Mock<IAutounattendXmlGeneratorService> _xmlGeneratorService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<ILocalizationService> _localizationService = new();
    private readonly Mock<ILogService> _logService = new();

    // Dependencies for WindowsAppsViewModel
    private readonly Mock<IWindowsAppsService> _windowsAppsService = new();
    private readonly Mock<IAppUninstallWorkflow> _uninstallWorkflow = new();
    private readonly Mock<ITaskProgressService> _progressService = new();
    private readonly Mock<ILogService> _winLogService = new();
    private readonly Mock<IDialogService> _winDialogService = new();
    private readonly Mock<ILocalizationService> _winLocalizationService = new();
    private readonly Mock<IDispatcherService> _dispatcherService = new();
    private readonly Mock<IThemeService> _themeService = new();

    public AutounattendGeneratorViewModelTests()
    {
        _localizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);
        _winLocalizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);
        _dispatcherService.Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _dispatcherService.Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);
        _dispatcherService.Setup(d => d.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);
    }

    private WindowsAppsViewModel CreateWindowsAppsVm() => new(
        _windowsAppsService.Object,
        _uninstallWorkflow.Object,
        _winLogService.Object,
        _winDialogService.Object,
        _winLocalizationService.Object,
        _dispatcherService.Object,
        _themeService.Object,
        iconResolver: Mock.Of<IAppIconResolver>());

    private AutounattendGeneratorViewModel CreateSut(WindowsAppsViewModel? windowsAppsVm = null)
    {
        var winVm = windowsAppsVm ?? CreateWindowsAppsVm();
        return new AutounattendGeneratorViewModel(
            _xmlGeneratorService.Object,
            _dialogService.Object,
            _localizationService.Object,
            _logService.Object,
            winVm, _progressService.Object);
    }

    [Fact]
    public void GenerateCardHeader_WhenLocalizationReturnsNull_UsesFallback()
    {
        _localizationService.Setup(l => l.GetString("Dialog_GenerateXml"))
            .Returns((string)null!);

        var sut = CreateSut();

        sut.GenerateCardHeader.Should().Be("Generate Autounattend XML");
    }

    [Fact]
    public void GenerateButtonText_WhenLocalizationReturnsNull_UsesFallback()
    {
        _localizationService.Setup(l => l.GetString("WIMUtil_ButtonGenerate"))
            .Returns((string)null!);

        var sut = CreateSut();

        sut.GenerateButtonText.Should().Be("Generate");
    }

    [Fact]
    public void NavigateToWimUtilRequested_CanBeSubscribedTo()
    {
        var sut = CreateSut();
        bool eventRaised = false;

        sut.NavigateToWimUtilRequested += (_, _) => eventRaised = true;

        eventRaised.Should().BeFalse();
    }

    [Fact]
    public void SetMainWindow_DoesNotThrow()
    {
        var sut = CreateSut();

        // SetMainWindow requires a Microsoft.UI.Xaml.Window which we can't easily mock in unit tests,
        // but we can test with null to verify it does not throw
        var act = () => sut.SetMainWindow(null!);

        act.Should().NotThrow();
    }

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
            It.IsAny<string>(), It.IsAny<IReadOnlyList<WinOptimizer.Core.Features.Common.Models.ConfigurationItem>>()), Times.Never);
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
            It.IsAny<string>(), It.IsAny<IReadOnlyList<WinOptimizer.Core.Features.Common.Models.ConfigurationItem>>()), Times.Never);
    }
}
