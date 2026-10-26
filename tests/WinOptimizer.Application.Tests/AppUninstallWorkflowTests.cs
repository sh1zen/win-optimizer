using Moq;
using WinOptimizer.Application.SoftwareApps;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

/// <summary>Checks phase ordering, exact per-app outcomes and review/session ownership without launching uninstallers.</summary>
public sealed class AppUninstallWorkflowTests
{
    private readonly Mock<IUninstallResidueService> _residues = new();
    private readonly Mock<IUninstallResidueSession> _session = new();
    private readonly Mock<IUninstallResidueReview> _review = new();
    private readonly Mock<IWindowsAppUninstallService> _windows = new();
    private readonly Mock<IExternalAppsService> _external = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly List<string> _events = [];
    private readonly ItemDefinition[] _apps = [new() { Id = "one", Name = "One", Description = "First" }, new() { Id = "two", Name = "Two", Description = "Second" }];
    private readonly UninstallResidueScan _scan = new([], [new("one", "One", UninstallVerificationState.Removed, "Verified")], [] , 3);
    private readonly AppUninstallWorkflow _sut;

    public AppUninstallWorkflowTests()
    {
        _mode.Setup(mode => mode.BeginSystemOperation()).Callback(() => _events.Add("reserve")).Returns(new Scope(() => _events.Add("release")));
        _tasks.Setup(tasks => tasks.StartTask(It.IsAny<string>(), true)).Returns(() => new CancellationTokenSource());
        _residues.Setup(service => service.CaptureAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _events.Add("capture")).ReturnsAsync(_session.Object);
        _session.Setup(session => session.InspectAsync(It.IsAny<IReadOnlyList<UninstallNativeOutcome>>(), It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _events.Add("inspect")).ReturnsAsync(_scan);
        _session.Setup(session => session.DisposeAsync()).Callback(() => _events.Add("dispose")).Returns(ValueTask.CompletedTask);
        _review.Setup(review => review.ShowAsync(_session.Object, _scan, It.IsAny<CancellationToken>()))
            .Callback(() => _events.Add("review")).ReturnsAsync(UninstallReviewDisposition.Completed);
        var text = new Mock<ILocalizationService>(); text.Setup(text => text.GetString(It.IsAny<string>())).Returns((string key) => key);
        _sut = new(_residues.Object, _review.Object, _windows.Object, _external.Object, _tasks.Object, _mode.Object, text.Object);
    }

    [Fact]
    public async Task InventoryRefreshCompletesBeforeSelectableResiduesArePresented()
    {
        _external.Setup(service => service.UninstallAppAsync(It.IsAny<ItemDefinition>(), It.IsAny<IProgress<TaskProgressDetail>>()))
            .ReturnsAsync(OperationResult<bool>.Succeeded(true));
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AppUninstallResult? published = null;
        var pending = _sut.UninstallAsync(_apps, AppUninstallKind.External, refreshInventory: result =>
        {
            published = result;
            Assert.Same(_scan.Applications, result.Applications);
            Assert.Same(_scan.Notices, result.Notices);
            _events.Add("refresh");
            return refreshed.Task;
        });
        Assert.Equal(new[] { "reserve", "capture", "inspect", "refresh" }, _events);
        Assert.False(pending.IsCompleted);
        _review.Verify(review => review.ShowAsync(It.IsAny<IUninstallResidueSession>(), It.IsAny<UninstallResidueScan>(), It.IsAny<CancellationToken>()), Times.Never);
        refreshed.SetResult();
        Assert.Same(published, await pending);
        Assert.Equal(new[] { "reserve", "capture", "inspect", "refresh", "review", "release", "dispose" }, _events);
    }

    [Fact]
    public async Task WindowsPreservesMixedOutcomes_AndCapturesBeforeLaunchingNativeRemoval()
    {
        UninstallNativeOutcome[] native = [new("one", true, false, null), new("two", false, true, "Restart required")];
        _windows.Setup(service => service.UninstallAppsWithOutcomesAsync(It.IsAny<List<ItemDefinition>>(), false))
            .Callback(() => _events.Add("native")).ReturnsAsync(OperationResult<IReadOnlyList<UninstallNativeOutcome>>.Succeeded(native));
        var result = await _sut.UninstallAsync(_apps, AppUninstallKind.Windows, false);
        Assert.Equal(new[] { "reserve", "capture", "native", "inspect", "review", "release", "dispose" }, _events);
        Assert.Same(_scan.Applications, result.Applications);
        _session.Verify(session => session.InspectAsync(It.Is<IReadOnlyList<UninstallNativeOutcome>>(outcomes => outcomes.SequenceEqual(native)),
            It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        _tasks.Verify(tasks => tasks.CompleteTask(), Times.Exactly(2));
    }

    [Fact]
    public async Task ExternalReportsFailureAndDeferredSuccessIndividually()
    {
        _external.Setup(service => service.UninstallAppAsync(_apps[0], It.IsAny<IProgress<TaskProgressDetail>>())).ReturnsAsync(OperationResult<bool>.Failed("Native failure"));
        _external.Setup(service => service.UninstallAppAsync(_apps[1], It.IsAny<IProgress<TaskProgressDetail>>())).ReturnsAsync(OperationResult<bool>.DeferredSuccess(true, "Restart"));
        await _sut.UninstallAsync(_apps, AppUninstallKind.External);
        _tasks.Verify(tasks => tasks.UpdateDetailedProgress(It.Is<TaskProgressDetail>(detail => detail.QueueTotal == 2 && detail.QueueCurrent == 1 && detail.QueueNextItemName == "Two")), Times.Once);
        _tasks.Verify(tasks => tasks.UpdateDetailedProgress(It.Is<TaskProgressDetail>(detail => detail.QueueTotal == 2 && detail.QueueCurrent == 2 && detail.QueueNextItemName == null)), Times.Once);
        _session.Verify(session => session.InspectAsync(It.Is<IReadOnlyList<UninstallNativeOutcome>>(outcomes =>
            outcomes.Count == 2 && !outcomes[0].Success && outcomes[0].Message == "Native failure" && outcomes[1].Success && outcomes[1].Deferred),
            It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SkipDoesNotLaunchThatApp_AndNeverClaimsItWasRemoved()
    {
        _tasks.Setup(tasks => tasks.ConsumeSkipNextRequest()).Returns(true);
        await _sut.UninstallAsync(_apps, AppUninstallKind.External);
        _external.Verify(service => service.UninstallAppAsync(It.IsAny<ItemDefinition>(), It.IsAny<IProgress<TaskProgressDetail>>()), Times.Never);
        _session.Verify(session => session.InspectAsync(It.Is<IReadOnlyList<UninstallNativeOutcome>>(outcomes => outcomes.All(outcome => !outcome.Success)),
            It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeferredReviewOwnsSessionUntilConfigurationOverlayHasClosed()
    {
        _tasks.Setup(tasks => tasks.ConsumeSkipNextRequest()).Returns(true);
        _review.Setup(review => review.ShowAsync(_session.Object, _scan, It.IsAny<CancellationToken>())).ReturnsAsync(UninstallReviewDisposition.Deferred);
        await _sut.UninstallAsync(_apps, AppUninstallKind.External);
        _session.Verify(session => session.DisposeAsync(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ErrorOrCancellationAlwaysDisposesSessionAndReleasesReservation(bool cancellation)
    {
        _external.Setup(service => service.UninstallAppAsync(It.IsAny<ItemDefinition>(), It.IsAny<IProgress<TaskProgressDetail>>()))
            .ThrowsAsync(cancellation ? new OperationCanceledException() : new IOException("Native launch error"));
        await Assert.ThrowsAnyAsync<Exception>(() => _sut.UninstallAsync(_apps, AppUninstallKind.External));
        _session.Verify(session => session.DisposeAsync(), Times.Once);
        _review.Verify(review => review.ShowAsync(It.IsAny<IUninstallResidueSession>(), It.IsAny<UninstallResidueScan>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("release", _events);
        _tasks.Verify(tasks => tasks.CompleteTask(), Times.Exactly(2));
    }

    [Fact]
    public async Task CaptureFailureAndBuilderGuardPreventNativeLaunch()
    {
        _mode.Setup(mode => mode.BeginSystemOperation()).Throws(new InvalidOperationException("Test mode"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UninstallAsync(_apps, AppUninstallKind.Windows));
        _residues.Verify(service => service.CaptureAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        _windows.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DuplicateIdentitiesAreRejectedBeforeAnyOperation()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.UninstallAsync([_apps[0], _apps[0]], AppUninstallKind.External));
        Assert.Empty(_events);
    }

    private sealed class Scope(Action release) : IDisposable { public void Dispose() => release(); }
}
