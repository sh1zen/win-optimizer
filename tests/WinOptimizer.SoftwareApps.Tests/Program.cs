using WinOptimizer.UI.Tests.Helpers;
using System.Reflection;
using System.Text.Json;
using Moq;
using Windows.Graphics;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WinOptimizer.UI.Tests.ViewModels;
using WinOptimizer.UI.Tests.Services;
using Xunit;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Resources;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.SoftwareAppsTests;

/// <summary>Runs app-view regressions on the real WinUI dispatcher with fake discovery and persistence boundaries.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Load the production module's Windows App SDK runtime before invoking WinUI.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(WinOptimizer.UI.App).Module.ModuleHandle);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WinUIApplication.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new SortingTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class SortingTestApplication(string resultPath) : WinOptimizer.UI.App(CreateTestHost())
{
    private Window? _window;

    private static IHost CreateTestHost() => new HostBuilder().ConfigureServices(services =>
    {
        services.AddTransient(_ => new AppSortingTests().Create(EnglishText(), InventoryApps()));
        services.AddSingleton(Mock.Of<IThemeService>());
    }).Build();

    private static ILocalizationService EnglishText()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("en.json")!;
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(resource)!;
        var text = new Mock<ILocalizationService>();
        text.Setup(source => source.GetString(It.IsAny<string>()))
            .Returns((string key) => strings.GetValueOrDefault(key, $"[{key}]"));
        text.Setup(source => source.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => string.Format(strings[key], args));
        return text.Object;
    }

    private static IReadOnlyList<ItemDefinition> InventoryApps() =>
        new (string Name, string Description, bool Reinstallable)[]
        {
            ("AI Manager", "Local AI requests for Office apps", false),
            ("Edge Game Assist", "Browse guides while you play", false),
            ("Office Actions Server", "AI-powered Office actions", false),
            ("Bing Search", "Web results in Windows Search", true),
            ("MSN Weather", "Forecasts, radar, and local weather", true),
            ("Camera", "Take photos and record videos", true),
            ("Xbox Game Bar", "Capture gameplay and monitor performance", true),
            ("Xbox Game Bar Plugin", "Components for Xbox Game Bar", false),
            ("Xbox Identity Provider", "Sign in to Xbox services", true),
            ("Paint", "Draw, edit, and create images", true),
            ("Photos", "View, edit, and organize photos", true),
            ("Snipping Tool", "Capture and annotate your screen", true)
        }.Select((app, index) => new ItemDefinition
        {
            Id = $"app-{index}",
            Name = app.Name,
            Description = app.Description,
            GroupName = $"{index:00}",
            AppxPackageName = [$"Test.App{index}"],
            CanBeReinstalled = app.Reinstallable,
            IsInstalled = true
        }).ToArray();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        InitializeComponent();
        InitializeShellResources();
        var shell = new Grid();
        _window = new Window { Content = shell };
        var deadline = _window.DispatcherQueue.CreateTimer();
        deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("App sorting tests timed out.");
        deadline.Start();
        shell.Loaded += async (_, _) => await RunAsync();
        _window.Activate();
    }

    private async Task RunAsync()
    {
        try
        {
            var count = await NativeTestCases.RunAsync(typeof(AppSortingTests), typeof(WindowsAppsViewModelTests), typeof(ExternalAppsViewModelTests), typeof(LeftoverAppsViewModelTests), typeof(UninstallResidueViewModelTests), typeof(UninstallResidueReviewTests), typeof(SoftwareAppsViewModelTests), typeof(SelectedAppsProviderTests), typeof(ConfigExportServiceTests), typeof(UniformWrapLayoutTests));
            await VerifyActiveTabHighlightAsync();
            await VerifyCardLayoutAsync();
            await VerifyInventoryLayoutAsync();
            await VerifyLeftoverBindingsAsync();
            Finish($"PASS: {count} Software & Apps cases on the WinUI dispatcher; active tab underline, responsive inventory and card layout, and English/Italian Leftovers bindings.", true);
        }
        catch (Exception ex) { Finish(ex.ToString()); }
    }

    /// <summary>Checks the active tab underline after selection and pointer transitions.</summary>
    private async Task VerifyActiveTabHighlightAsync()
    {
        var shell = (Grid)(_window?.Content ?? throw new InvalidOperationException("The test window is unavailable."));
        var page = new SoftwareAppsPage();
        shell.Children.Add(page);
        try
        {
            await Task.Yield();
            shell.UpdateLayout();
            var tabs = new[] { "WindowsAppsTab", "ExternalAppsTab", "LeftoverTab" }
                .Select(name => (Button: Assert.IsType<Button>(page.FindName(name)),
                    Indicator: Assert.IsType<Border>(page.FindName(name + "Indicator")))).ToArray();
            void AssertHighlight(Button selected)
            {
                Assert.True(selected.ApplyTemplate() || VisualTreeHelper.GetChildrenCount(selected) > 0);
                Assert.True(VisualStateManager.GoToState(selected, "PointerOver", false));
                Assert.True(VisualStateManager.GoToState(selected, "Normal", false));
                foreach (var tab in tabs)
                    Assert.Equal(tab.Button == selected ? Visibility.Visible : Visibility.Collapsed, tab.Indicator.Visibility);
                var indicator = tabs.Single(tab => tab.Button == selected).Indicator;
                Assert.Equal(VerticalAlignment.Bottom, indicator.VerticalAlignment);
                Assert.NotEqual((byte)0, Assert.IsType<SolidColorBrush>(indicator.Background).Color.A);
            }

            AssertHighlight(tabs[0].Button);
            tabs[1].Button.Command.Execute(null);
            AssertHighlight(tabs[1].Button);
            await page.ViewModel.SelectLeftoverTabCommand.ExecuteAsync(null);
            AssertHighlight(tabs[2].Button);
            tabs[0].Button.Command.Execute(null);
            AssertHighlight(tabs[0].Button);
        }
        finally { shell.Children.Remove(page); }
    }

    /// <summary>Measures the production inventory and checks filter, selection and card placement.</summary>
    private async Task VerifyInventoryLayoutAsync()
    {
        var window = _window ?? throw new InvalidOperationException("The test window is unavailable.");
        var shell = (Grid)window.Content;
        var colors = Resources.MergedDictionaries.OfType<AppColorResources>().Single();
        colors.Apply(AppColor.Purple);
        shell.Background = new SolidColorBrush(colors.GetCanvasColor(true));
        shell.RequestedTheme = ElementTheme.Dark;
        var page = new SoftwareAppsPage { Margin = new Thickness(14, 0, 14, 0) };
        shell.Children.Add(page);
        try
        {
            await page.ViewModel.InitializeWindowsAppsAsync();
            Assert.True(page.ViewModel.WindowsAppsViewModel.IsInitialized);
            Assert.Equal(12, page.ViewModel.WindowsAppsViewModel.WindowsAppsFiltered.Count());
            var searchGroup = Assert.IsType<Grid>(page.FindName("AppSearchGroup"));
            var search = Assert.IsType<TextBox>(page.FindName("AppSearchBox"));
            var filters = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("InventoryFilters"));
            var hiddenApps = Assert.IsType<ToggleButton>(page.FindName("ShowHiddenInstalledAppsToggle"));
            var hiddenAppsIcon = Assert.IsType<FluentIcons.WinUI.FluentIcon>(hiddenApps.Content);
            var sortLabel = Assert.IsType<TextBlock>(page.FindName("SortButtonLabel"));
            var heading = Assert.IsType<TextBlock>(page.FindName("InstalledAppsHeading"));
            var selectionActions = Assert.IsAssignableFrom<FrameworkElement>(page.FindName("InventorySelectionActions"));
            var list = Assert.IsType<ItemsControl>(page.FindName("WindowsAppsCards"));
            var selectAll = Assert.IsType<CheckBox>(page.FindName("SelectAllWindowsApps"));
            var uninstall = Assert.IsType<Button>(page.FindName("UninstallSelectedButton"));

            foreach (var (width, columns) in new[] { (1054, 3), (620, 2), (460, 1), (380, 1) })
            {
                var scale = shell.XamlRoot.RasterizationScale;
                window.AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling((width + 28) * scale), (int)Math.Ceiling(670 * scale)));
                await Task.Delay(350);
                shell.UpdateLayout();

                var panel = Assert.Single(Descendants<UniformWrapPanel>(list));
                Assert.Equal(12, panel.Children.Count);
                var first = Assert.IsAssignableFrom<FrameworkElement>(panel.Children[0]);
                var endOfRow = Assert.IsAssignableFrom<FrameworkElement>(panel.Children[columns - 1]);
                var nextRow = Assert.IsAssignableFrom<FrameworkElement>(panel.Children[columns]);
                var firstPosition = first.TransformToVisual(panel).TransformPoint(default);
                var endPosition = endOfRow.TransformToVisual(panel).TransformPoint(default);
                var nextPosition = nextRow.TransformToVisual(panel).TransformPoint(default);
                Assert.Equal(firstPosition.Y, endPosition.Y);
                Assert.InRange(endPosition.X + endOfRow.ActualWidth, panel.ActualWidth - 2, panel.ActualWidth + 2);
                Assert.Equal(firstPosition.X, nextPosition.X);
                Assert.True(nextPosition.Y > firstPosition.Y);

                var searchPosition = search.TransformToVisual(page).TransformPoint(default);
                var filterPosition = filters.TransformToVisual(page).TransformPoint(default);
                var headingY = heading.TransformToVisual(page).TransformPoint(default).Y;
                var selectionY = selectionActions.TransformToVisual(page).TransformPoint(default).Y;
                Assert.InRange(search.TransformToVisual(searchGroup).TransformPoint(default).X, -1, 1);
                Assert.Equal(width < 620 ? Visibility.Collapsed : Visibility.Visible, sortLabel.Visibility);
                Assert.True(headingY > filterPosition.Y);
                if (width >= 460)
                {
                    Assert.InRange(filterPosition.Y - searchPosition.Y, -2, 2);
                    Assert.True(filterPosition.X >= searchPosition.X + search.ActualWidth);
                    Assert.True(search.ActualWidth >= 160);
                    Assert.InRange(selectionY + selectionActions.ActualHeight / 2 - headingY - heading.ActualHeight / 2, -2, 2);
                }
                else
                {
                    Assert.True(filterPosition.Y >= searchPosition.Y + search.ActualHeight);
                    Assert.True(selectionY >= headingY + heading.ActualHeight);
                }
            }

            Assert.Equal("Search apps", search.PlaceholderText);
            Assert.Equal("Search", AutomationProperties.GetName(search));
            Assert.Equal("Show hidden or inactive apps", AutomationProperties.GetName(hiddenApps));
            Assert.Equal(page.ViewModel.WindowsAppsViewModel.ShowHiddenInstalledAppsTooltip, ToolTipService.GetToolTip(hiddenApps));
            Assert.False(hiddenApps.IsChecked);
            Assert.Equal(FluentIcons.Common.Icon.EyeOff, hiddenAppsIcon.Icon);

            var toggle = Assert.IsAssignableFrom<IToggleProvider>(new ToggleButtonAutomationPeer(hiddenApps).GetPattern(PatternInterface.Toggle));
            toggle.Toggle();
            Assert.True(page.ViewModel.WindowsAppsViewModel.ShowHiddenInstalledApps);
            Assert.Equal(FluentIcons.Common.Icon.Eye, hiddenAppsIcon.Icon);
            toggle.Toggle();
            Assert.False(page.ViewModel.WindowsAppsViewModel.ShowHiddenInstalledApps);
            Assert.Equal(FluentIcons.Common.Icon.EyeOff, hiddenAppsIcon.Icon);

            search.Text = "Camera";
            Assert.Equal("Camera", page.ViewModel.SearchText);
            Assert.False(uninstall.IsEnabled);
            selectAll.Command.Execute(null);
            Assert.True(selectAll.IsChecked == true);
            Assert.True(uninstall.IsEnabled);
            Assert.Equal("Camera", Assert.Single(page.ViewModel.WindowsAppsViewModel.Items, app => app.IsSelected).Name);
            selectAll.Command.Execute(null);
            Assert.True(selectAll.IsChecked == false);
            Assert.False(uninstall.IsEnabled);
        }
        finally
        {
            shell.Children.Remove(page);
            colors.Apply(AppColor.Blue);
        }
    }

    /// <summary>Checks row height and spacing with the production panel in a real WinUI visual tree.</summary>
    private async Task VerifyCardLayoutAsync()
    {
        var window = _window ?? throw new InvalidOperationException("The test window is unavailable.");
        var shell = (Grid)window.Content;
        var panel = new WinOptimizer.UI.Features.Common.Controls.UniformWrapPanel
        {
            Width = 210, ItemWidth = 100, ItemHeight = 20, ColumnSpacing = 10, RowSpacing = 5,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        };
        var first = new Grid { Height = 15, VerticalAlignment = VerticalAlignment.Top };
        var second = new Grid { Height = 70, VerticalAlignment = VerticalAlignment.Top };
        var third = new Grid { Height = 15, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(third);
        shell.Children.Add(panel);
        try
        {
            await Task.Yield();
            shell.UpdateLayout();

            Assert.Equal(new Windows.Foundation.Size(210, 145), panel.DesiredSize);
            Assert.Equal(70, second.ActualHeight);
            Assert.Equal(new Windows.Foundation.Point(0, 0), first.TransformToVisual(panel).TransformPoint(default));
            Assert.Equal(new Windows.Foundation.Point(110, 0), second.TransformToVisual(panel).TransformPoint(default));
            Assert.Equal(new Windows.Foundation.Point(0, 75), third.TransformToVisual(panel).TransformPoint(default));
            Assert.All(new[] { first, second, third }, card => Assert.Equal(100, card.ActualWidth));

            third.Height = 90;
            await Task.Yield();
            shell.UpdateLayout();
            Assert.Equal(new Windows.Foundation.Size(210, 185), panel.DesiredSize);
            Assert.Equal(90, third.ActualHeight);
            Assert.Equal(new Windows.Foundation.Point(0, 95), third.TransformToVisual(panel).TransformPoint(default));
        }
        finally { shell.Children.Remove(panel); }
    }

    private void Finish(string message, bool passed = false)
    {
        File.WriteAllText(resultPath, message);
        Console.WriteLine(message);
        Environment.Exit(passed ? 0 : 1);
    }

    /// <summary>Checks selection and eligibility bindings in the production Leftovers control.</summary>
    private async Task VerifyLeftoverBindingsAsync()
    {
        foreach (var language in new[] { "en", "it" })
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(resource)!;
            var text = new Mock<ILocalizationService>();
            text.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            text.Setup(source => source.GetString(It.IsAny<string>(), It.IsAny<object[]>())).Returns((string key, object[] args) => string.Format(strings[key], args));
            var service = new Mock<ILeftoverService>();
            service.Setup(source => source.ScanAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new LeftoverScan(
            [
                new(Guid.NewGuid(), "Obsolete application", LeftoverKind.Registration, @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\ObsoleteApplication", 0, true, "The application directory, executable and uninstaller are missing."),
                new(Guid.NewGuid(), "unused-installer.msi", LeftoverKind.Msi, @"C:\Windows\Installer\unused-installer.msi", 21843721, true, "No installed product, patch or uninstall registration references this cache file; it has not been used for at least 30 days."),
                new(Guid.NewGuid(), "Obsolete application folder", LeftoverKind.Folder, @"C:\Program Files\ObsoleteApplication", 8304400, true, "The registered executable and uninstaller are absent; this aged folder has no executable payload or active installation reference."),
            ], [@"C:\ProgramData\Package Cache\Restricted: access denied; this folder was not scanned."]));
            using var cancellation = new CancellationTokenSource();
            var tasks = new Mock<ITaskProgressService>(); tasks.Setup(source => source.StartTask(It.IsAny<string>(), It.IsAny<bool>())).Returns(cancellation);
            var dispatcher = new Mock<IDispatcherService>(); dispatcher.Setup(source => source.RunOnUIThreadAsync(It.IsAny<Func<Task>>())).Returns((Func<Task> action) => action());
            using var vm = new LeftoverAppsViewModel(service.Object, text.Object, Mock.Of<IDialogService>(), Mock.Of<IApplicationModeService>(), tasks.Object, dispatcher.Object, Mock.Of<ILogService>());
            var content = new Grid { Padding = new Thickness(20), RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)), RowSpacing = 16 };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition());
            content.Children.Add(new TextBlock { Text = strings["SoftwareApps_Tab_Leftover"], FontSize = 24 });
            var control = new LeftoverAppsControl { DataContext = vm }; Grid.SetRow(control, 1); content.Children.Add(control);
            _window!.Content = content;
            await vm.ScanAsync();
            vm.Items[1].IsSelected = true;
            foreach (var width in new[] { 620, 1100 })
            {
                _window.AppWindow.ResizeClient(new SizeInt32(width, 700)); await Task.Delay(350); content.UpdateLayout();
                Assert.Equal(vm.SelectionText, Descendants<TextBlock>(control).Single(block => block.Name == "SelectionSummary").Text);
                foreach (var checkbox in Descendants<CheckBox>(control).Where(checkbox => checkbox.ActualWidth > 0))
                {
                    if (checkbox.DataContext is LeftoverItemViewModel row)
                    {
                        Assert.Equal((bool?)row.IsSelected, checkbox.IsChecked);
                        Assert.Equal(row.CanRemove, checkbox.IsEnabled);
                    }
                    else if (checkbox.DataContext == vm) Assert.Equal((bool?)vm.IsAllSelected, checkbox.IsChecked);
                }
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
