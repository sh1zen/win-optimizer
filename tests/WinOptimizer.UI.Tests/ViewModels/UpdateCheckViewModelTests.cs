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
            _mockLogService.Object, _backgroundUpdates.Object, _dispatcher.Object);
    }

    public void Dispose()
    {
        _sut.Dispose();
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
    }

    [Fact]
    public async Task CheckForUpdatesCommand_NullResult_ShowsNoUpdates()
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

    [Fact]
    public async Task BackgroundUpdateAvailable_IsDispatchedBeforeShowingInfoBar()
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
        report!(new VersionInfo { Version = "v25.06.01", IsUpdateAvailable = true });
        _sut.IsUpdateInfoBarOpen.Should().BeFalse();
        publish.Should().NotBeNull();
        publish!();

        _sut.IsUpdateInfoBarOpen.Should().BeTrue();
        _sut.IsUpdateActionButtonVisible.Should().BeTrue();
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
        _mockVersionService
            .Setup(v => v.DownloadAndInstallUpdateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Download failed"));

        await _sut.InstallUpdateCommand.ExecuteAsync(null);

        _sut.UpdateInfoBarSeverity.Should().Be(InfoBarSeverity.Error);
        _sut.UpdateInfoBarMessage.Should().Contain("Download failed");
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
    public void LanguageChanged_NotifiesInstallNowButtonText()
    {
        var changedProperties = new List<string>();
        _sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

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
            _mockLogService.Object, _backgroundUpdates.Object, _dispatcher.Object);

        sut.Dispose();

        // After dispose, raising language changed should not cause property notifications
        var changedProperties = new List<string>();
        sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

        changedProperties.Should().NotContain(nameof(sut.InstallNowButtonText));
    }
}
