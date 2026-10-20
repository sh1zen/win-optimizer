using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.ViewModels;
using Xunit;
using WinOptimizer.UI.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Enums;

namespace WinOptimizer.UI.Tests.ViewModels;

public class ApplicationInfoViewModelTests
{
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IVersionService> _mockVersionService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IProcessExecutor> _processExecutor = new();
    private readonly Mock<IDialogService> _mockDialogService = new();

    public ApplicationInfoViewModelTests()
    {
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);
    }

    private ApplicationInfoViewModel CreateViewModel()
    {
        return new ApplicationInfoViewModel(
            _mockLocalization.Object,
            _mockVersionService.Object,
            _mockLogService.Object,
            _mockDialogService.Object, _processExecutor.Object,
            new UpdateCheckViewModel(_mockVersionService.Object, _mockLocalization.Object, _mockLogService.Object,
                Mock.Of<IBackgroundUpdateCheckService>(), Mock.Of<IDispatcherService>()));
    }

    [Theory]
    [InlineData(false, "https://github.com/sh1zen/win-optimizer#readme")]
    [InlineData(true, "https://github.com/sh1zen/win-optimizer/issues")]
    public async Task HelpCommandsOpenTheirExistingDestinations(bool reportBug, string url)
    {
        using var vm = CreateViewModel();
        await (reportBug ? vm.ReportBugCommand : vm.OpenDocsCommand).ExecuteAsync(null);
        _processExecutor.Verify(process => process.ShellExecuteAsync(url, null, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelpCommandFailuresAreLogged(bool reportBug)
    {
        _processExecutor.Setup(process => process.ShellExecuteAsync(It.IsAny<string>(), null, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("browser unavailable"));
        using var vm = CreateViewModel();
        await (reportBug ? vm.ReportBugCommand : vm.OpenDocsCommand).ExecuteAsync(null);
        _mockLogService.Verify(log => log.LogError(It.Is<string>(message => message.Contains("browser unavailable")), It.IsAny<IOException>()), Times.Once);
    }

    [Fact]
    public async Task SupportCommandOpensTheNormalSponsorsDialog()
    {
        using var vm = CreateViewModel();
        await vm.SupportWinOptimizerCommand.ExecuteAsync(null);
        _mockDialogService.Verify(dialog => dialog.ShowSponsorsDialogAsync(SponsorsDialogMode.Normal), Times.Once);
    }

    [Fact]
    public async Task ManualUpdateCheckUsesTheSharedUpdateWorkflow()
    {
        _mockVersionService.Setup(service => service.GetCurrentVersion()).Returns(new VersionInfo { Version = "v26.10.19" });
        _mockVersionService.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new VersionInfo());
        using var vm = CreateViewModel();
        _mockVersionService.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Never);
        await vm.Updates.CheckForUpdatesCommand.ExecuteAsync(null);
        _mockVersionService.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);
        vm.Updates.IsUpdateInfoBarOpen.Should().BeTrue();
    }

    [Fact]
    public void Dispose_UnsubscribesFromLanguageChanged()
    {
        var vm = CreateViewModel();
        vm.Dispose();

        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalization.Raise(l => l.LanguageChanged += null, EventArgs.Empty);

        changedProperties.Should().BeEmpty();
    }
}
