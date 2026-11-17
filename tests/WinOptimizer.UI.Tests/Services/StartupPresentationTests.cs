using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Checks the production startup frame boundary on a native WinUI dispatcher.</summary>
public sealed class StartupPresentationTests
{
    /// <summary>Exercises the compositor effect, rapid navigation, and reduced-motion cancellation.</summary>
    [Fact]
    public async Task PageBlurOverlayClearsAfterTheLatestTransition()
    {
        var overlay = new Border { IsHitTestVisible = false };
        var content = new Grid { Children = { new TextBlock { Text = "Page content" }, overlay } };
        var window = new Window { Content = content };
        try
        {
            using var transition = new PageBlurTransition(overlay, null);
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => Task.CompletedTask);

            transition.Play(false);
            Assert.Null(ElementCompositionPreview.GetElementChildVisual(overlay));
            transition.Play(true);
            var visual = Assert.IsType<SpriteVisual>(ElementCompositionPreview.GetElementChildVisual(overlay));
            Assert.IsType<CompositionEffectBrush>(visual.Brush);
            Assert.Equal(System.Numerics.Vector2.One, visual.RelativeSizeAdjustment);

            transition.Play(true);
            Assert.Same(visual, ElementCompositionPreview.GetElementChildVisual(overlay));
            transition.Play(false);
            Assert.Null(ElementCompositionPreview.GetElementChildVisual(overlay));
            transition.Play(true);
            await WaitForOverlayToClearAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { window.Close(); }

        async Task WaitForOverlayToClearAsync()
        {
            while (ElementCompositionPreview.GetElementChildVisual(overlay) != null)
                await StartupUiCoordinator.RunAfterFirstRenderAsync(overlay, () => Task.CompletedTask);
        }
    }

