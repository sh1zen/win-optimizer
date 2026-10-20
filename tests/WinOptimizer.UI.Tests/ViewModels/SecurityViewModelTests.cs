using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Security;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Security;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Exercises normal commands with synthetic scan data and isolated dialog/task boundaries.</summary>
public sealed class SecurityViewModelTests : IDisposable
{
    private readonly Mock<ISecurityScanService> _service = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly List<CancellationTokenSource> _sources = [];
    private CancellationTokenSource? _current;
    public SecurityViewModelTests()
    {
        _localization.Setup(localization => localization.GetString(It.IsAny<string>())).Returns((string key) => key);
        _tasks.SetupGet(tasks => tasks.CurrentTaskCancellationSource).Returns(() => _current);
        _tasks.Setup(tasks => tasks.StartTask(It.IsAny<string>(), true, true, true)).Returns(() => { _current = new(); _sources.Add(_current); return _current; });
        _tasks.Setup(tasks => tasks.CompleteTask()).Callback(() => _current = null);
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _service.Setup(service => service.ScanAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((bool deep, CancellationToken _) =>
            new SecurityScanReport(Guid.NewGuid(), DateTimeOffset.Now, deep, [
                new(SecurityCheckId.Firewall, SecurityCheckState.Attention, "Disabled", "public", true),
                new(SecurityCheckId.Isatap, SecurityCheckState.Attention, "Automatic", "Automatic", true),
                new(SecurityCheckId.Firefox, SecurityCheckState.Unknown, "", "unavailable", false)
            ]));
        _service.Setup(service => service.FixAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SecurityCheckId>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyList<SecurityCheckId> ids, CancellationToken _) => new SecurityFixResult(ids.Select(id => new SecurityFixOutcome(id, true, "")).ToArray(), false));
    }
    private SecurityViewModel Create() => new(_service.Object, _dialogs.Object, _localization.Object, _mode.Object,
        new ExtensionTestDispatcher(), _tasks.Object);

    [Fact]
    public async Task ExplicitScanShowsFiveCategoriesAndOnlySupportedSelections()
    {
        using var vm = Create(); Assert.False(vm.HasReport); _service.VerifyNoOtherCalls();
        await vm.DeepScanCommand.ExecuteAsync(null);
        Assert.True(vm.HasReport); Assert.Equal(5, vm.Categories.Count); Assert.Equal(2, vm.Findings.Count);
        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.Findings[0].IsSelected); Assert.False(vm.Findings[1].IsSelected); Assert.True(vm.CanFix);
        _tasks.Verify(tasks => tasks.CompleteTask(), Times.Once);
    }

    [Fact]
    public async Task ConfirmationCapturesSelectionsAcrossCategoriesAndSuccessRequiresRescan()
    {
        using var vm = Create(); await vm.DeepScanCommand.ExecuteAsync(null);
        vm.Findings[0].IsSelected = true;
        vm.SelectedCategory = vm.Categories.Single(category => category.Area == SecurityArea.Firewall);
        vm.Findings[0].IsSelected = true;
        await vm.FixCommand.ExecuteAsync(null);
        _dialogs.Verify(dialogs => dialogs.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request => request.Items!.Count == 2)), Times.Once);
        _service.Verify(service => service.FixAsync(It.IsAny<Guid>(), It.Is<IReadOnlyList<SecurityCheckId>>(ids => ids.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(vm.CanFix); Assert.False(vm.Findings[0].CanSelect); Assert.Equal(SecurityCheckState.Good, vm.Findings[0].Observation.State);
    }

    [Fact]
    public async Task DeclinedConfirmationDoesNotWriteOrDiscardSelection()
    {
        using var vm = Create(); await vm.DeepScanCommand.ExecuteAsync(null); vm.Findings[0].IsSelected = true;
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        await vm.FixCommand.ExecuteAsync(null); Assert.True(vm.Findings[0].IsSelected);
        _service.Verify(service => service.FixAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SecurityCheckId>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ModeChangesBlockFixesButLeaveScanAvailable()
    {
        using var vm = Create(); await vm.DeepScanCommand.ExecuteAsync(null); vm.Findings[0].IsSelected = true;
        _mode.SetupGet(mode => mode.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mode.Raise(mode => mode.ModeChanged += null, EventArgs.Empty);
        Assert.False(vm.FixCommand.CanExecute(null)); Assert.True(vm.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task NavigationCancelsScanAndClearsPreviousReport()
    {
        using var vm = Create(); await vm.DeepScanCommand.ExecuteAsync(null);
        _service.Setup(service => service.ScanAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(async (bool _, CancellationToken token) =>
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); });
        var scan = vm.ScanCommand.ExecuteAsync(null);
        Assert.False(vm.HasReport); vm.Deactivate(); await scan;
        Assert.True(vm.IsIdle); Assert.False(vm.HasReport); Assert.Equal("Security_Cancelled", vm.Status);
    }

    public void Dispose() { foreach (var source in _sources) source.Dispose(); }
}
