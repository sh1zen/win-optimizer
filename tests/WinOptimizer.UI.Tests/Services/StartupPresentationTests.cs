using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Resources;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinOptimizer.UI.Features.Utilities;
using WinOptimizer.UI.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Checks the production startup frame boundary on a native WinUI dispatcher.</summary>
public sealed class StartupPresentationTests
{
    /// <summary>Checks the real hosts toolbar at both desktop and compact widths.</summary>
    [Fact]
    public async Task HostsEditorAlignsTheCountAndActionsAboveTheEditor()
    {
        var page = new HostsEditorPage();
        var window = new Window { Content = page };
        try
        {
            window.Activate();
            await page.ViewModel.LoadAsync();
            var toolbar = Assert.IsType<Grid>(page.FindName("HostsToolbar"));
            var count = Assert.IsType<TextBlock>(page.FindName("HostsEntryCount"));
            var actions = Assert.IsAssignableFrom<Panel>(page.FindName("HostsActions"));
            var entries = Assert.IsType<ListView>(page.FindName("HostsEntriesList"));
            var textMode = Assert.IsType<ToggleSwitch>(page.FindName("TextModeSwitch"));
            var editor = Assert.IsType<TextBox>(page.FindName("HostsContent"));
            Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(page)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

            foreach (var width in new[] { 1100d, 520d })
            {
                page.Width = width;
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
                page.UpdateLayout();
                Assert.Equal("2 entries", count.Text);
                Assert.InRange(Math.Abs(Bounds(count).Left - Bounds(toolbar).Left), 0, 1);
                Assert.InRange(Math.Abs(Bounds(actions).Right - Bounds(toolbar).Right), 0, 1);
                Assert.True(Bounds(count).Right < Bounds(actions).Left);
                Assert.InRange(Bounds(entries).Top - Bounds(toolbar).Bottom, 0, 12);
                Assert.All(actions.Children.OfType<FrameworkElement>().Where(control => control.Visibility == Visibility.Visible),
                    control => Assert.InRange(Bounds(control).Right, Bounds(actions).Left, Bounds(actions).Right + 1));
            }

            textMode.IsOn = true;
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            Assert.Equal(Visibility.Collapsed, count.Visibility);
            Assert.Equal(Visibility.Visible, editor.Visibility);
            Assert.InRange(Bounds(editor).Top - Bounds(toolbar).Bottom, 0, 12);
        }
        finally { window.Close(); }
    }

    /// <summary>Checks the environment toolbar and its live result count on the production page.</summary>
    [Fact]
    public async Task EnvironmentEditorAlignsTheFilteredCountAndActionsAboveTheList()
    {
        var page = new EnvironmentEditorPage();
        var window = new Window { Content = page };
        try
        {
            window.Activate();
            await page.ViewModel.LoadAsync();
            var toolbar = Assert.IsType<Grid>(page.FindName("VariablesToolbar"));
            var count = Assert.IsType<TextBlock>(page.FindName("VariableEntryCount"));
            var actions = Assert.IsAssignableFrom<Panel>(page.FindName("VariableActions"));
            var variables = Assert.IsType<ListView>(page.FindName("VariablesPane"));
            var search = Assert.IsType<TextBox>(page.FindName("VariableSearch"));
            Windows.Foundation.Rect Bounds(FrameworkElement element) => element.TransformToVisual(page)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

            foreach (var width in new[] { 1100d, 520d })
            {
                page.Width = width;
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
                page.UpdateLayout();
                Assert.Equal("1 entries", count.Text);
                Assert.InRange(Math.Abs(Bounds(count).Left - Bounds(toolbar).Left), 0, 1);
                Assert.InRange(Math.Abs(Bounds(actions).Right - Bounds(toolbar).Right), 0, 1);
                Assert.True(Bounds(count).Right < Bounds(actions).Left);
                Assert.InRange(Bounds(variables).Top - Bounds(toolbar).Bottom, 0, 12);
                Assert.All(actions.Children.OfType<FrameworkElement>().Where(control => control.Visibility == Visibility.Visible),
                    control => Assert.InRange(Bounds(control).Right, Bounds(actions).Left, Bounds(actions).Right + 1));
            }

            search.Text = "nonexistent";
            Assert.Empty(variables.Items);
            Assert.Equal("0 entries", count.Text);
            search.Text = "Path";
            Assert.Single(variables.Items);
            Assert.Equal("1 entries", count.Text);
        }
        finally { window.Close(); }
    }

