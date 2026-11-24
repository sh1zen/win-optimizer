using System.Xml.Linq;
using FluentAssertions;
using Microsoft.UI.Xaml.Controls;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.ViewModels;
using WinOptimizer.UI.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class UpdateCheckViewModelTests : IDisposable
{
    private readonly Mock<IVersionService> _mockVersionService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IBackgroundUpdateCheckService> _backgroundUpdates = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();

    private readonly Mock<IApplicationCloseService> _close = new();
    private readonly UpdateCheckViewModel _sut;

    public UpdateCheckViewModelTests()
    {
        _dispatcher.Setup(service => service.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => action());
        _backgroundUpdates.Setup(service => service.RunAsync(It.IsAny<Action<VersionInfo>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Default localization returns null so fallbacks are used
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => null!);

        _sut = new UpdateCheckViewModel(
            _mockVersionService.Object,
            _mockLocalizationService.Object,
            _mockLogService.Object, _backgroundUpdates.Object, _dispatcher.Object, _close.Object);
    }

    public void Dispose()
    {
        _sut.Dispose();
    }

    [Fact]
    public void TitleBarMenuUsesTheSharedUpdateCommandAndLabel()
    {
        using var source = typeof(UpdateCheckViewModelTests).Assembly.GetManifestResourceStream("MainWindow.xaml");
        var document = XDocument.Load(source!);
        XNamespace controls = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = Assert.Single(document.Descendants(controls + "Button"),
            element => (string?)element.Attribute(names + "Name") == "UpdatesButton");
        var item = Assert.Single(button.Descendants(controls + "Button"));
        Assert.Equal("{x:Bind ViewModel.UpdateCheck.CheckForUpdatesCommand, Mode=OneWay}", (string?)item.Attribute("Command"));
        var label = "{x:Bind ViewModel.UpdateCheck.CheckForUpdatesLabel, Mode=OneWay}";
        Assert.Equal(label, (string?)Assert.Single(item.Elements(controls + "TextBlock")).Attribute("Text"));
        Assert.Equal(label, (string?)button.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void MenuShowsTheInstalledVersionBeforeTheFirstCheck()
    {
        _mockVersionService.Setup(service => service.GetCurrentVersion()).Returns(new VersionInfo { Version = "v25.05.01" });

        Assert.Equal("Current Version: v25.05.01", _sut.CurrentVersionText);
        Assert.Equal("Latest Version: —", _sut.LatestVersionText);
        Assert.Equal("Latest release has yet to be verified.", _sut.UpdateStatusText);
        _mockVersionService.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_UpdateAvailable_ShowsInfoBar()
    {
        _mockVersionService
            .Setup(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionInfo { Version = "v25.06.01", IsUpdateAvailable = true });
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);

        _sut.IsUpdateInfoBarOpen.Should().BeTrue();
        _sut.UpdateInfoBarSeverity.Should().Be(InfoBarSeverity.Success);
        _sut.IsUpdateActionButtonVisible.Should().BeTrue();
        _sut.UpdateInfoBarTitle.Should().NotBeNullOrEmpty();
        _sut.UpdateInfoBarMessage.Should().Contain("v25.05.01");
        _sut.UpdateInfoBarMessage.Should().Contain("v25.06.01");
        _sut.DismissUpdateInfoBar();
        Assert.Equal("Latest Version: v25.06.01", _sut.LatestVersionText);
        Assert.Equal("Update Available", _sut.UpdateStatusText);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_NoUpdateAvailable_ShowsInfoBarWithNoUpdates()
    {
        _mockVersionService
            .Setup(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionInfo { Version = "v25.05.01", IsUpdateAvailable = false });
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);

        _sut.IsUpdateInfoBarOpen.Should().BeTrue();
        _sut.UpdateInfoBarSeverity.Should().Be(InfoBarSeverity.Success);
        _sut.IsUpdateActionButtonVisible.Should().BeFalse();
        _sut.UpdateInfoBarTitle.Should().Contain("No Updates");
        Assert.Equal("Latest Version: v25.05.01", _sut.LatestVersionText);
        Assert.Equal("No Updates Available", _sut.UpdateStatusText);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_NullResult_ShowsAnError()
    {
        _mockVersionService
            .Setup(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((VersionInfo?)null!);
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);

        _sut.IsUpdateInfoBarOpen.Should().BeTrue();
        _sut.IsUpdateActionButtonVisible.Should().BeFalse();
        _sut.UpdateInfoBarSeverity.Should().Be(InfoBarSeverity.Error);
    }

    [Fact]
    public async Task EmptyReleaseMetadataIsUnavailableRatherThanUpToDate()
    {
        _mockVersionService.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new VersionInfo());
        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(InfoBarSeverity.Error, _sut.UpdateInfoBarSeverity);
        Assert.False(_sut.IsUpdateActionButtonVisible);
    }

    [Fact]
    public async Task RelaunchUsesTheSharedConfirmationAndDrainWorkflow()
    {
        Action? install = null;
        _close.Setup(service => service.CheckOperationsAndRestartAsync(It.IsAny<Action>()))
            .Callback<Action>(action => install = action).ReturnsAsync(OperationResult.Failed("User cancelled application close"));
        await _sut.RelaunchCommand.ExecuteAsync(null);
        Assert.NotNull(install);
        _mockVersionService.Verify(service => service.LaunchInstallerAndRestart(), Times.Never);
        install();
        _mockVersionService.Verify(service => service.LaunchInstallerAndRestart(), Times.Once);
        _close.Verify(service => service.CheckOperationsAndRestartAsync(It.IsAny<Action>()), Times.Once);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_ServiceThrows_ShowsError()
    {
        _mockVersionService
            .Setup(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Network error"));

        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);

        _sut.IsUpdateInfoBarOpen.Should().BeTrue();
        _sut.UpdateInfoBarSeverity.Should().Be(InfoBarSeverity.Error);
        _sut.IsUpdateActionButtonVisible.Should().BeFalse();
        _sut.UpdateInfoBarMessage.Should().Contain("Network error");
        Assert.Equal("Update Check Error", _sut.UpdateStatusText);
    }

    [Fact]
    public async Task CheckForUpdatesCommand_SetsIsUpdateCheckInProgress_DuringExecution()
    {
        var tcs = new TaskCompletionSource<VersionInfo>();
        _mockVersionService
            .Setup(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        var commandTask = _sut.CheckForUpdatesCommand.ExecuteAsync(null);
        _sut.IsUpdateCheckInProgress.Should().BeTrue();
        Assert.Equal("Checking for updates…", _sut.UpdateStatusText);

        tcs.SetResult(new VersionInfo { Version = "v25.05.01", IsUpdateAvailable = false });
        await commandTask;

        _sut.IsUpdateCheckInProgress.Should().BeFalse();
    }

    [Fact]
    public async Task CheckForUpdatesCommand_AlreadyInProgress_DoesNotStartAnother()
    {
        var tcs = new TaskCompletionSource<VersionInfo>();
        _mockVersionService
            .Setup(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        // Start first check
        var task1 = _sut.CheckForUpdatesCommand.ExecuteAsync(null);

        // Try to start another check while the first is in progress
        var task2 = _sut.CheckForUpdatesCommand.ExecuteAsync(null);
        await task2; // This should complete immediately

        // The version service should only have been called once
        _mockVersionService.Verify(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Once);

        tcs.SetResult(new VersionInfo { Version = "v25.05.01", IsUpdateAvailable = false });
        await task1;
    }

    [Fact]
    public async Task ManualChecks_RemainImmediateAndIndependentOfTheDailySchedule()
    {
        _mockVersionService.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionInfo { IsUpdateAvailable = false });
        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);
        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);
        _mockVersionService.Verify(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _backgroundUpdates.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BackgroundVersionIsDispatchedToTheMenuAndOnlyUpdatesOpenTheInfoBar(bool updateAvailable)
    {
        Action<VersionInfo>? report = null;
        Action? publish = null;
        _backgroundUpdates.Setup(service => service.RunAsync(It.IsAny<Action<VersionInfo>>(), It.IsAny<CancellationToken>()))
            .Callback<Action<VersionInfo>, CancellationToken>((callback, _) => report = callback).Returns(Task.CompletedTask);
        _dispatcher.Setup(service => service.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => publish = action);
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        await _sut.StartBackgroundUpdateChecksAsync();
        var version = updateAvailable ? "v25.06.01" : "v25.05.01";
        report!(new VersionInfo { Version = version, IsUpdateAvailable = updateAvailable });
        _sut.IsUpdateInfoBarOpen.Should().BeFalse();
        Assert.Equal("Latest Version: —", _sut.LatestVersionText);
        publish.Should().NotBeNull();
        publish!();

        Assert.Equal(updateAvailable, _sut.IsUpdateInfoBarOpen);
        Assert.Equal(updateAvailable, _sut.IsUpdateActionButtonVisible);
        Assert.Equal($"Latest Version: {version}", _sut.LatestVersionText);
        Assert.Equal(updateAvailable ? "Update Available" : "No Updates Available", _sut.UpdateStatusText);
    }

    [Fact]
    public async Task BackgroundChecks_StartOnlyOnce_AndDoNotInvokeManualVersionCheck()
    {
        await _sut.StartBackgroundUpdateChecksAsync();
        await _sut.StartBackgroundUpdateChecksAsync();
        _backgroundUpdates.Verify(service => service.RunAsync(It.IsAny<Action<VersionInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockVersionService.VerifyNoOtherCalls();
        _sut.IsUpdateInfoBarOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_CancelsBackgroundChecks_AndIgnoresLateNotifications()
    {
        Action<VersionInfo>? report = null;
        CancellationToken cancellation = default;
        _backgroundUpdates.Setup(service => service.RunAsync(It.IsAny<Action<VersionInfo>>(), It.IsAny<CancellationToken>()))
            .Callback<Action<VersionInfo>, CancellationToken>((callback, token) => { report = callback; cancellation = token; })
            .Returns(Task.CompletedTask);
        await _sut.StartBackgroundUpdateChecksAsync();
        _sut.Dispose();
        cancellation.IsCancellationRequested.Should().BeTrue();
        report!(new VersionInfo { IsUpdateAvailable = true });
        await _sut.StartBackgroundUpdateChecksAsync();
        _backgroundUpdates.Verify(service => service.RunAsync(It.IsAny<Action<VersionInfo>>(), It.IsAny<CancellationToken>()), Times.Once);
        _sut.IsUpdateInfoBarOpen.Should().BeFalse();
    }

    [Fact]
    public async Task InstallUpdateCommand_Success_ShowsRelaunchButton()
    {
        _mockVersionService
            .Setup(v => v.DownloadAndInstallUpdateAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // First get an update available state
        _sut.IsUpdateActionButtonVisible = true;

        await _sut.InstallUpdateCommand.ExecuteAsync(null);

        _sut.IsUpdateActionButtonVisible.Should().BeFalse();
        _sut.IsRelaunchButtonVisible.Should().BeTrue();
        _sut.UpdateInfoBarSeverity.Should().Be(InfoBarSeverity.Success);
        _sut.UpdateInfoBarTitle.Should().Contain("Update Ready");
        _sut.UpdateInfoBarMessage.Should().Contain("Relaunch");
    }

    [Fact]
    public async Task InstallUpdateCommand_Failure_ShowsErrorMessage()
    {
        _sut.IsUpdateInfoBarOpen = true;
        _mockVersionService
            .Setup(v => v.DownloadAndInstallUpdateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("Download failed"));

        await _sut.InstallUpdateCommand.ExecuteAsync(null);
        _mockLocalizationService.Setup(l => l.GetString("Dialog_Update_Status_Error")).Returns("Download error after language change: {0}");
        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

        _sut.UpdateInfoBarSeverity.Should().Be(InfoBarSeverity.Error);
        _sut.UpdateInfoBarMessage.Should().Be("Download error after language change: Download failed");
        _sut.IsUpdateActionButtonVisible.Should().BeFalse();
        _sut.IsRelaunchButtonVisible.Should().BeFalse();
    }

    [Fact]
    public async Task InstallUpdateCommand_ShowsDownloadingState_DuringExecution()
    {
        var tcs = new TaskCompletionSource();
        _mockVersionService
            .Setup(v => v.DownloadAndInstallUpdateAsync(It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        _sut.IsUpdateActionButtonVisible = true;

        var commandTask = _sut.InstallUpdateCommand.ExecuteAsync(null);

        // During download: action button hidden, relaunch not yet visible
        _sut.IsUpdateActionButtonVisible.Should().BeFalse();
        _sut.IsRelaunchButtonVisible.Should().BeFalse();
        _sut.UpdateInfoBarTitle.Should().Contain("Downloading");

        tcs.SetResult();
        await commandTask;

        // After download: relaunch button visible
        _sut.IsRelaunchButtonVisible.Should().BeTrue();
        _sut.UpdateInfoBarTitle.Should().Contain("Update Ready");
    }

    [Fact]
    public void DismissUpdateInfoBar_ClosesInfoBar()
    {
        _sut.IsUpdateInfoBarOpen = true;

        _sut.DismissUpdateInfoBar();

        _sut.IsUpdateInfoBarOpen.Should().BeFalse();
    }

    [Fact]
    public void LanguageChanged_NotifiesUpdateActionLabels()
    {
        var changedProperties = new List<string>();
        _sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);
        _mockLocalizationService.Setup(l => l.GetString("Menu_CheckForUpdates")).Returns("Controlla aggiornamenti");
        _mockLocalizationService.Setup(l => l.GetString("Dialog_Update_CurrentVersion")).Returns("Versione attuale:");
        _mockLocalizationService.Setup(l => l.GetString("Dialog_Update_LatestVersion")).Returns("Ultima versione:");
        _mockLocalizationService.Setup(l => l.GetString("Menu_Update_NotChecked")).Returns("Ultima versione ancora da verificare.");

        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

        changedProperties.Should().Contain(nameof(_sut.CheckForUpdatesLabel));
        _sut.CheckForUpdatesLabel.Should().Be("Controlla aggiornamenti");
        changedProperties.Should().Contain([nameof(_sut.CurrentVersionText), nameof(_sut.LatestVersionText), nameof(_sut.UpdateStatusText)]);
        Assert.Equal("Versione attuale: —", _sut.CurrentVersionText);
        Assert.Equal("Ultima versione: —", _sut.LatestVersionText);
        Assert.Equal("Ultima versione ancora da verificare.", _sut.UpdateStatusText);
        changedProperties.Should().Contain(nameof(_sut.InstallNowButtonText));
        changedProperties.Should().Contain(nameof(_sut.RelaunchButtonText));
    }

    [Fact]
    public async Task LanguageChanged_WhenInfoBarOpen_RefreshesInfoBarText()
    {
        // Set up an update available state
        _mockVersionService
            .Setup(v => v.CheckForUpdateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionInfo { Version = "v25.06.01", IsUpdateAvailable = true });
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        await _sut.CheckForUpdatesCommand.ExecuteAsync(null);
        var originalTitle = _sut.UpdateInfoBarTitle;

        // Now simulate language change with a new localization value
        _mockLocalizationService
            .Setup(l => l.GetString("Dialog_Update_Title"))
            .Returns("Mise a jour disponible");

        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

        _sut.UpdateInfoBarTitle.Should().Be("Mise a jour disponible");
    }

    [Fact]
    public void LanguageChanged_WhenInfoBarClosed_DoesNotRefreshInfoBarText()
    {
        _sut.IsUpdateInfoBarOpen = false;

        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

        // Title should remain as initialized (empty string)
        _sut.UpdateInfoBarTitle.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_UnsubscribesFromLanguageChanged()
    {
        var sut = new UpdateCheckViewModel(
            _mockVersionService.Object,
            _mockLocalizationService.Object,
            _mockLogService.Object, _backgroundUpdates.Object, _dispatcher.Object, _close.Object);

        sut.Dispose();

        // After dispose, raising language changed should not cause property notifications
        var changedProperties = new List<string>();
        sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

        changedProperties.Should().NotContain(nameof(sut.InstallNowButtonText));
    }
}
