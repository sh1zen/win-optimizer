using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks real servicing commands against fake dialogs and a fake service, never the local OS.</summary>
public sealed class WindowsStatusViewModelTests : IDisposable
{
    private readonly Mock<IWindowsServicingService> _service = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly List<CancellationTokenSource> _sources = [];
    private CancellationTokenSource? _current;

    public WindowsStatusViewModelTests()
    {
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        _tasks.SetupGet(service => service.CurrentTaskCancellationSource).Returns(() => _current);
        _tasks.Setup(service => service.StartTask(It.IsAny<string>(), true, false, true)).Returns(() =>
        { _current = new(); _sources.Add(_current); return _current; });
        _tasks.Setup(service => service.CompleteTask()).Callback(() => _current = null);
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _service.Setup(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WindowsServicingRequest request, IProgress<WindowsServicingProgress>? _, CancellationToken _) =>
                new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.ReviewFileResults, WindowsImageHealth.Healthy,
                    [new(WindowsServicingStep.RepairFiles, true, 0, WindowsImageHealth.Unknown, false, "SFC native result")]));
    }

    private WindowsStatusViewModel Create(ScanCancellationNotifications? notifications = null) => new(_service.Object, _dialogs.Object, Mock.Of<IFilePickerService>(),
        _localization.Object, _mode.Object, new ExtensionTestDispatcher(), _tasks.Object, notifications);

    [Fact]
    public void OpeningTheToolDoesNotRunCommandsOrEnableResetBase()
    {
        using var vm = Create(); Assert.False(vm.ResetBase); Assert.False(vm.HasReport); Assert.Empty(vm.SourcePath);
        Assert.Equal("WindowsStatus_Title", vm.SectionTitle); _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RepairPassesExplicitSourceOptionsAndPreservesNativeOutput()
    {
        using var vm = Create(); vm.SourcePath = @"D:\sources\install.wim"; vm.WimIndex = 6; vm.LimitAccess = true;
        await vm.RunCommand.ExecuteAsync("Repair");
        _service.Verify(service => service.RunAsync(It.Is<WindowsServicingRequest>(request =>
            request.Action == WindowsServicingAction.Repair && request.SourcePath == @"D:\sources\install.wim" && request.WimIndex == 6 && request.LimitAccess && !request.ResetBase),
            It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("SFC native result", Assert.Single(vm.Report!.Steps).Output);
        Assert.Equal("WindowsStatus_StateReviewFileResults", vm.ReportSummary);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, vm.ReportSeverity);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResetBaseRequiresConfirmationAndAnInitiallyUncheckedAcknowledgement(bool confirmed, bool acknowledged)
    {
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = confirmed, CheckboxChecked = acknowledged });
        using var vm = Create(); vm.ResetBase = true;
        await vm.RunCommand.ExecuteAsync("Cleanup");
        _dialogs.Verify(dialog => dialog.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request =>
            !request.CheckboxInitiallyChecked && request.CheckboxText != null && request.Message == "WindowsStatus_ConfirmResetBase")), Times.Once);
        _service.Verify(service => service.RunAsync(It.Is<WindowsServicingRequest>(request => request.ResetBase && request.Action == WindowsServicingAction.Cleanup),
            It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()), confirmed && acknowledged ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task DismissingRepairConfirmationDoesNotRunAnything()
    {
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        using var vm = Create(); await vm.RunCommand.ExecuteAsync("Repair");
        Assert.False(vm.HasReport); _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task TestAndReviewModesBlockEvenDiagnosticCommands(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        using var vm = Create(); Assert.False(vm.RunCommand.CanExecute("Diagnosis"));
        await vm.RunCommand.ExecuteAsync("Diagnosis"); _service.VerifyNoOtherCalls(); _dialogs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FractionalImageIndexCannotReachTheService()
    {
        using var vm = Create(); vm.SourcePath = @"D:\sources\install.wim"; vm.WimIndex = 1.5;
        await vm.RunCommand.ExecuteAsync("Repair"); Assert.True(vm.IsError); _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InlineCancellationKeepsTheOperationReservedUntilTheWorkerStops()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<WindowsServicingReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { started.TrySetResult(); return finished.Task; });
        var notifications = new ScanCancellationNotifications();
        var notices = new List<ScanCancellationNotice>();
        notifications.Cancelled += notices.Add;
        using var vm = Create(notifications); var running = vm.RunCommand.ExecuteAsync("Diagnosis"); await started.Task;
        Assert.False(vm.RunCommand.CanExecute("Repair")); Assert.False(vm.WaitForWriteAsync().IsCompleted);
        Assert.True(vm.IsOperationRunning);
        Assert.True(vm.CancelOperationCommand.CanExecute(null));
        vm.CancelOperationCommand.Execute(null);
        Assert.True(_current!.IsCancellationRequested);
        Assert.True(vm.IsCancelling);
        Assert.False(vm.CancelOperationCommand.CanExecute(null));
        Assert.Empty(notices);
        _tasks.Verify(service => service.StartTask(It.IsAny<string>(), true, false, true), Times.Once);
        finished.SetResult(new(new(WindowsServicingAction.Diagnosis), DateTimeOffset.Now, WindowsServicingState.Cancelled, WindowsImageHealth.Unknown, []));
        await running; await vm.WaitForWriteAsync();
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasReport);
        Assert.Equal("WindowsStatus_StateCancelled", Assert.Single(notices).Message);
        Assert.False(vm.IsOperationRunning);
        Assert.False(vm.IsCancelling);
        Assert.Equal("Diagnosis", vm.DiagnosisAction);
        Assert.Equal("WindowsStatus_DiagnosisShort", vm.DiagnosisLabel);
    }

    [Theory]
    [InlineData("QuickCheck", WindowsImageHealth.Repairable, "WindowsStatus_StateRepairable", true)]
    [InlineData("Diagnosis", WindowsImageHealth.Repairable, "WindowsStatus_StateRepairable", true)]
    [InlineData("Cleanup", WindowsImageHealth.Repairable, "WindowsStatus_StateCleanupNeedsRepair", true)]
    [InlineData("Repair", WindowsImageHealth.Repairable, "WindowsStatus_StateRepairIncomplete", true)]
    [InlineData("QuickCheck", WindowsImageHealth.NonRepairable, "WindowsStatus_StateNonRepairable", false)]
    [InlineData("QuickCheck", WindowsImageHealth.Unknown, "WindowsStatus_StateNeedsAttention", false)]
    public async Task AttentionGuidanceReflectsTheHealthAndTheOperationActuallyRun(string action, WindowsImageHealth health, string summary, bool needsRepair)
    {
        _service.Setup(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WindowsServicingRequest request, IProgress<WindowsServicingProgress>? _, CancellationToken _) =>
                new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.NeedsAttention, health, []));
        using var vm = Create(); await vm.RunCommand.ExecuteAsync(action);
        Assert.Equal(summary, vm.ReportSummary);
        Assert.Equal(needsRepair, vm.NeedsRepair);
        Assert.Equal("Diagnosis", vm.DiagnosisAction);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, vm.ReportSeverity);
        _service.Verify(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(WindowsServicingState.Failed)]
    [InlineData(WindowsServicingState.RestartRequired)]
    public async Task FailureAndRestartGuidanceTakePrecedenceOverRepairableHealth(WindowsServicingState state)
    {
        _service.Setup(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowsServicingReport(new(WindowsServicingAction.Diagnosis), DateTimeOffset.Now, state, WindowsImageHealth.Repairable, []));
        using var vm = Create(); await vm.RunCommand.ExecuteAsync("Diagnosis");
        Assert.Equal("WindowsStatus_State" + state, vm.ReportSummary); Assert.False(vm.NeedsRepair); Assert.Equal("Diagnosis", vm.DiagnosisAction);
    }

    [Fact]
    public async Task RepairRecommendationSurvivesCancellationAndClearsWhenTheImageIsHealthy()
    {
        _service.SetupSequence(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowsServicingReport(new(WindowsServicingAction.QuickCheck), DateTimeOffset.Now,
                WindowsServicingState.NeedsAttention, WindowsImageHealth.Repairable, []))
            .ReturnsAsync(new WindowsServicingReport(new(WindowsServicingAction.Repair), DateTimeOffset.Now,
                WindowsServicingState.ReviewFileResults, WindowsImageHealth.Healthy, []));
        using var vm = Create();
        vm.UseQuickCheck = true;
        await vm.RunCommand.ExecuteAsync(vm.DiagnosisAction);
        Assert.True(vm.NeedsRepair);
        Assert.Equal("WindowsStatus_RepairShort", vm.ReportRepairLabel);
        var diagnosis = vm.Report;
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        await vm.RunCommand.ExecuteAsync(nameof(WindowsServicingAction.Repair));
        Assert.Same(diagnosis, vm.Report);
        Assert.True(vm.NeedsRepair);
        Assert.Equal("QuickCheck", vm.DiagnosisAction);
        Assert.Equal("WindowsStatus_DiagnosisShort", vm.DiagnosisLabel);
        _service.Verify(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        var notifications = new List<string?>(); vm.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
        await vm.RunCommand.ExecuteAsync(nameof(WindowsServicingAction.Repair));
        Assert.False(vm.NeedsRepair);
        Assert.Equal("QuickCheck", vm.DiagnosisAction);
        Assert.Equal("WindowsStatus_DiagnosisShort", vm.DiagnosisLabel);
        Assert.Contains(nameof(vm.NeedsRepair), notifications);
        Assert.Equal("WindowsStatus_StateReviewFileResults", vm.ReportSummary);
        _dialogs.Verify(dialog => dialog.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request => request.Message == "WindowsStatus_ConfirmRepair")), Times.Exactly(2));
    }

    public void Dispose() { foreach (var source in _sources) source.Dispose(); }
}