    /// <summary>Checks the state brushes consumed by the rendered card and input templates.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InteractiveSurfacesKeepOpaqueFillsThroughHoverAndPress(bool dark)
    {
        var card = new NavigationCard { Title = "SysMonitor" };
        var button = new Button { Content = "Show widget" };
        var resources = Microsoft.UI.Xaml.Application.Current.Resources;
        var titleBarButton = new Button { Content = "Back", Style = (Style)resources["TitleBarButtonStyle"] };
        var dropDownButton = new DropDownButton { Content = "Sort" };
        var comboBox = new ComboBox { PlaceholderText = "Options" };
        var searchInput = new TextBox { PlaceholderText = "Search" };
        var content = new StackPanel
        {
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Children = { card, button, titleBarButton, dropDownButton, comboBox, searchInput }
        };
        var window = new Window { Content = content };
        try
        {
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => Task.CompletedTask);
            var cardControl = Assert.IsType<CommunityToolkit.WinUI.Controls.SettingsCard>(card.FindName("Card"));
            var cardSurface = Assert.IsType<Grid>(VisualTreeHelper.GetChild(cardControl, 0));
            var buttonSurface = Assert.IsType<ContentPresenter>(VisualTreeHelper.GetChild(button, 0));
            var titleBarSurface = Assert.IsType<ContentPresenter>(VisualTreeHelper.GetChild(titleBarButton, 0));
            var dropDownSurface = Assert.IsType<Grid>(VisualTreeHelper.GetChild(dropDownButton, 0));
            var comboRoot = Assert.IsType<Grid>(VisualTreeHelper.GetChild(comboBox, 0));
            var comboSurface = comboRoot.Children.OfType<Border>().Single(border => border.Name == "Background");
            var searchRoot = Assert.IsType<Grid>(VisualTreeHelper.GetChild(searchInput, 0));
            var searchSurface = searchRoot.Children.OfType<Border>().Single(border => border.Name == "BorderElement");
            var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var appColors = resources.MergedDictionaries
                .OfType<WinOptimizer.UI.Features.Common.Resources.AppColorResources>().Single();
            var palette = Assert.IsType<ResourceDictionary>(appColors.ThemeDictionaries[dark ? "Default" : "Light"]);

            foreach (var (state, surfaceKey) in new[]
            {
                ("PointerOver", "AppSurfacePointerOverBrush"),
                ("Pressed", "AppSurfacePressedBrush"),
                ("Normal", "AppSurfaceBrush")
            })
            {
                Assert.True(VisualStateManager.GoToState(cardControl, state, true));
                Assert.True(VisualStateManager.GoToState(button, state, true));
                Assert.True(VisualStateManager.GoToState(titleBarButton, state, true));
                Assert.True(VisualStateManager.GoToState(dropDownButton, state, true));
                Assert.True(VisualStateManager.GoToState(comboBox, state, true));
                if (state != "Pressed")
                    Assert.True(VisualStateManager.GoToState(searchInput, state, true));

                await StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => Task.CompletedTask);
                var cardFill = Assert.IsType<SolidColorBrush>(cardSurface.Background).Color;
                Assert.Equal(byte.MaxValue, cardFill.A);
                // Contrast themes use the system's separate interaction palette.
                if (highContrast) continue;

                var expected = Assert.IsType<SolidColorBrush>(palette[surfaceKey]).Color;
                Assert.Equal(expected, cardFill);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(buttonSurface.Background).Color);
                var titleBarOpacity = Assert.IsType<SolidColorBrush>(titleBarSurface.Background).Color.A;
                if (state == "Normal")
                    Assert.Equal((byte)0, titleBarOpacity);
                else
                    Assert.InRange(titleBarOpacity, (byte)1, (byte)64);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(dropDownSurface.Background).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(comboSurface.Background).Color);
                if (state != "Pressed")
                    Assert.Equal(expected, Assert.IsType<SolidColorBrush>(searchSurface.Background).Color);
            }
        }
        finally { window.Close(); }
    }

    /// <summary>Centers the loader at each viewport size without shifting on detailed progress reports.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingContentStaysCenteredWhenResizedAndStatusChanges(bool rightToLeft)
    {
        var loader = new StartupLoadingView
        {
            FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
        };
        var viewport = new Grid { Children = { loader } };
        var window = new Window { Content = viewport };
        try
        {
            window.Activate();
            foreach (var (width, height) in new[] { (1050, 720), (696, 479), (320, 600) })
            {
                window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, height));
                loader.StatusText.Text = "Initializing settings...";
                await StartupUiCoordinator.RunAfterFirstRenderAsync(loader, () => Task.CompletedTask);
                var content = Assert.IsType<StackPanel>(loader.FindName("LoadingContent"));
                var card = Assert.IsType<Border>(loader.FindName("LoadingStatusCard"));
                Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(loader)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                var contentBounds = Bounds(content);
                var cardBounds = Bounds(card);
                Assert.InRange(Math.Abs(contentBounds.X + contentBounds.Width / 2 - loader.ActualWidth / 2), 0, 1);
                Assert.InRange(Math.Abs(contentBounds.Y + contentBounds.Height / 2 - loader.ActualHeight / 2), 0, 1);
                Assert.InRange(Math.Abs(cardBounds.X + cardBounds.Width / 2 - loader.ActualWidth / 2), 0, 1);

                loader.StatusText.Text = "Initializing settings...\nPreparing the interface...\nLoading installed applications...\nPreparing configuration backup...";
                await StartupUiCoordinator.RunAfterFirstRenderAsync(loader, () => Task.CompletedTask);
                Assert.Equal(contentBounds, Bounds(content));
                Assert.Equal(cardBounds, Bounds(card));
                var statusViewport = Assert.IsType<ScrollViewer>(loader.FindName("LoadingStatusViewport"));
                Assert.True(statusViewport.ScrollableHeight > 0);
                statusViewport.ChangeView(null, statusViewport.ScrollableHeight, null, disableAnimation: true);
                await StartupUiCoordinator.RunAfterFirstRenderAsync(loader, () => Task.CompletedTask);
                Assert.InRange(Math.Abs(statusViewport.VerticalOffset - statusViewport.ScrollableHeight), 0, 1);
                Assert.Equal(contentBounds, Bounds(content));
                Assert.Equal(cardBounds, Bounds(card));
            }
        }
        finally { window.Close(); }
    }

    /// <summary>Restores placement before rendering the loader and retains it through shell construction.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainWindowPresentsItsLoaderBeforeShellInitialization(bool rightToLeft)
    {
        var window = new WinOptimizer.UI.MainWindow();
        try
        {
            var loader = Assert.IsType<StartupLoadingView>(window.Content);
            Assert.Null(window.ViewModel);
            Assert.False(window.AppWindow.IsVisible);
            Assert.True(window.ExtendsContentIntoTitleBar);
            Assert.Equal(Microsoft.UI.Windowing.TitleBarHeightOption.Tall, window.AppWindow.TitleBar.PreferredHeightOption);
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.IsRightToLeft).Returns(rightToLeft);
            localization.Setup(service => service.GetString("App_Title")).Returns("win-optimizer");
            localization.Setup(service => service.GetString("App_Tagline")).Returns("Windows optimization and maintenance");
            localization.Setup(service => service.GetString("Loading_PreparingApp")).Returns("Preparing app...");
            window.InitializeLoadingPresentation(localization.Object);
            loader.RequestedTheme = ElementTheme.Dark;
            var preferences = new Mock<IUserPreferencesService>(MockBehavior.Strict);
            var log = Mock.Of<ILogService>();
            var saved = new UserPreferencesSnapshot(new Dictionary<string, object>
            {
                [UserPreferenceKeys.WindowWidth] = 1050d,
                [UserPreferenceKeys.WindowHeight] = 720d,
            }, log);
            await window.InitializeWindowPlacementAsync(preferences.Object, log, saved);
            Assert.False(window.AppWindow.IsVisible);
            Assert.Equal(1050, window.AppWindow.Size.Width);
            Assert.Equal(720, window.AppWindow.Size.Height);
            var presented = window.WaitForLoadingFrameAsync();
            Assert.False(presented.IsCompleted);
            window.Activate();
            await presented.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(loader.IsLoaded);
            Assert.True(loader.ActualWidth > 0);
            Assert.True(Assert.IsType<ProgressRing>(loader.FindName("LoadingProgressRing")).IsActive);
            Assert.Equal("win-optimizer", Assert.IsType<TextBlock>(loader.FindName("LoadingTitleText")).Text);
            Assert.Null(window.ViewModel);
            Assert.Equal(rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, loader.FlowDirection);
            var logo = Assert.IsType<Image>(loader.FindName("LoadingLogo"));
            var card = Assert.IsType<Border>(loader.FindName("LoadingStatusCard"));
            Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(null)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            var logoBounds = Bounds(logo);
            var cardBounds = Bounds(card);
            var clientHeight = loader.ActualHeight;
            var clientWidth = loader.ActualWidth;

            // Progress reports may wrap, but should not resize the card or move the branding.
            loader.StatusText.Text = "Initializing settings...\nPreparing the interface...";
            await StartupUiCoordinator.RunAfterFirstRenderAsync(loader, () => Task.CompletedTask);
            Assert.Equal(logoBounds, Bounds(logo));
            Assert.Equal(cardBounds, Bounds(card));

            // The production handover keeps the same loader and native window alive.
            var id = window.AppWindow.Id;
            window.InitializeShell();
            var shell = Assert.IsType<Grid>(window.Content);
            var frame = Assert.IsType<Frame>(shell.FindName("ContentFrame"));
            var navigation = Assert.IsType<NavigationThemeTransition>(Assert.Single(frame.ContentTransitions));
            Assert.IsType<SuppressNavigationTransitionInfo>(navigation.DefaultNavigationTransitionInfo);
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            var overlay = Assert.IsType<Grid>(shell.FindName("LoadingOverlay"));
            Assert.Same(loader, Assert.Single(overlay.Children));
            Assert.Equal(id, window.AppWindow.Id);
            Assert.Equal(1050, window.AppWindow.Size.Width);
            Assert.Equal(720, window.AppWindow.Size.Height);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.True(loader.IsLoaded);
            Assert.Equal(clientWidth, loader.ActualWidth);
            Assert.Equal(clientHeight, loader.ActualHeight);
            Assert.Equal(logoBounds, Bounds(logo));
            Assert.Equal(cardBounds, Bounds(card));
            Assert.Equal(ElementTheme.Dark, shell.RequestedTheme);
            Assert.Equal(ElementTheme.Dark, loader.ActualTheme);
            Assert.Equal("Initializing settings...\nPreparing the interface...", loader.StatusText.Text);
            Assert.Single(Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries
                .OfType<WinOptimizer.UI.Features.Common.Resources.AppColorResources>());
        }
        finally { window.Close(); }
        Assert.True(window.IsClosed);
    }

    /// <summary>Keeps the loader in place until the shell's first measured frame is ready.</summary>
    [Fact]
    public async Task ShellRevealWaitsForLayoutBeforeCompletingStartup()
    {
        var content = new Grid();
        var overlay = new Grid();
        var root = new Grid();
        root.Children.Add(content);
        root.Children.Add(overlay);
        var window = new Window { Content = root };
        var completed = false;
        try
        {
            var reveal = StartupUiCoordinator.RevealShellAsync(content, overlay, () =>
            {
                Assert.True(content.IsLoaded);
                Assert.True(content.ActualWidth > 0);
                Assert.True(content.ActualHeight > 0);
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                completed = true;
            });
            Assert.False(reveal.IsCompleted);
            Assert.False(completed);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            window.Activate();
            await reveal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(completed);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
        }
        finally { window.Close(); }
    }

    /// <summary>Defers redirected activation until placement is ready during widget startup.</summary>
    [Fact]
    public async Task ManualActivationDuringEarlyStartupShowsTheSameWindow()
    {
        var window = new WinOptimizer.UI.MainWindow();
        try
        {
            var loader = window.Content;
            window.RestoreFromBackground();
            Assert.True(window.IsManualActivationRequested);
            Assert.False(window.AppWindow.IsVisible);
            await window.InitializeWindowPlacementAsync(Mock.Of<IUserPreferencesService>(), Mock.Of<ILogService>());
            window.RestoreFromBackground();
            await window.WaitForLoadingFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsManualActivationRequested);
            Assert.True(window.AppWindow.IsVisible);
            Assert.Same(loader, window.Content);
            Assert.Null(window.ViewModel);
        }
        finally { window.Close(); }
    }

    /// <summary>Checks the selected button's theme and bounds against the real scroll viewport.</summary>
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

            var colors = Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries
                .OfType<WinOptimizer.UI.Features.Common.Resources.AppColorResources>().Single();
            var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            foreach (var (theme, themeKey) in new[] { (ElementTheme.Light, "Light"), (ElementTheme.Dark, "Default") })
            {
                sidebar.RequestedTheme = theme;
                await StartupUiCoordinator.RunAfterFirstRenderAsync(sidebar, () => Task.CompletedTask);
                var palette = Assert.IsType<ResourceDictionary>(colors.ThemeDictionaries[highContrast ? "HighContrast" : themeKey]);
                var selected = Assert.IsType<SolidColorBrush>(palette["AccentSelectionBackgroundBrush"]);
                Assert.Equal(selected.Color, Assert.IsType<SolidColorBrush>(background.Background).Color);
            }

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
