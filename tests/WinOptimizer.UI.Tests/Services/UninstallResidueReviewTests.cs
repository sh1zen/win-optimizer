using Moq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.Services;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Checks consent, result-page navigation and inspection-session lifetimes.</summary>
public sealed class UninstallResidueReviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticUninstallConsentUsesDispatcherAndReturnsUsersChoice(bool consent)
    {
        var dialogs = new Mock<IDialogService>();
        var dispatcher = new Mock<IDispatcherService>();
        var text = new Mock<ILocalizationService>();
        var onDispatcher = false;
        dispatcher.Setup(source => source.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Returns(async (Func<Task> action) =>
            {
                onDispatcher = true;
                try { await action(); }
                finally { onDispatcher = false; }
            });
        text.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key);
        text.Setup(source => source.GetString("Uninstall_AutomaticPrompt", It.IsAny<object[]>())).Returns("No registered uninstaller for App. Use automatic uninstall?");
        dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .Callback((ConfirmationRequest request) =>
            {
                Assert.True(onDispatcher);
                Assert.Equal("Uninstall_AutomaticTitle", request.Title);
                Assert.Contains("App", request.Message);
                Assert.Equal("Uninstall_AutomaticProceed", request.ConfirmButtonText);
                Assert.Equal("Button_Cancel", request.CancelButtonText);
            }).ReturnsAsync(new ConfirmationResponse { Confirmed = consent });
        var review = new UninstallResidueReview(Mock.Of<IUninstallResidueService>(), dialogs.Object, dispatcher.Object,
            Mock.Of<ITaskProgressService>(), text.Object, Mock.Of<IApplicationModeService>(), () => new Frame(), () => Mock.Of<IDisposable>());

        Assert.Equal(consent, await review.ConfirmAutomaticUninstallAsync(new ItemDefinition { Id = "app", Name = "App", Description = "Test" }, CancellationToken.None));
        dialogs.Verify(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Once);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReviewOpensPageForBothEmptyAndPopulatedScansAndExpiresHistory(bool hasFindings, bool deferred)
    {
        var frame = new Frame();
        Assert.True(frame.Navigate(typeof(Page)));
        var dispatcher = new Mock<IDispatcherService>();
        dispatcher.Setup(source => source.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Returns((Func<Task> action) => action());
        var text = new Mock<ILocalizationService>();
        text.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key);
        text.Setup(source => source.GetString(It.IsAny<string>(), It.IsAny<object[]>())).Returns((string key, object[] args) => key);
        var session = new Mock<IUninstallResidueSession>();
        var findings = hasFindings ? new[]
        {
            new UninstallResidue(Guid.NewGuid(), "app", "App", "Account", UninstallArtifactKind.File,
                @"C:\App\settings.ini", null, "", 10, DateTime.UtcNow, false, true, "Owned file"),
            new UninstallResidue(Guid.NewGuid(), "app", "App", "Account", UninstallArtifactKind.RegistryKey,
                @"HKEY_USERS\account\Software\App", null, "", 0, null, false, true, "Owned key")
        } : Array.Empty<UninstallResidue>();
        var tasks = new Mock<ITaskProgressService>();
        tasks.Setup(source => source.StartTask(It.IsAny<string>(), true)).Returns(() => new CancellationTokenSource());
        var mode = new Mock<IApplicationModeService>();
        var reservation = new Mock<IDisposable>();
        mode.Setup(source => source.BeginSystemOperation()).Returns(reservation.Object);
        var review = new UninstallResidueReview(Mock.Of<IUninstallResidueService>(), Mock.Of<IDialogService>(),
            dispatcher.Object, tasks.Object, text.Object, mode.Object, () => frame, () => Mock.Of<IDisposable>());
        var scan = new UninstallResidueScan(findings, [new("app", "App", UninstallVerificationState.Removed, "Verified")], [], 0);
        var deferral = deferred ? review.Defer() : null;

        var showing = review.ShowAsync(session.Object, new(scan.Applications, scan.Notices), CancellationToken.None);
        Task? endingDeferral = null;
        if (deferral != null)
        {
            Assert.Equal(UninstallReviewDisposition.Deferred, await showing);
            Assert.IsType<Page>(frame.Content);
            endingDeferral = deferral.DisposeAsync().AsTask();
        }

        var page = Assert.IsType<UninstallResiduePage>(frame.Content);
        Assert.False((endingDeferral ?? showing).IsCompleted);
        var host = Assert.IsType<ContentControl>(page.FindName("ReviewHost"));
        var vm = Assert.IsType<UninstallResidueReviewControl>(host.Content).ViewModel;
        Assert.True(vm.IsScanStep);
        Assert.Empty(vm.FileRoots);
        Assert.Empty(vm.RegistryRoots);
        session.Verify(source => source.ScanAsync(It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(vm.CanDelete);
        var rescanned = new TaskCompletionSource<UninstallResidueScan>();
        session.Setup(source => source.ScanAsync(null, It.IsAny<CancellationToken>())).Returns(rescanned.Task);
        var scanning = vm.ScanCommand.ExecuteAsync(null);
        frame.GoBack();
        Assert.IsType<UninstallResiduePage>(frame.Content);
        Assert.False((endingDeferral ?? showing).IsCompleted);
        rescanned.SetResult(scan);
        await scanning;
        Assert.Equal(UninstallReviewStep.Files, vm.Step);
        Assert.Equal(hasFindings, vm.FileRoots.Count > 0);
        Assert.Equal(hasFindings, vm.RegistryRoots.Count > 0);
        if (hasFindings) Assert.True(frame.Navigate(typeof(Page)));
        else frame.GoBack();

        if (endingDeferral != null) await endingDeferral;
        else Assert.Equal(UninstallReviewDisposition.Completed, await showing);
        Assert.IsType<Page>(frame.Content);
        Assert.DoesNotContain(frame.BackStack, entry => entry.SourcePageType == typeof(UninstallResiduePage));
        Assert.Empty(frame.ForwardStack);
        session.Verify(source => source.DisposeAsync(), deferred ? Times.Once() : Times.Never());
        reservation.Verify(source => source.Dispose(), deferred ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task NativeSuspensionAndRestorationUseDispatcher_AndDisposeOnce()
    {
        var dispatcher = new Mock<IDispatcherService>();
        var onDispatcher = false;
        dispatcher.Setup(source => source.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Returns(async (Func<Task> action) =>
            {
                onDispatcher = true;
                try { await action(); }
                finally { onDispatcher = false; }
            });
        var blocked = new Mock<IDisposable>();
        blocked.Setup(source => source.Dispose()).Callback(() => Assert.True(onDispatcher));
        var review = new UninstallResidueReview(Mock.Of<IUninstallResidueService>(), Mock.Of<IDialogService>(), dispatcher.Object,
            Mock.Of<ITaskProgressService>(), Mock.Of<ILocalizationService>(), Mock.Of<IApplicationModeService>(), () => new Frame(),
            () => { Assert.True(onDispatcher); return blocked.Object; });
        var scope = await review.SuspendForUninstallAsync(CancellationToken.None);
        blocked.Verify(source => source.Dispose(), Times.Never);
        await scope.DisposeAsync();
        await scope.DisposeAsync();
        blocked.Verify(source => source.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeWindowIsDisabledAndMinimizedUntilItsPreviousStateIsRestored(bool maximized)
    {
        var window = new Window { Content = new Grid() };
        try
        {
            window.Activate();
            var presenter = Assert.IsType<OverlappedPresenter>(window.AppWindow.Presenter);
            if (maximized) presenter.Maximize();
            var handle = new Windows.Win32.Foundation.HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
            using (var suspension = new UninstallWindowSuspension(window))
            {
                Assert.False(Windows.Win32.PInvoke.IsWindowEnabled(handle));
                Assert.Equal(OverlappedPresenterState.Minimized, presenter.State);
            }
            Assert.True(Windows.Win32.PInvoke.IsWindowEnabled(handle));
            Assert.Equal(maximized ? OverlappedPresenterState.Maximized : OverlappedPresenterState.Restored, presenter.State);
        }
        finally { window.Close(); }
    }
}
