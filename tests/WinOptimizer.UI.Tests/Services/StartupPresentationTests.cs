using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Checks the production startup frame boundary on a native WinUI dispatcher.</summary>
public sealed class StartupPresentationTests
{
    /// <summary>Renders the production main window before any shell services are registered or resolved.</summary>
    [Fact]
    public async Task MainWindowPresentsItsLoaderBeforeShellInitialization()
    {
        var window = new WinOptimizer.UI.MainWindow();
        try
        {
            var loader = Assert.IsType<StartupLoadingView>(window.Content);
            Assert.Null(window.ViewModel);
            Assert.False(window.AppWindow.IsVisible);
            var presented = window.WaitForLoadingFrameAsync();
            Assert.False(presented.IsCompleted);
            window.Activate();
            await presented.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(loader.IsLoaded);
            Assert.True(loader.ActualWidth > 0);
            Assert.True(Assert.IsType<ProgressRing>(loader.FindName("LoadingProgressRing")).IsActive);
            Assert.Equal("win-optimizer", Assert.IsType<TextBlock>(loader.FindName("LoadingTitleText")).Text);
            Assert.Null(window.ViewModel);

            // The production handover keeps the same loader and native window alive.
            var id = window.AppWindow.Id;
            window.InitializeShell();
            var shell = Assert.IsType<Grid>(window.Content);
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            var overlay = Assert.IsType<Grid>(shell.FindName("LoadingOverlay"));
            Assert.Same(loader, Assert.Single(overlay.Children));
            Assert.Equal(id, window.AppWindow.Id);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(loader.IsLoaded);
            Assert.Equal("Loading_PreparingApp", loader.StatusText.Text);
            Assert.Single(Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries
                .OfType<WinOptimizer.UI.Features.Common.Resources.AppColorResources>());
        }
        finally { window.Close(); }
        Assert.True(window.IsClosed);
    }

    /// <summary>Allows a redirected manual launch to open the loader while widget startup is preparing services.</summary>
    [Fact]
    public async Task ManualActivationDuringEarlyStartupShowsTheSameWindow()
    {
        var window = new WinOptimizer.UI.MainWindow();
        try
        {
            var loader = window.Content;
            window.RestoreFromBackground();
            await window.WaitForLoadingFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsManualActivationRequested);
            Assert.True(window.AppWindow.IsVisible);
            Assert.Same(loader, window.Content);
            Assert.Null(window.ViewModel);
        }
        finally { window.Close(); }
    }

    /// <summary>Checks selected button bounds against the real scroll viewport as the pane resizes.</summary>
    [Fact]
    public async Task SidebarSelectionFitsTheViewportInBothPaneModesWithAndWithoutScrolling()
    {
        var sidebar = new NavSidebar { SelectedTag = "SoftwareApps", VerticalAlignment = VerticalAlignment.Top };
        var window = new Window { Content = sidebar };
        try
        {
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(sidebar, () => Task.CompletedTask);
            var panel = Assert.IsType<StackPanel>(sidebar.FindName("TopNavPanel"));
            var presenter = VisualTreeHelper.GetParent(panel);
            while (presenter is not null && presenter is not ScrollContentPresenter)
                presenter = VisualTreeHelper.GetParent(presenter);
            var viewport = Assert.IsType<ScrollContentPresenter>(presenter);
            var button = Assert.IsType<NavButton>(sidebar.GetButton("SoftwareApps"));
            var background = Assert.IsType<Border>(button.FindName("BackgroundBorder"));

            foreach (var expanded in new[] { true, false })
            foreach (var height in new[] { 800d, 300d })
            {
                sidebar.IsPaneOpen = expanded;
                sidebar.Height = height;
                await StartupUiCoordinator.RunAfterFirstRenderAsync(sidebar, () => Task.CompletedTask);
                sidebar.UpdateLayout();
                Assert.True(button.IsSelected);
                Assert.Equal(button.ActualButtonWidth, background.ActualWidth);
                var offset = background.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point());
                Assert.InRange(offset.X, 0, viewport.ActualWidth - background.ActualWidth);
                Assert.InRange(offset.Y, 0, viewport.ActualHeight - background.ActualHeight);
            }
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task ShellInitializationScheduledBeforeActivationWaitsForTheFirstFrame()
    {
        var content = new Grid();
        var window = new Window { Content = content };
        var initialized = false;
        try
        {
            var startup = StartupUiCoordinator.RunAfterFirstRenderAsync(content, () =>
            {
                Assert.True(content.IsLoaded);
                Assert.True(content.ActualWidth > 0);
                initialized = true;
                return Task.CompletedTask;
            });
            Assert.False(initialized);
            Assert.False(startup.IsCompleted);
            window.Activate();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(initialized);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task InventoryStartsOnlyAfterRendering_AndItsCompletionDoesNotBlockPresentation()
    {
        var content = new Grid();
        var window = new Window { Content = content };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        content.Loaded += (_, _) => loaded.TrySetResult();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        try
        {
            window.Activate();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var deferred = StartupUiCoordinator.RunAfterFirstRenderAsync(content, () =>
            {
                Assert.True(content.IsLoaded);
                Assert.True(content.ActualWidth > 0);
                calls++;
                started.SetResult();
                return finish.Task;
            });
            Assert.Equal(0, calls);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(deferred.IsCompleted);
            Assert.Equal(1, calls);
            finish.SetResult();
            await deferred.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
        }
        finally { finish.TrySetResult(); window.Close(); }
    }

    [Fact]
    public async Task UnloadingBeforeTheFrameCancelsInventory()
    {
        var content = new Grid();
        var window = new Window { Content = content };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        content.Loaded += (_, _) => loaded.TrySetResult();
        window.Activate();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var called = false;
            var deferred = StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => { called = true; return Task.CompletedTask; });
            window.Content = new Grid();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deferred.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(called);
        }
        finally { window.Close(); }
    }
}
