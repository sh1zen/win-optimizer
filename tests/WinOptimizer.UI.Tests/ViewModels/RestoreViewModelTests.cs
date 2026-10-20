using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Restore.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class RestoreViewModelTests : IDisposable
{
    private readonly Mock<IConfigurationService> _config = new();
    private readonly Mock<ISystemBackupService> _backup = new();
    private readonly Mock<ISystemRestoreService> _restore = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly RestoreViewModel _vm;
    private static SystemRestorePoint Point(uint id) => new(id, $"Point {id}", DateTimeOffset.UnixEpoch.AddHours(id));

    public RestoreViewModelTests()
    {
        _localization.Setup(l => l.GetString(It.IsAny<string>())).Returns((string key) => key);
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<SystemRestorePoint>());
        _tasks.Setup(t => t.StartTask(It.IsAny<string>(), true)).Returns(() => new CancellationTokenSource());
        _mode.Setup(m => m.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        _dispatcher.Setup(d => d.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => action());
        _vm = new(_config.Object, _backup.Object, _restore.Object, _dialogs.Object, _localization.Object,
            _tasks.Object, _mode.Object, Mock.Of<ILogService>(), _dispatcher.Object);
    }

    [Fact]
    public void Construction_DoesNotQueryWindowsOrShowAnEmptyInventory()
    {
        _restore.VerifyNoOtherCalls(); _backup.VerifyNoOtherCalls();
        _vm.HasLoaded.Should().BeFalse(); _vm.HasNoPoints.Should().BeFalse();
        _vm.PointCount.Should().BeEmpty();
    }

    [Fact]
    public void ConfigurationLabelsRefreshWithTheLanguage()
    {
        _localization.Setup(l => l.GetString("Button_Import")).Returns("Import Config");
        _localization.Setup(l => l.GetString("Button_Export")).Returns("Export Config");
        var notified = false;
        _vm.PropertyChanged += (_, _) => notified = true;
        _localization.Raise(l => l.LanguageChanged += null, EventArgs.Empty);
        _vm.ImportLabel.Should().Be("Import Config");
        _vm.ExportLabel.Should().Be("Export Config");
        notified.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_PublishesAllPointsNewestFirst_AndForwardsTheSamePointObjects()
    {
        var points = Enumerable.Range(1, 300).Select(id => Point((uint)id)).ToArray();
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(points);
        await _vm.RefreshRestorePointsCommand.ExecuteAsync(null);
        _vm.RestorePoints.Should().HaveCount(300);
        _vm.RestorePoints.Select(row => row.Point).Should().Equal(points.Reverse());
        _vm.RestorePoints[0].Point.Should().BeSameAs(points[^1]);
        _vm.HasLoaded.Should().BeTrue(); _vm.HasNoPoints.Should().BeFalse();
        _vm.RestorePoints[0].DeleteCommand.Should().BeSameAs(_vm.DeleteRestorePointCommand);
    }

    [Fact]
    public async Task EmptyInventoryAppearsOnlyAfterASuccessfulQuery()
    {
        var worker = new TaskCompletionSource<IReadOnlyList<SystemRestorePoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>())).Returns(worker.Task);
        var refresh = _vm.RefreshRestorePointsCommand.ExecuteAsync(null);
        _vm.IsBusy.Should().BeTrue(); _vm.HasNoPoints.Should().BeFalse();
        _vm.IsLoadingPoints.Should().BeTrue();
        worker.SetResult(Array.Empty<SystemRestorePoint>());
        await refresh;
        _vm.HasNoPoints.Should().BeTrue(); _vm.IsBusy.Should().BeFalse();
        _vm.IsLoadingPoints.Should().BeFalse();
    }

    [Fact]
    public async Task QueryFailure_IsReportedInsteadOfShowingNoRestorePoints()
    {
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Inventory unavailable"));
        await _vm.RefreshRestorePointsCommand.ExecuteAsync(null);
        _vm.Status.Should().Be("Inventory unavailable");
        _vm.IsLoadingPoints.Should().BeFalse();
        _vm.HasNoPoints.Should().BeFalse(); _vm.HasLoaded.Should().BeFalse();
        _dialogs.Verify(d => d.ShowErrorAsync("Inventory unavailable", It.IsAny<string>(), "OK"), Times.Once);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Delete_RequiresConfirmation_AndRemovesOnlyTheSuccessfulTarget(bool confirmed)
    {
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Point(1), Point(2) });
        await _vm.RefreshRestorePointsCommand.ExecuteAsync(null);
        var selected = _vm.RestorePoints[0];
        _dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = confirmed });
        _restore.Setup(r => r.DeleteRestorePointAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(OperationResult.Succeeded());
        await _vm.DeleteRestorePointCommand.ExecuteAsync(selected);
        _restore.Verify(r => r.DeleteRestorePointAsync(2, It.IsAny<CancellationToken>()), confirmed ? Times.Once : Times.Never);
        _vm.RestorePoints.Should().HaveCount(confirmed ? 1 : 2);
        _vm.RestorePoints.Should().Contain(row => row.Point.SequenceNumber == 1);
        _tasks.Verify(t => t.CompleteTask(), confirmed ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task FailedDelete_PreservesTheRow_AndCompletesGlobalProgress()
    {
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Point(1) });
        await _vm.RefreshRestorePointsCommand.ExecuteAsync(null);
        var row = _vm.RestorePoints[0];
        _dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _restore.Setup(r => r.DeleteRestorePointAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(OperationResult.Failed("Point is in use"));
        await _vm.DeleteRestorePointCommand.ExecuteAsync(row);
        _vm.RestorePoints.Should().ContainSingle().Which.Should().BeSameAs(row);
        _vm.Status.Should().Be("Point is in use"); _vm.IsBusy.Should().BeFalse();
        _tasks.Verify(t => t.CompleteTask(), Times.Once);
    }

    [Fact]
    public async Task AnotherGlobalTaskStartingDuringConfirmation_IsNotReplaced()
    {
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Point(1) });
        await _vm.RefreshRestorePointsCommand.ExecuteAsync(null);
        _dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).Returns(() =>
        {
            _tasks.SetupGet(t => t.IsTaskRunning).Returns(true);
            return Task.FromResult(new ConfirmationResponse { Confirmed = true });
        });
        await _vm.DeleteRestorePointCommand.ExecuteAsync(_vm.RestorePoints[0]);
        _restore.Verify(r => r.DeleteRestorePointAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
        _tasks.Verify(t => t.StartTask(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _tasks.Verify(t => t.CompleteTask(), Times.Never);
        _vm.RestorePoints.Should().ContainSingle();
    }

    [Fact]
    public async Task TestMode_AllowsInventoryAndExport_ButBlocksEverySystemChange()
    {
        _mode.SetupGet(m => m.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mode.Raise(m => m.ModeChanged += null, EventArgs.Empty);
        _vm.RefreshRestorePointsCommand.CanExecute(null).Should().BeTrue();
        _vm.ExportConfigCommand.CanExecute(null).Should().BeTrue();
        _vm.ImportConfigCommand.CanExecute(null).Should().BeFalse();
        _vm.RestoreDefaultsCommand.CanExecute(null).Should().BeFalse();
        _vm.CreateRestorePointCommand.CanExecute(null).Should().BeFalse();
        _vm.DeleteRestorePointCommand.CanExecute(null).Should().BeFalse();
        await _vm.ImportConfigCommand.ExecuteAsync(null);
        await _vm.RestoreDefaultsCommand.ExecuteAsync(null);
        await _vm.CreateRestorePointCommand.ExecuteAsync(null);
        await _vm.DeleteRestorePointCommand.ExecuteAsync(null);
        await _vm.ExportConfigCommand.ExecuteAsync(null);
        _vm.Status.Should().NotBe("Restore_Deleted");
        _config.Verify(c => c.ExportConfigurationAsync(), Times.Once);
        _config.Verify(c => c.ImportConfigurationAsync(), Times.Never);
        _config.Verify(c => c.RestoreWindowsDefaultsAsync(), Times.Never);
        _backup.VerifyNoOtherCalls(); _restore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConfigurationCommands_UseTheExistingConfigurationService()
    {
        await _vm.ImportConfigCommand.ExecuteAsync(null);
        await _vm.ExportConfigCommand.ExecuteAsync(null);
        await _vm.RestoreDefaultsCommand.ExecuteAsync(null);
        _config.Verify(c => c.ImportConfigurationAsync(), Times.Once);
        _config.Verify(c => c.ExportConfigurationAsync(), Times.Once);
        _config.Verify(c => c.RestoreWindowsDefaultsAsync(), Times.Once);
    }

    [Fact]
    public async Task Create_VerifiesCreationBeforeRefreshingTheInventory()
    {
        _backup.Setup(b => b.CreateRestorePointAsync(null, It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackupResult.CreateSuccess(restorePointCreated: true));
        await _vm.CreateRestorePointCommand.ExecuteAsync(null);
        _vm.Status.Should().Be("Settings_RestorePoint_Success");
        _restore.Verify(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(t => t.CompleteTask(), Times.Once);
    }

    [Fact]
    public async Task UnverifiedCreation_ShowsTheExistingWarning_WithoutRefreshingInventory()
    {
        _backup.Setup(b => b.CreateRestorePointAsync(null, It.IsAny<IProgress<TaskProgressDetail>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackupResult.CreateFailure("Could not verify the restore point"));
        await _vm.CreateRestorePointCommand.ExecuteAsync(null);
        _vm.Status.Should().Contain("Could not verify");
        _dialogs.Verify(d => d.ShowWarningAsync(_vm.Status, "Warning", "OK"), Times.Once);
        _restore.VerifyNoOtherCalls();
        _tasks.Verify(t => t.CompleteTask(), Times.Once);
        _vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_CancelsInventoryAndPreventsLatePublication()
    {
        var worker = new TaskCompletionSource<IReadOnlyList<SystemRestorePoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        _restore.Setup(r => r.GetRestorePointsAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => token = ct).Returns(worker.Task);
        var refresh = _vm.RefreshRestorePointsCommand.ExecuteAsync(null);
        _vm.Dispose();
        token.IsCancellationRequested.Should().BeTrue();
        worker.SetResult(new[] { Point(1) });
        await refresh;
        _vm.RestorePoints.Should().BeEmpty();
        _vm.HasLoaded.Should().BeFalse();
        _dialogs.VerifyNoOtherCalls();
    }

    public void Dispose() => _vm.Dispose();
}