    /// <summary>Checks fade completion and cancellation across committed frame navigation.</summary>
    [Fact]
    public async Task PageFadeSettlesAfterRapidNavigationAndSkipsDisabledAnimations()
    {
        var frame = new Frame();
        var window = new Window { Content = frame };
        try
        {
            using var transition = new PageFadeTransition(frame);
            var animate = false;
            frame.Navigated += (_, _) => transition.Play(animate);
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask);

            var visual = ElementCompositionPreview.GetElementVisual(frame);
            Assert.True(frame.Navigate(typeof(NavigationOtherPage), null, new SuppressNavigationTransitionInfo()));
            Assert.Null(visual.TryGetAnimationController(nameof(Visual.Opacity)));

            animate = true;
            Assert.True(frame.Navigate(typeof(NavigationDetailPage), null, new SuppressNavigationTransitionInfo()));
            Assert.NotNull(visual.TryGetAnimationController(nameof(Visual.Opacity)));
            frame.GoBack();
            Assert.IsType<NavigationOtherPage>(frame.Content);

            using var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            batch.Completed += (_, _) => completed.SetResult();
            frame.GoForward();
            batch.End();
            Assert.IsType<NavigationDetailPage>(frame.Content);
            Assert.NotNull(visual.TryGetAnimationController(nameof(Visual.Opacity)));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1f, visual.Opacity);

