using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
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
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key == "WindowsStatus_HealthSummary" ? "{0}: {1}" : key);
        _tasks.SetupGet(service => service.CurrentTaskCancellationSource).Returns(() => _current);
        _tasks.Setup(service => service.StartTask(It.IsAny<string>(), true, true, true)).Returns(() =>
        { _current = new(); _sources.Add(_current); return _current; });
        _tasks.Setup(service => service.CompleteTask()).Callback(() => _current = null);
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _service.Setup(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WindowsServicingRequest request, IProgress<WindowsServicingProgress>? _, CancellationToken _) =>
                new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.ReviewFileResults, WindowsImageHealth.Healthy,
                    [new(WindowsServicingStep.RepairFiles, true, 0, WindowsImageHealth.Unknown, false, "SFC native result")]));
    }

    private WindowsStatusViewModel Create() => new(_service.Object, _dialogs.Object, Mock.Of<IFilePickerService>(),
        _localization.Object, _mode.Object, new ExtensionTestDispatcher(), _tasks.Object);

    [Fact]
    public void OpeningTheToolDoesNotRunCommandsOrEnableResetBase()
    {
        using var vm = Create(); Assert.False(vm.ResetBase); Assert.False(vm.HasReport); Assert.Empty(vm.SourcePath);
        Assert.Equal("Nav_Management", vm.SectionTitle); _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RepairPassesExplicitSourceOptionsAndPreservesNativeOutput()
    {
        using var vm = Create(); vm.SourcePath = @"D:\sources\install.wim"; vm.WimIndex = 6; vm.LimitAccess = true;
        await vm.RunCommand.ExecuteAsync("Repair");
        _service.Verify(service => service.RunAsync(It.Is<WindowsServicingRequest>(request =>
            request.Action == WindowsServicingAction.Repair && request.SourcePath == @"D:\sources\install.wim" && request.WimIndex == 6 && request.LimitAccess && !request.ResetBase),
            It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("SFC native result", Assert.Single(vm.Steps).Output);
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
    public async Task PendingOperationDisablesCommandsAndShutdownWaitsForItsReport()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<WindowsServicingReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { started.TrySetResult(); return finished.Task; });
        using var vm = Create(); var running = vm.RunCommand.ExecuteAsync("Diagnosis"); await started.Task;
        Assert.False(vm.RunCommand.CanExecute("Repair")); Assert.False(vm.WaitForWriteAsync().IsCompleted);
        finished.SetResult(new(new(WindowsServicingAction.Diagnosis), DateTimeOffset.Now, WindowsServicingState.Cancelled, WindowsImageHealth.Unknown, []));
        await running; await vm.WaitForWriteAsync(); Assert.False(vm.IsBusy); Assert.Equal("WindowsStatus_StateCancelled", vm.ReportSummary);
    }

    [Theory]
    [InlineData("QuickCheck", WindowsImageHealth.Repairable, "WindowsStatus_StateRepairable", "Repair")]
    [InlineData("Diagnosis", WindowsImageHealth.Repairable, "WindowsStatus_StateRepairable", "Repair")]
    [InlineData("Cleanup", WindowsImageHealth.Repairable, "WindowsStatus_StateCleanupNeedsRepair", "Repair")]
    [InlineData("Repair", WindowsImageHealth.Repairable, "WindowsStatus_StateRepairIncomplete", "Repair")]
    [InlineData("QuickCheck", WindowsImageHealth.NonRepairable, "WindowsStatus_StateNonRepairable", "Diagnosis")]
    [InlineData("QuickCheck", WindowsImageHealth.Unknown, "WindowsStatus_StateNeedsAttention", "Diagnosis")]
    public async Task AttentionGuidanceReflectsTheHealthAndTheOperationActuallyRun(string action, WindowsImageHealth health, string summary, string nextAction)
    {
        _service.Setup(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WindowsServicingRequest request, IProgress<WindowsServicingProgress>? _, CancellationToken _) =>
                new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.NeedsAttention, health, []));
        using var vm = Create(); await vm.RunCommand.ExecuteAsync(action);
        Assert.Equal(summary, vm.ReportSummary); Assert.Equal(nextAction, vm.PrimaryAction);
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
        Assert.Equal("WindowsStatus_State" + state, vm.ReportSummary); Assert.False(vm.NeedsRepair); Assert.Equal("Diagnosis", vm.PrimaryAction);
    }

    [Fact]
    public async Task RecommendedRepairStillRequiresConfirmationAndAHealthyResultUpdatesTheMainAction()
    {
        _service.SetupSequence(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowsServicingReport(new(WindowsServicingAction.QuickCheck), DateTimeOffset.Now,
                WindowsServicingState.NeedsAttention, WindowsImageHealth.Repairable, []))
            .ReturnsAsync(new WindowsServicingReport(new(WindowsServicingAction.Repair), DateTimeOffset.Now,
                WindowsServicingState.ReviewFileResults, WindowsImageHealth.Healthy, []));
        using var vm = Create(); await vm.RunCommand.ExecuteAsync("QuickCheck");
        Assert.True(vm.NeedsRepair); Assert.Equal("WindowsStatus_RepairShort", vm.PrimaryActionLabel);
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        await vm.RunCommand.ExecuteAsync(vm.PrimaryAction); Assert.True(vm.NeedsRepair);
        _service.Verify(service => service.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        _dialogs.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        var notifications = new List<string?>(); vm.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
        await vm.RunCommand.ExecuteAsync(vm.PrimaryAction);
        Assert.False(vm.NeedsRepair); Assert.Equal("Diagnosis", vm.PrimaryAction); Assert.Equal("WindowsStatus_DiagnosisShort", vm.PrimaryActionLabel);
        Assert.Contains(nameof(vm.PrimaryAction), notifications); Assert.Contains(nameof(vm.IntroTitle), notifications);
        Assert.Equal("WindowsStatus_StateReviewFileResults", vm.ReportSummary);
        _dialogs.Verify(dialog => dialog.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request => request.Message == "WindowsStatus_ConfirmRepair")), Times.Exactly(2));
    }

    public void Dispose() { foreach (var source in _sources) source.Dispose(); }
}
