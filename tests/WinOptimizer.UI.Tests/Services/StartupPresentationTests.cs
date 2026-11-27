using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Benchmark;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Disks;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.UI.Features.AdvancedTools;
using WinOptimizer.UI.Features.Benchmark;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Resources;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Dashboard;
using WinOptimizer.UI.Features.Disks;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinOptimizer.UI.Features.Utilities;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.ViewModels;
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
            var titleBar = Assert.IsType<Grid>(shell.FindName("AppTitleBar"));
            var search = Assert.IsType<ProgramSearch>(shell.FindName("TitleBarSearch"));
            var searchBox = Assert.IsType<AutoSuggestBox>(search.FindName("ProgramSearchBox"));
            var actions = Assert.IsType<StackPanel>(shell.FindName("TitleBarButtons"));
            var nonClientInput = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id);
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
                var searchBounds = Bounds(search);
                var actionsBounds = Bounds(actions);
                var sidebarBounds = Bounds(sidebar);
                var separatorBounds = Bounds(separator);
                Assert.True(iconBounds.Width > 0);
                Assert.Equal(expanded ? Visibility.Visible : Visibility.Collapsed, appName.Visibility);
                Assert.InRange(arrowsBounds.Width, 69, 71);
                Assert.InRange(arrowsBounds.Height, 31, 33);
                Assert.InRange(searchBounds.Width, 1, 620);
                Assert.Equal(searchBounds, Bounds(searchBox));
                Assert.InRange(Math.Abs(searchBounds.Top + searchBounds.Height / 2 - titleBar.ActualHeight / 2), 0, 1);
                var scale = shell.XamlRoot.RasterizationScale;
                var searchRegion = new Windows.Graphics.RectInt32(
                    (int)Math.Round(searchBounds.X * scale), (int)Math.Round(searchBounds.Y * scale),
                    (int)Math.Round(searchBounds.Width * scale), (int)Math.Round(searchBounds.Height * scale));
                Assert.Contains(searchRegion, nonClientInput.GetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough));
                Assert.InRange(Math.Abs(separatorBounds.Top - Bounds(shell).Top), 0, 1);
                Assert.InRange(Math.Abs(separatorBounds.Bottom - Bounds(shell).Bottom), 0, 1);
                if (shell.FlowDirection == FlowDirection.RightToLeft)
                {
                    Assert.True(actionsBounds.Right < searchBounds.Left && searchBounds.Right < arrowsBounds.Left);
                    Assert.InRange(Math.Abs(sidebarBounds.Left - separatorBounds.Left), 0, 1);
                    Assert.True(arrowsBounds.Right < separatorBounds.Left);
                    if (expanded) Assert.True(nameBounds.Right <= iconBounds.Left);
                }
                else
                {
                    Assert.True(arrowsBounds.Right < searchBounds.Left && searchBounds.Right < actionsBounds.Left);
                    Assert.InRange(Math.Abs(sidebarBounds.Right - separatorBounds.Right), 0, 1);
                    Assert.InRange(arrowsBounds.Left - separatorBounds.Right, 19, 21);
                    Assert.True(iconBounds.Right < separatorBounds.Left);
                    if (expanded) Assert.True(iconBounds.Right <= nameBounds.Left && nameBounds.Right < separatorBounds.Left);
                }
            }

            var taskActivity = Assert.IsType<TaskProgressControl>(shell.FindName("TaskActivity"));
            Assert.Contains(taskActivity, actions.Children);
            Assert.Equal(Visibility.Collapsed, taskActivity.Visibility);
            var viewport = Assert.IsType<Grid>(shell.FindName("ZoomViewport"));
            Assert.InRange(Math.Abs(Bounds(frame).Bottom - Bounds(viewport).Bottom), 0, 1);
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

    /// <summary>Runs the actual title-bar check button and observes the shell overlay and native flyout.</summary>
    [Fact]
    public async Task TitleBarVersionMenuStaysOpenWhenTheUpdateToastAppears()
    {
        var versions = new Mock<IVersionService>(MockBehavior.Strict);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<VersionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        versions.Setup(service => service.CheckForUpdateAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            started.TrySetResult();
            return release.Task;
        });
        var localization = Mock.Of<ILocalizationService>();
        using var updates = new UpdateCheckViewModel(versions.Object, localization, Mock.Of<ILogService>(),
            Mock.Of<IBackgroundUpdateCheckService>(), new ExtensionTestDispatcher(), Mock.Of<IApplicationCloseService>());
        using var background = new WindowBackgroundPreferencesViewModel(Mock.Of<IUserPreferencesService>(), localization);
        var window = new WinOptimizer.UI.MainWindow();
        try
        {
            window.InitializeLoadingPresentation(localization);
            window.Activate();
            await window.WaitForLoadingFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var colors = Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries.OfType<AppColorResources>().Single();
            window.InitializeShell(background, colors);
            var shell = Assert.IsType<Grid>(window.Content);
            Assert.IsType<Grid>(shell.FindName("LoadingOverlay")).Visibility = Visibility.Collapsed;
            var toast = Assert.IsType<UpdateToast>(shell.FindName("UpdateNotification"));
            var notification = Assert.IsType<InfoBar>(toast.Content);
            toast.ViewModel = updates;
            var anchor = Assert.IsType<Button>(shell.FindName("UpdatesButton"));
            var menu = Assert.IsType<Flyout>(anchor.Flyout);
            var check = Assert.Single(Assert.IsType<StackPanel>(menu.Content).Children.OfType<Button>());
            check.Command = updates.CheckForUpdatesCommand;
            var viewport = Assert.IsType<Grid>(shell.FindName("ZoomViewport"));

            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            var viewportSize = viewport.ActualSize;
            var menuOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            menu.Opened += (_, _) => menuOpened.TrySetResult();
            menu.ShowAt(anchor);
            await menuOpened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            ((IInvokeProvider)new ButtonAutomationPeer(check).GetPattern(PatternInterface.Invoke)).Invoke();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(updates.IsUpdateCheckInProgress);
            Assert.True(menu.IsOpen);
            Assert.False(notification.IsOpen);

            release.SetResult(new VersionInfo { Version = "v25.05.01", IsUpdateAvailable = false });
            await updates.CheckForUpdatesCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(3));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            Assert.True(notification.IsOpen);
            Assert.Equal("No Updates Available", notification.Title);
            Assert.True(menu.IsOpen);
            Assert.Equal(viewportSize, viewport.ActualSize);
            Assert.InRange(toast.ActualWidth, 1, 400);
        }
        finally { window.Close(); }
    }

    /// <summary>Exercises the shared task commands through the native title-bar popup.</summary>
    [Fact]
    public async Task TitleBarTaskActivityShowsOutputAndCancelsTheActiveOperation()
    {
        var log = Mock.Of<ILogService>();
        var tasks = new TaskProgressService(log);
        var dispatcher = new Mock<IDispatcherService>();
        dispatcher.Setup(service => service.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => action());
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(service => service.ShowTaskOutputDialogAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns(Task.CompletedTask);
        using var model = new TaskProgressViewModel(tasks, dispatcher.Object, dialogs.Object, localization.Object, log);
        var control = new TaskProgressControl { ViewModel = model, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var shell = new Grid();
        shell.Children.Add(control);
        var window = new Window { Content = shell };
        try
        {
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            Assert.Equal(Visibility.Collapsed, control.Visibility);

            var cancellation = tasks.StartTask("Driver download");
            tasks.UpdateDetailedProgress(new() { StatusText = "Downloading package", TerminalOutput = "Downloaded fixture bytes", Progress = 35 });
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            Assert.Equal(Visibility.Visible, control.Visibility);
            var anchor = Assert.IsType<Button>(control.FindName("TaskStatusButton"));
            var output = Assert.IsType<Button>(control.FindName("TaskOutputAction"));
            var cancel = Assert.IsType<Button>(control.FindName("TaskCancelAction"));
            var dismiss = Assert.IsType<Button>(control.FindName("TaskDismissAction"));
            var flyout = Assert.IsType<Flyout>(anchor.Flyout);

            async Task WaitForFlyoutAsync(bool opening, Action change)
            {
                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChanged(object? sender, object args) => changed.TrySetResult();
                if (opening) flyout.Opened += OnChanged;
                else flyout.Closed += OnChanged;
                try
                {
                    change();
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                finally
                {
                    if (opening) flyout.Opened -= OnChanged;
                    else flyout.Closed -= OnChanged;
                }
            }
            void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

            await WaitForFlyoutAsync(true, () => flyout.ShowAt(anchor));
            Assert.True(flyout.IsOpen);
            Assert.Equal(Visibility.Visible, cancel.Visibility);
            Assert.Equal(Visibility.Collapsed, dismiss.Visibility);

            await WaitForFlyoutAsync(false, () => Invoke(output));
            dialogs.Verify(service => service.ShowTaskOutputDialogAsync("Dialog_TerminalOutput_Title",
                It.Is<IReadOnlyList<string>>(lines => lines.SequenceEqual(new[] { "Downloaded fixture bytes" }))), Times.Once);

            await WaitForFlyoutAsync(true, () => flyout.ShowAt(anchor));
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellation.Token.Register(() => cancelled.TrySetResult());
            Invoke(cancel);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForFlyoutAsync(false, tasks.CompleteTask);
            Assert.Equal(Visibility.Collapsed, control.Visibility);
            Assert.False(flyout.IsOpen);

            tasks.StartTask("Generate XML", true, showInTaskBar: true, canCancel: false);
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask);
            await WaitForFlyoutAsync(true, () => flyout.ShowAt(anchor));
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
            tasks.UpdateDetailedProgress(new() { LogLevel = LogLevel.Error });
            tasks.CompleteTask();
            Assert.Equal(Visibility.Visible, dismiss.Visibility);
            await WaitForFlyoutAsync(false, () => Invoke(dismiss));
            Assert.Equal(Visibility.Collapsed, control.Visibility);
            Assert.False(flyout.IsOpen);
        }
        finally { window.Close(); }
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

    /// <summary>Checks the first WinUI page, its responsive cards, and an existing quick-action route.</summary>
    [Fact]
    public async Task StartupShowsDashboardAndQuickActionUsesShellHistory()
    {
        var frame = new Frame();
        var sidebar = new NavSidebar();
        var overlay = new Grid();
        var root = new Grid();
        root.Children.Add(frame);
        root.Children.Add(overlay);
        var window = new Window { Content = root };
        var coordinator = new StartupUiCoordinator(root.DispatcherQueue, Mock.Of<ILogService>());
        var orchestrator = WinOptimizer.UI.App.Services.GetRequiredService<Mock<IStartupOrchestrator>>();
        orchestrator.Invocations.Clear();
        var inventory = WinOptimizer.UI.App.Services.GetRequiredService<Mock<ISystemInformationService>>();
        inventory.Invocations.Clear();
        var dashboardClock = WinOptimizer.UI.App.Services.GetRequiredService<Mock<TimeProvider>>();
        dashboardClock.Setup(clock => clock.GetTimestamp()).Returns(0L);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Activate();
            await coordinator.RunStartupAndCompleteAsync(new TextBlock(), frame, sidebar, overlay, root,
                () => null, () => ready.TrySetResult());
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var page = Assert.IsType<DashboardPage>(frame.Content);
            Assert.Equal(NavigationRouter.DashboardTag, sidebar.SelectedTag);
            Assert.NotNull(sidebar.GetButton(NavigationRouter.DashboardTag));
            Assert.False(frame.CanGoBack);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);

            var cards = Assert.IsType<ItemsControl>(page.FindName("ResourceCards"));
            var actions = Assert.IsType<ItemsControl>(page.FindName("QuickActions"));
            for (var attempt = 0; attempt < 3; attempt++)
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));

            orchestrator.Verify(service => service.RunDeferredInitializationAsync(), Times.Once);
            Assert.Equal([27d, 50d, 75d], page.ViewModel.Metrics.Select(metric => metric.Percent));
            Assert.Contains("Example processor", page.ViewModel.GetCopyText());
            Assert.Contains("Example graphics adapter", page.ViewModel.GetCopyText());
            Assert.Contains("31.0.1", page.ViewModel.GetCopyText());
            Assert.Contains("Example manufacturer · Model One", page.ViewModel.GetCopyText());
            Assert.Contains("SystemInfo_Field_Cores · 8", page.ViewModel.Metrics[0].Detail);
            Assert.Contains("MHz", page.ViewModel.Metrics[0].Detail);
            Assert.Contains("SystemInfo_Field_AvailableMemory · 50 B", page.ViewModel.Metrics[1].Detail);
            Assert.Contains("NTFS · Example volume", page.ViewModel.Metrics[2].Detail);
            Assert.True(page.ViewModel.CanCopy);

            foreach (var width in new[] { 850d, 390d })
            {
                page.Width = width;
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
                page.UpdateLayout();
                var first = Assert.IsAssignableFrom<FrameworkElement>(cards.ContainerFromIndex(0));
                var second = Assert.IsAssignableFrom<FrameworkElement>(cards.ContainerFromIndex(1));
                var top = second.TransformToVisual(first).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (width == 850) Assert.InRange(Math.Abs(top), 0, 1);
                else Assert.InRange(top - first.ActualHeight, 15, 17);
            }

            var repair = page.ViewModel.Actions.Single(action => action.Destination == typeof(WindowsStatusPage));
            var container = Assert.IsAssignableFrom<FrameworkElement>(actions.ContainerFromItem(repair));
            var panel = VisualTreeHelper.GetChild(container, 0);
            var button = panel as Button ?? Assert.IsType<Button>(VisualTreeHelper.GetChild(panel, 0));
            var provider = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
            provider.Invoke();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<WindowsStatusPage>(frame.Content);
            Assert.True(frame.CanGoBack);
            frame.GoBack();
            Assert.Same(page, frame.Content);
            for (var attempt = 0; attempt < 3; attempt++)
                await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            inventory.Verify(service => service.ReadSectionsAsync(It.IsAny<IReadOnlyCollection<SystemInformationCategory>>(), It.IsAny<CancellationToken>()), Times.Once);

            dashboardClock.Setup(clock => clock.GetTimestamp()).Returns(TimeSpan.FromSeconds(31).Ticks);
            frame.GoForward();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            frame.GoBack();
            for (var attempt = 0; attempt < 3; attempt++)
                await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            inventory.Verify(service => service.ReadSectionsAsync(It.IsAny<IReadOnlyCollection<SystemInformationCategory>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void DashboardGaugeUpdatesAtDisplayedPrecision()
    {
        var gauge = new DashboardMetric(FluentIcons.Common.Icon.Ram);
        var changed = new List<string?>();
        gauge.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        gauge.UpdateUsage(27.1);
        Assert.Equal(27, gauge.Percent);
        Assert.Equal([nameof(DashboardMetric.Percent), nameof(DashboardMetric.Readout)], changed);
        changed.Clear();
        gauge.UpdateUsage(27.3);
        Assert.Empty(changed);

        gauge.UpdateUsage(0);
        Assert.NotEqual("—", gauge.Readout);
        gauge.UpdateUsage(null);
        Assert.Equal(0, gauge.Percent);
        Assert.Equal("—", gauge.Readout);
    }

    [Fact]
    public async Task TitleBarSearchMatchesTranslatedNamesAndNavigatesAcrossPages()
    {
        var localization = WinOptimizer.UI.App.Services.GetRequiredService<ILocalizationService>();
        using var background = new WindowBackgroundPreferencesViewModel(Mock.Of<IUserPreferencesService>(), localization);
        var window = new WinOptimizer.UI.MainWindow();
        try
        {
            window.Activate();
            await window.WaitForLoadingFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var colors = Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries.OfType<AppColorResources>().Single();
            window.InitializeShell(background, colors);
            var shell = Assert.IsType<Grid>(window.Content);
            Assert.IsType<Grid>(shell.FindName("LoadingOverlay")).Visibility = Visibility.Collapsed;
            var frame = window.NavigationFrame;
            Assert.True(frame.Navigate(typeof(WindowsStatusPage)));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            var titleBar = Assert.IsType<Grid>(shell.FindName("AppTitleBar"));
            var titleBarSearch = Assert.IsType<ProgramSearch>(titleBar.FindName("TitleBarSearch"));
            var search = Assert.IsType<AutoSuggestBox>(titleBarSearch.FindName("ProgramSearchBox"));
            var overlay = Assert.IsType<ProgramSearchResults>(shell.FindName("SearchResultsOverlay"));
            var resultsList = Assert.IsType<ListView>(overlay.FindName("ResultsList"));
            var backdrop = Assert.IsType<Grid>(overlay.FindName("ResultsBackdrop"));
            var status = Assert.IsType<TextBlock>(overlay.FindName("SearchStatus"));
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            search.ApplyTemplate();
            search.UpdateLayout();

            static IEnumerable<DependencyObject> Descendants(DependencyObject root)
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
                {
                    var child = VisualTreeHelper.GetChild(root, index);
                    yield return child;
                    foreach (var descendant in Descendants(child)) yield return descendant;
                }
            }

            var queryButton = Descendants(search).OfType<Button>().Single(button => button.Name == "QueryButton");
            var input = Descendants(search).OfType<TextBox>().Single(box => box.Name == "TextBox");

            static void Invoke(AutomationPeer peer) =>
                Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();

            async Task NavigateAsync(Type pageType, Action invoke)
            {
                var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
                {
                    if (args.SourcePageType == pageType) navigated.TrySetResult();
                }

                frame.Navigated += OnNavigated;
                try
                {
                    invoke();
                    await navigated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.IsType(pageType, frame.Content);
                    Assert.True(search.IsLoaded);
                    Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                }
                finally { frame.Navigated -= OnNavigated; }
            }

            await NavigateAsync(typeof(DashboardPage), () =>
            {
                search.Text = "Nav_Dashboard";
                Invoke(new ButtonAutomationPeer(queryButton));
            });

            static async Task WaitUntilAsync(Func<bool> ready)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!ready()) await Task.Delay(15, deadline.Token);
            }

            var dashboard = frame.Content;
            Assert.True(input.Focus(FocusState.Programmatic));
            input.Text = "reparacion";
            await WaitUntilAsync(() => resultsList.ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: Button });
            Assert.Same(dashboard, frame.Content);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.False(search.IsSuggestionListOpen);
            Assert.InRange(Math.Abs(backdrop.ActualWidth - shell.ActualWidth), 0, 1);
            Assert.InRange(Math.Abs(backdrop.ActualHeight + titleBar.ActualHeight - shell.ActualHeight), 0, 1);
            Assert.InRange(Math.Abs(backdrop.TransformToVisual(shell).TransformPoint(default).Y - titleBar.ActualHeight), 0, 1);
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                Assert.IsType<SolidColorBrush>(backdrop.Background);
            else
                Assert.IsType<AcrylicBrush>(backdrop.Background);

            Assert.True(overlay.FocusFirstResult());
            var first = Assert.IsType<ListViewItem>(resultsList.ContainerFromIndex(0));
            var resultButton = Assert.IsType<Button>(first.ContentTemplateRoot);
            await NavigateAsync(typeof(WindowsStatusPage), () => Invoke(new ButtonAutomationPeer(resultButton)));
            frame.GoBack();
            Assert.IsType<DashboardPage>(frame.Content);
            frame.GoBack();
            Assert.IsType<WindowsStatusPage>(frame.Content);
            frame.GoForward();
            frame.GoForward();
            Assert.IsType<WindowsStatusPage>(frame.Content);

            input.Text = "no-matching-program-item";
            await WaitUntilAsync(() => status.Text == "Dashboard_SearchNoResults");
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Empty(resultsList.Items);
            var currentPage = frame.Content;
            var close = Assert.IsType<Button>(overlay.FindName("CloseButton"));
            Invoke(new ButtonAutomationPeer(close));
            await WaitUntilAsync(() => overlay.Visibility == Visibility.Collapsed);
            Assert.Same(currentPage, frame.Content);
            Assert.Empty(input.Text);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task ProgramSearchUsesCurrentLabelsAndRoutesOnlyAvailableSettings()
    {
        var definition = new SettingDefinition { Id = "power-test-option", Name = "Example power option", Description = "Example description" };
        var registry = new Mock<ICompatibleSettingsRegistry>();
        registry.SetupGet(service => service.IsInitialized).Returns(true);
        registry.Setup(service => service.GetAllBypassedSettings()).Returns(new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            [FeatureIds.Power] = [definition]
        });
        registry.Setup(service => service.GetById(definition.Id)).Returns(definition);
        var mode = new Mock<IApplicationModeService>();
        var localizer = new Mock<ILocalizationService>();
        localizer.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => $"[{key}]");
        localizer.Setup(service => service.GetString("Setting_power-test-option_Name")).Returns("Opzione risparmio energia");
        localizer.Setup(service => service.ReadTranslations(It.IsAny<IReadOnlySet<string>>())).Returns(new Dictionary<string, IReadOnlyList<string>>
        {
            ["Setting_power-test-option_Name"] = ["Opzione risparmio energia", "省電力オプション"],
            ["Nav_Restore"] = ["Restauración"],
            ["Settings_Menu_Language"] = ["Język"]
        });
        var search = new ProgramSearchService(localizer.Object, registry.Object, mode.Object);

        var setting = Assert.Single(await search.FindAsync("省電力", default));
        Assert.Equal("Opzione risparmio energia", setting.Title);
        var route = Assert.IsType<SectionPageNavigation>(setting.Entry.Parameter);
        Assert.Equal("Power", route.SectionKey);
        Assert.Same(definition, route.Setting);
        registry.Setup(service => service.GetById(definition.Id)).Returns((SettingDefinition?)null);
        Assert.Empty(await search.FindAsync("省電力", default));
        Assert.Single(await search.FindAsync("restauracion", default));
        mode.SetupGet(service => service.CurrentMode).Returns(WinOptimizerMode.ConfigReview);
        Assert.Empty(await search.FindAsync("restauracion", default));
        Assert.Equal(SettingsCategory.General, Assert.IsType<WinOptimizer.UI.Features.Settings.SettingsPageNavigation>(
            Assert.Single(await search.FindAsync("język", default)).Entry.Parameter).Category);
        localizer.Verify(service => service.ReadTranslations(It.IsAny<IReadOnlySet<string>>()), Times.Once);
    }

    /// <summary>Opens Bench from the Dashboard and exercises its native controls with synthetic measurements.</summary>
    [Fact]
    public async Task DashboardBenchRunsIndividualAndCombinedTestsAndCancels()
    {
        var service = WinOptimizer.UI.App.Services.GetRequiredService<Mock<IBenchmarkService>>();
        service.Reset();
        service.Setup(benchmark => benchmark.ReadSystemAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new BenchmarkSystemInfo(
            [new(SystemInformationCategory.Processors, [new("Example CPU", [])], null)],
            new(6, 141, 1, ["SSE2", "AVX2"]),
            [new("Example GPU", 8UL * 1024 * 1024 * 1024, 5), new("Secondary GPU", 4UL * 1024 * 1024 * 1024, 11)],
            [@"M:\", @"N:\"]));
        var completedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var calls = new List<BenchmarkRequest>();
        var flopsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unfinishedFlops = new TaskCompletionSource<BenchmarkMeasurement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseFlops = false;
        var failGpu = false;
        service.Setup(benchmark => benchmark.RunAsync(It.IsAny<BenchmarkRequest>(), It.IsAny<CancellationToken>()))
            .Returns((BenchmarkRequest request, CancellationToken token) =>
            {
                calls.Add(request);
                if (request.Kind == BenchmarkKind.GpuFlops && failGpu)
                    return Task.FromException<BenchmarkMeasurement>(new BenchmarkException(BenchmarkFailure.GraphicsUnavailable));
                if (request.Kind == BenchmarkKind.CpuFlops && pauseFlops)
                {
                    flopsStarted.TrySetResult();
                    return unfinishedFlops.Task.WaitAsync(token);
                }
                return Task.FromResult(new BenchmarkMeasurement(request.Kind == BenchmarkKind.CpuClock ? 2500 : 12_345_000_000, completedAt));
            });

        var frame = new Frame();
        var window = new Window { Content = frame };
        try
        {
            Assert.True(frame.Navigate(typeof(DashboardPage)));
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            var dashboard = Assert.IsType<DashboardPage>(frame.Content);
            var actions = Assert.IsType<ItemsControl>(dashboard.FindName("QuickActions"));
            var action = Assert.Single(dashboard.ViewModel.Actions, item => item.Destination == typeof(BenchmarkPage));
            Invoke(FindButton(actions.ContainerFromItem(action)));
            await WaitForAsync(() => frame.Content is BenchmarkPage);
            var page = Assert.IsType<BenchmarkPage>(frame.Content);
            var viewModel = page.ViewModel;
            await WaitForAsync(() => viewModel.DriveRoots.Count == 2);
            Assert.Contains(viewModel.HardwareFacts, fact => fact.Value == "Example CPU");
            Assert.Contains(viewModel.HardwareFacts, fact => fact.Value.Contains("Example GPU"));
            Assert.Empty(calls);

            var router = new NavigationRouter(null, null, new ExtensionTestDispatcher());
            Assert.Equal(NavigationRouter.DashboardTag, router.GetTagForCurrentPage(typeof(BenchmarkPage)));
            var search = WinOptimizer.UI.App.Services.GetRequiredService<ProgramSearchService>();
            Assert.Contains(await search.FindAsync("Benchmark_Title", default), result => result.Entry.PageType == typeof(BenchmarkPage));

            var picker = Assert.IsType<ComboBox>(page.FindName("DrivePicker"));
            picker.SelectedItem = @"N:\";
            Assert.Equal(@"N:\", viewModel.SelectedDrive);
            var graphicsPicker = Assert.IsType<ComboBox>(page.FindName("GraphicsAdapterPicker"));
            graphicsPicker.SelectedIndex = 1;
            Assert.Equal(11UL, viewModel.SelectedGraphicsAdapter?.Id);
            var rows = Assert.IsType<ItemsControl>(page.FindName("ResultCards"));
            var runAll = Assert.IsType<Button>(page.FindName("RunAllButton"));
            var cancel = Assert.IsType<Button>(page.FindName("CancelButton"));
            var scale = frame.XamlRoot.RasterizationScale;
            foreach (var width in new[] { 1000, 390 })
            {
                window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(680 * scale)));
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
                page.UpdateLayout();
                var first = Assert.IsAssignableFrom<FrameworkElement>(rows.ContainerFromIndex(0));
                var second = Assert.IsAssignableFrom<FrameworkElement>(rows.ContainerFromIndex(1));
                var top = second.TransformToVisual(first).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (width == 1000) Assert.InRange(Math.Abs(top), 0, 1);
                else Assert.InRange(top - first.ActualHeight, 9, 11);
                Assert.InRange(Assert.IsType<ScrollView>(page.FindName("BenchScrollViewer")).ScrollableWidth, 0, 1);
            }

            Invoke(FindButton(rows.ContainerFromIndex(0)));
            await WaitForAsync(() => calls.Count == 1 && !viewModel.IsRunning);
            Assert.Equal(BenchmarkKind.CpuClock, calls[0].Kind);
            Assert.EndsWith(" GHz", viewModel.Rows[0].Result);
            Assert.Contains(Descendants(rows.ContainerFromIndex(0)).OfType<TextBlock>(),
                text => text.Text == viewModel.Rows[0].Result);
            calls.Clear();

            Invoke(runAll);
            await WaitForAsync(() => calls.Count == 5 && !viewModel.IsRunning);
            Assert.Equal(Enum.GetValues<BenchmarkKind>(), calls.Select(call => call.Kind));
            Assert.Equal(@"N:\", Assert.Single(calls, call => call.Kind == BenchmarkKind.DiskWrite).DriveRoot);
            Assert.Equal(11UL, Assert.Single(calls, call => call.Kind == BenchmarkKind.GpuFlops).GraphicsAdapterId);
            Assert.EndsWith(" GFLOPS", viewModel.Rows[1].Result);
            Assert.EndsWith(" GB/s", viewModel.Rows[2].Result);
            Assert.EndsWith(" MB/s", viewModel.Rows[3].Result);
            Assert.EndsWith(" GFLOPS", viewModel.Rows[4].Result);
            Assert.Equal("Secondary GPU", viewModel.Rows[4].Detail);

            failGpu = true;
            calls.Clear();
            Invoke(FindButton(rows.ContainerFromIndex(4)));
            await WaitForAsync(() => calls.Count == 1 && !viewModel.IsRunning);
            Assert.Equal(new BenchmarkRequest(BenchmarkKind.GpuFlops, GraphicsAdapterId: 11), calls[0]);
            Assert.Equal("Benchmark_GpuUnavailable", viewModel.Rows[4].Detail);

            pauseFlops = true;
            calls.Clear();
            Invoke(runAll);
            await flopsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(runAll.IsEnabled);
            Invoke(cancel);
            await WaitForAsync(() => !viewModel.IsRunning);
            Assert.Equal(new[] { BenchmarkKind.CpuClock, BenchmarkKind.CpuFlops }, calls.Select(call => call.Kind));
            Assert.EndsWith(" GHz", viewModel.Rows[0].Result);
            Assert.True(frame.CanGoBack);
            frame.GoBack();
            Assert.Same(dashboard, frame.Content);
        }
        finally { window.Close(); }

        static void Invoke(Button button) =>
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

        static Button FindButton(DependencyObject root)
        {
            if (root is Button button) return button;
            Assert.IsAssignableFrom<FrameworkElement>(root).UpdateLayout();
            return Assert.Single(Descendants(root).OfType<Button>());
        }

        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        static async Task WaitForAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition()) await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Checks that the Extension sidebar button opens its own page and follows the shell journal.</summary>
    [Fact]
    public async Task ExtensionAppearsAboveAdvancedToolsAndUsesTheShellJournal()
    {
        var frame = new Frame();
        var sidebar = new NavSidebar();
        var shell = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children = { sidebar, frame }
        };
        Grid.SetColumn(frame, 1);
        var window = new Window { Content = shell };
        try
        {
            var router = new NavigationRouter(null, null, new ExtensionTestDispatcher());
            sidebar.ItemClicked += (_, args) => router.NavigateToPage(frame, args.NavigationTag as string);
            frame.Navigated += (_, args) => sidebar.SelectedTag = router.GetTagForCurrentPage(args.SourcePageType);
            router.NavigateToPage(frame, "AdvancedTools");
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(shell, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));

            var bottomPanel = Assert.IsType<StackPanel>(sidebar.FindName("BottomNavPanel"));
            Assert.Equal(new[] { "TimeMachine", "Functionalities", "AdvancedTools", "Settings" },
                bottomPanel.Children.OfType<NavButton>().Select(button => Assert.IsType<string>(button.NavigationTag)));
            var extensionsButton = Assert.IsType<NavButton>(sidebar.GetButton("Functionalities"));
            new NavButtonAutomationPeer(extensionsButton).Invoke();
            Assert.IsType<UtilitiesPage>(frame.Content);
            Assert.True(extensionsButton.IsSelected);

            var search = WinOptimizer.UI.App.Services.GetRequiredService<ProgramSearchService>();
            var extensions = Assert.Single(await search.FindAsync("Nav_Functionalities", default));
            Assert.Equal(typeof(UtilitiesPage), extensions.Entry.PageType);
            Assert.Empty(extensions.Section);
            frame.GoBack();
            Assert.IsType<AdvancedToolsPage>(frame.Content);
            Assert.Equal("AdvancedTools", sidebar.SelectedTag);
            frame.GoForward();
            Assert.IsType<UtilitiesPage>(frame.Content);
            Assert.True(extensionsButton.IsSelected);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task DiskCardsOpenDedicatedToolsInTheShellJournal()
    {
        var service = WinOptimizer.UI.App.Services.GetRequiredService<Mock<IDiskToolsService>>();
        service.Setup(tools => tools.GetUsbDisksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<UsbDisk>());
        service.Setup(tools => tools.GetVolumesAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        (string title, string panel)[] tools =
        [
            ("Disks_Delete", "DeletePanel"),
            ("Disks_Permissions", "PermissionsPanel"),
            ("Disks_Shadow", "ShadowPanel"),
            ("Disks_Hash", "HashPanel"),
            ("Disks_Usb", "UsbPanel"),
            ("Disks_Recovery", "RecoveryPanel"),
            ("Disks_Optimize", "OptimizePanel")
        ];
        var frame = new Frame();
        var window = new Window { Content = frame };
        var router = new NavigationRouter(null, null, new ExtensionTestDispatcher());
        try
        {
            router.NavigateToPage(frame, "Disks");
            window.Activate();
            for (var index = 0; index < tools.Length; index++)
            {
                var overview = Assert.IsType<DisksPage>(frame.Content);
                await StartupUiCoordinator.RunAfterFirstRenderAsync(overview, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
                var cards = Assert.IsType<StackPanel>(overview.FindName("ToolCards")).Children.Cast<NavigationCard>().ToArray();
                Assert.Equal(tools.Select(entry => entry.title), cards.Select(card => card.Title));
                Assert.All(cards, card => Assert.NotNull(card.Icon));
                var control = Assert.IsType<CommunityToolkit.WinUI.Controls.SettingsCard>(cards[index].FindName("Card"));
                Assert.True(control.IsClickEnabled);
                var command = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(control.Command);
                Assert.True(command.CanExecute(control.CommandParameter));
                command.Execute(control.CommandParameter);
                var detail = Assert.IsType<DiskToolPage>(frame.Content);
                await StartupUiCoordinator.RunAfterFirstRenderAsync(detail, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("Disks", router.GetTagForCurrentPage(frame.CurrentSourcePageType));
                Assert.Equal(tools[index].title, Assert.IsType<PageHeader>(detail.FindName("ToolHeader")).Title);
                foreach (var (_, panel) in tools)
                    Assert.Equal(panel == tools[index].panel ? Visibility.Visible : Visibility.Collapsed,
                        Assert.IsAssignableFrom<FrameworkElement>(detail.FindName(panel)).Visibility);

                Assert.True(frame.CanGoBack);
                frame.GoBack();
                Assert.IsType<DisksPage>(frame.Content);
            }

            Assert.True(frame.CanGoForward);
            frame.GoForward();
            Assert.Equal("Disks_Optimize", Assert.IsType<DiskToolPage>(frame.Content).ToolTitle);
            router.NavigateToPage(frame, "Disks");
            Assert.IsType<DisksPage>(frame.Content);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task DisksSearchOpensTheHashToolAndRunsItsButton()
    {
        const string path = @"X:\example\file.txt";
        var digest = new string('A', 64);
        var service = WinOptimizer.UI.App.Services.GetRequiredService<Mock<IDiskToolsService>>();
        service.Setup(tools => tools.HashAsync(path, "", It.IsAny<CancellationToken>())).ReturnsAsync(new DiskHash(digest, null));
        var search = WinOptimizer.UI.App.Services.GetRequiredService<ProgramSearchService>();
        var match = Assert.Single(await search.FindAsync("Disks_Hash", default), result => result.Title == "Disks_Hash");
        var frame = new Frame();
        var window = new Window { Content = frame };
        try
        {
            var router = new NavigationRouter(null, null, new ExtensionTestDispatcher());
            Assert.True(NavigationRouter.NavigateToDestination(frame, match.Entry.PageType, match.Entry.Parameter));
            var page = Assert.IsType<DiskToolPage>(frame.Content);
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Disks", router.GetTagForCurrentPage(frame.CurrentSourcePageType));
            var hashPanel = Assert.IsType<StackPanel>(page.FindName("HashPanel"));
            Assert.Equal(Visibility.Visible, hashPanel.Visibility);
            Assert.IsType<TextBox>(page.FindName("TargetPath")).Text = path;
            var calculate = Assert.Single(hashPanel.Children.OfType<Button>());
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(calculate).GetPattern(PatternInterface.Invoke)).Invoke();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!page.ViewModel.Result.Contains(digest, StringComparison.Ordinal))
                await Task.Delay(10, timeout.Token);
            service.Verify(tools => tools.HashAsync(path, "", It.IsAny<CancellationToken>()), Times.Once);

            Assert.True(NavigationRouter.NavigateToDestination(frame, typeof(DiskToolPage), DiskTool.Permissions));
            Assert.Empty(Assert.IsType<DiskToolPage>(frame.Content).ViewModel.Result);
            frame.GoBack();
            Assert.Contains(digest, Assert.IsType<DiskToolPage>(frame.Content).ViewModel.Result, StringComparison.Ordinal);
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
            Assert.Equal(new[] { "Dashboard", "SoftwareApps", "Optimize", "Drivers", "Cleaner", "Disks", "Internet", "Management", "Security", "Repair", "Customize" },
                panel.Children.OfType<NavButton>().Select(button => Assert.IsType<string>(button.NavigationTag)));
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