            frame.GoBack();
            Assert.NotNull(visual.TryGetAnimationController(nameof(Visual.Opacity)));
            animate = false;
            frame.GoForward();
            Assert.Null(visual.TryGetAnimationController(nameof(Visual.Opacity)));
            Assert.Equal(1f, visual.Opacity);
        }
        finally { window.Close(); }
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
            preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, It.IsAny<bool>()))
                .ReturnsAsync(OperationResult.Succeeded());
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
            using var background = new WindowBackgroundPreferencesViewModel(preferences.Object, localization.Object);
            Assert.True(await background.LoadAsync(saved));
            Assert.False(background.IsBlurEnabled);
            preferences.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, It.IsAny<bool>()), Times.Never);
            var colors = Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries.OfType<AppColorResources>().Single();
            window.InitializeShell(background, colors);
            var shell = Assert.IsType<Grid>(window.Content);
            var solidCanvas = Assert.IsType<Border>(shell.FindName("SolidCanvas"));
            Assert.Equal(Visibility.Visible, solidCanvas.Visibility);
            Assert.Null(window.SystemBackdrop);
            background.IsBlurEnabled = true;
            if (DesktopAcrylicController.IsSupported())
            {
                Assert.Equal(0.98f, Assert.IsType<TintedDesktopBackdrop>(window.SystemBackdrop).TintOpacity);
                Assert.Equal(Visibility.Collapsed, solidCanvas.Visibility);
            }
            else
            {
                Assert.Null(window.SystemBackdrop);
                Assert.Equal(Visibility.Visible, solidCanvas.Visibility);
            }
            background.IsBlurEnabled = false;
            Assert.Null(window.SystemBackdrop);
            Assert.Equal(Visibility.Visible, solidCanvas.Visibility);
            Assert.True(await background.FlushAsync());
            preferences.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.BlurWindowBackground, false), Times.Once);
            var frame = Assert.IsType<Frame>(shell.FindName("ContentFrame"));
            var navigation = Assert.IsType<NavigationThemeTransition>(Assert.Single(frame.ContentTransitions));
            Assert.IsType<SuppressNavigationTransitionInfo>(navigation.DefaultNavigationTransitionInfo);
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            var titleIcon = Assert.IsType<Image>(shell.FindName("TitleBarIcon"));
            var appName = Assert.IsType<TextBlock>(shell.FindName("TitleBarAppName"));
            var arrows = Assert.IsType<StackPanel>(shell.FindName("TitleBarNavigation"));
            var sidebar = Assert.IsType<NavSidebar>(shell.FindName("NavSidebar"));
            var sidebarSurface = Assert.IsType<Grid>(shell.FindName("SidebarSurface"));
            var separator = Assert.IsType<Microsoft.UI.Xaml.Shapes.Rectangle>(shell.FindName("SidebarSeparator"));
            Assert.Equal("WinOptimizer", appName.Text);
            Assert.Equal(new Uri(AppBranding.ImageUri), Assert.IsType<BitmapImage>(titleIcon.Source).UriSource);
            Assert.Equal(1, separator.Width);
            Assert.False(sidebarSurface.IsHitTestVisible);

            var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var palette = Assert.IsType<ResourceDictionary>(colors.ThemeDictionaries[highContrast ? "HighContrast" : "Default"]);
            Assert.Equal(Assert.IsType<SolidColorBrush>(palette["AppSurfaceBrush"]).Color,
                Assert.IsType<SolidColorBrush>(sidebarSurface.Background).Color);
            Assert.Equal(Assert.IsType<SolidColorBrush>(palette["AppSurfaceStrokeBrush"]).Color,
                Assert.IsType<SolidColorBrush>(separator.Fill).Color);

            foreach (var expanded in new[] { true, false, true })
            {
                sidebar.IsPaneOpen = expanded;
                await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);

                var iconBounds = Bounds(titleIcon);
                var nameBounds = Bounds(appName);
                var arrowsBounds = Bounds(arrows);
                var sidebarBounds = Bounds(sidebar);
                var separatorBounds = Bounds(separator);
                Assert.True(iconBounds.Width > 0);
                Assert.Equal(expanded ? Visibility.Visible : Visibility.Collapsed, appName.Visibility);
                Assert.InRange(arrowsBounds.Width, 69, 71);
                Assert.InRange(arrowsBounds.Height, 31, 33);
                Assert.InRange(Math.Abs(separatorBounds.Top - Bounds(shell).Top), 0, 1);
                Assert.InRange(Math.Abs(separatorBounds.Bottom - Bounds(shell).Bottom), 0, 1);
                if (shell.FlowDirection == FlowDirection.RightToLeft)
                {
                    Assert.InRange(Math.Abs(sidebarBounds.Left - separatorBounds.Left), 0, 1);
                    Assert.True(arrowsBounds.Right < separatorBounds.Left);
                    if (expanded) Assert.True(nameBounds.Right <= iconBounds.Left);
                }
                else
                {
                    Assert.InRange(Math.Abs(sidebarBounds.Right - separatorBounds.Right), 0, 1);
                    Assert.InRange(arrowsBounds.Left - separatorBounds.Right, 19, 21);
                    Assert.True(iconBounds.Right < separatorBounds.Left);
                    if (expanded) Assert.True(iconBounds.Right <= nameBounds.Left && nameBounds.Right < separatorBounds.Left);
                }
            }

            var actions = Assert.IsType<StackPanel>(shell.FindName("TitleBarButtons"));
            var updatesButton = Assert.IsType<Button>(shell.FindName("UpdatesButton"));
            Assert.Contains(updatesButton, actions.Children);
            var updatesMenu = Assert.IsType<Flyout>(updatesButton.Flyout);
            Assert.Equal(Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight, updatesMenu.Placement);
            var updateDetails = Assert.IsType<StackPanel>(updatesMenu.Content);
            Assert.Collection(updateDetails.Children,
                child => Assert.Equal("UpdateCurrentVersion", Assert.IsType<TextBlock>(child).Name),
                child => Assert.Equal("UpdateLatestVersion", Assert.IsType<TextBlock>(child).Name),
                child => Assert.Equal("UpdateCheckStatus", Assert.IsType<TextBlock>(child).Name),
                child => Assert.Equal("CheckForUpdatesAction", Assert.IsType<Button>(child).Name));
            updatesMenu.ShowAt(updatesButton);
            Assert.True(updatesMenu.IsOpen);
            updatesMenu.Hide();

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

    /// <summary>Defers redirected activation until the shell's saved placement is ready.</summary>
    [Fact]
    public async Task ManualActivationDuringEarlyStartupShowsTheSameWindow()
    {
        var window = new WinOptimizer.UI.MainWindow();
        try
        {
            var loader = window.Content;
            window.ActivateFromLaunch();
            Assert.False(window.AppWindow.IsVisible);
            await window.InitializeWindowPlacementAsync(Mock.Of<IUserPreferencesService>(), Mock.Of<ILogService>());
            window.ActivateFromLaunch();
            await window.WaitForLoadingFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
            button.Text = "Software & Apps";
            var background = Assert.IsType<Border>(button.FindName("BackgroundBorder"));
            var icon = Assert.IsType<Viewbox>(button.FindName("ButtonIcon"));
            var label = Assert.IsType<TextBlock>(button.FindName("ButtonText"));
            var indicator = Assert.IsType<Microsoft.UI.Xaml.Shapes.Rectangle>(button.FindName("SelectionIndicator"));
            var topDivider = Assert.IsType<Microsoft.UI.Xaml.Shapes.Rectangle>(sidebar.FindName("TopGroupDivider"));
            var bottomDivider = Assert.IsType<Microsoft.UI.Xaml.Shapes.Rectangle>(sidebar.FindName("BottomGroupDivider"));
            Assert.Equal(14, label.FontSize);

            var timeMachine = Assert.IsType<NavButton>(sidebar.GetButton("TimeMachine"));
            var timeMachineIcon = Assert.IsType<Microsoft.UI.Xaml.Shapes.Path>(timeMachine.FindName("IconPath"));
            var iconBounds = timeMachineIcon.Data.Bounds;
            Assert.InRange(iconBounds.Width, 16, 19);
            Assert.InRange(iconBounds.Height, 17.5, 19);
            Assert.InRange(iconBounds.X + iconBounds.Width / 2, 11, 13);
            Assert.InRange(iconBounds.Y + iconBounds.Height / 2, 11, 13);

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
                var stroke = Assert.IsType<SolidColorBrush>(palette["AppSurfaceStrokeBrush"]);
                Assert.Equal(stroke.Color, Assert.IsType<SolidColorBrush>(topDivider.Fill).Color);
                Assert.Equal(stroke.Color, Assert.IsType<SolidColorBrush>(bottomDivider.Fill).Color);
            }

            foreach (var expanded in new[] { true, false, true })
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
                var expandedVisibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                Assert.Equal(expandedVisibility, label.Visibility);
                Assert.Equal(expandedVisibility, indicator.Visibility);
                Assert.Equal(expandedVisibility, topDivider.Visibility);
                Assert.Equal(expandedVisibility, bottomDivider.Visibility);
                Assert.Equal(expanded ? 26 : 24, icon.Width);
                var iconOffset = icon.TransformToVisual(background).TransformPoint(default);
                if (expanded)
                {
                    Assert.Equal("Software & Apps", label.Text);
                    Assert.True(label.ActualWidth > 0);
                    var labelOffset = label.TransformToVisual(background).TransformPoint(default);
                    Assert.True(iconOffset.X + icon.ActualWidth < labelOffset.X);
                    Assert.InRange(Math.Abs(iconOffset.Y + icon.ActualHeight / 2 - labelOffset.Y - label.ActualHeight / 2), 0, 1);
                }
                else
                {
                    Assert.InRange(Math.Abs(iconOffset.X + icon.ActualWidth / 2 - background.ActualWidth / 2), 0, 1);
                }
            }

            // Exercise wrapped labels at a larger text size without changing Windows preferences.
            var standardHeight = background.ActualHeight;
            button.Text = "Advanced Tools";
            label.FontSize = 24;
            sidebar.UpdateLayout();
            Assert.False(label.IsTextTrimmed);
            Assert.True(background.ActualHeight > standardHeight);

            sidebar.SelectedTag = "Optimize";
            var optimize = Assert.IsType<NavButton>(sidebar.GetButton("Optimize"));
            var optimizeIndicator = Assert.IsType<Microsoft.UI.Xaml.Shapes.Rectangle>(optimize.FindName("SelectionIndicator"));
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);
            Assert.Equal(Visibility.Visible, optimizeIndicator.Visibility);

            sidebar.SelectedTag = "Internet";
            var internet = Assert.IsType<NavButton>(sidebar.GetButton("Internet"));
            var internetIndicator = Assert.IsType<Microsoft.UI.Xaml.Shapes.Rectangle>(internet.FindName("SelectionIndicator"));
            Assert.Equal(Visibility.Collapsed, optimizeIndicator.Visibility);
            Assert.Equal(Visibility.Visible, internetIndicator.Visibility);
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
