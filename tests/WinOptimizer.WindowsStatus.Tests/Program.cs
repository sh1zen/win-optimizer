using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Moq;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.WindowsStatusTests;

/// <summary>Runs production servicing presentation with isolated operating-system boundaries.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass a preview directory.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WinUIApplication.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new StatusTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class StatusTestApplication(string outputDirectory) : WinUIApplication
{
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(outputDirectory);
        InitializeComponent(); UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid(); _window = new Window { Content = shell };
        var deadline = _window.DispatcherQueue.CreateTimer(); deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("Windows status presentation timed out."); deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try { await ContractsAsync(); await CheckPageContainersAsync(shell); await CheckScrollInsetsAsync(shell); await RenderAsync(shell); Finish(); }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    /// <summary>Verifies that common containers fill the available width even with short content.</summary>
    private async Task CheckPageContainersAsync(Grid shell)
    {
        foreach (var stack in new[] { false, true })
        {
            Panel container = stack
                ? new StackPanel { Style = (Style)Resources["PageStackStyle"] }
                : new Grid { Style = (Style)Resources["PageGridStyle"] };
            container.Children.Add(new TextBlock { Text = "Short heading" });
            var viewport = new Grid { Margin = (Thickness)Resources["PageHorizontalMargin"] };
            viewport.Children.Add(stack
                ? new ScrollViewer { Content = container, Style = (Style)Resources["PageScrollViewerStyle"] }
                : container);
            shell.Children.Clear(); shell.Children.Add(viewport);
            foreach (var width in new[] { 480, 1100, 1920 })
            {
                _window!.AppWindow.ResizeClient(new SizeInt32(width, 1000)); await Task.Delay(80); shell.UpdateLayout();
                AssertFillsViewport(container, viewport, stack ? 16 : 0);
            }
        }
        shell.Children.Clear();
        Console.WriteLine("PASS: shared grid/stack containers fill short content at three widths.");
    }

    /// <summary>Checks real template bounds for classic, modern and list scrolling hosts.</summary>
    private async Task CheckScrollInsetsAsync(Grid shell)
    {
        var padding = (Thickness)Resources["ScrollContentPadding"];
        foreach (var host in new Control[]
        {
            new ScrollViewer { Padding = padding, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = new Border { Height = 1600 } },
            new ScrollView { Padding = padding, VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Visible,
                Content = new Border { Height = 1600 } },
            new ListView { Padding = padding, ItemsSource = Enumerable.Range(0, 80).Select(i => $"Row {i}").ToArray() }
        })
        {
            host.Width = 420; host.Height = 240;
            shell.Children.Clear(); shell.Children.Add(host);
            await Task.Delay(100); shell.UpdateLayout();
            FrameworkElement content = host switch
            {
                ScrollViewer viewer => (FrameworkElement)viewer.Content,
                ScrollView view => (FrameworkElement)view.Content,
                ListView list => (FrameworkElement)list.ContainerFromIndex(0),
                _ => throw new InvalidOperationException("Unexpected scroll host.")
            };
            var bar = Descendants(host).OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
            var right = content.TransformToVisual(host).TransformPoint(default).X + content.ActualWidth;
            var barLeft = bar.TransformToVisual(host).TransformPoint(default).X;
            Assert.True(right <= barLeft - 3, $"{host.GetType().Name}: content right={right}, scrollbar left={barLeft}.");
        }
        var wideContent = new Border { Width = 1600, Height = 1600 };
        var bidirectional = new ScrollViewer
        {
            Width = 420, Height = 240, Content = wideContent,
            Padding = (Thickness)Resources["BidirectionalScrollContentPadding"],
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible
        };
        shell.Children.Clear(); shell.Children.Add(bidirectional);
        await Task.Delay(100); shell.UpdateLayout();
        var presenter = Descendants(bidirectional).OfType<ScrollContentPresenter>().Single();
        var horizontalBar = Descendants(bidirectional).OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Horizontal);
        var bottom = presenter.TransformToVisual(bidirectional).TransformPoint(default).Y + presenter.ActualHeight;
        var barTop = horizontalBar.TransformToVisual(bidirectional).TransformPoint(default).Y;
        Assert.True(bottom <= barTop - 3, $"Content bottom={bottom}, horizontal scrollbar top={barTop}.");
        shell.Children.Clear();
        Console.WriteLine("PASS: classic, modern and list content leave gaps before vertical and horizontal scrollbars.");
    }

    /// <summary>Enumerates template elements for layout assertions.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>Runs the same cases as the main UI test project inside the native WinUI runtime.</summary>
    private static async Task ContractsAsync()
    {
        var passed = 0;
        foreach (var method in typeof(WindowsStatusViewModelTests).GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            var data = method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray();
            foreach (var row in data.Length == 0 ? new[] { Array.Empty<object>() } : data)
            {
                using var tests = new WindowsStatusViewModelTests();
                try { if (method.Invoke(tests, row) is Task task) await task; passed++; }
                catch (TargetInvocationException ex) { throw new InvalidOperationException(method.Name, ex.InnerException); }
            }
        }
        Console.WriteLine($"PASS: {passed} production Windows status view-model cases.");
    }

    /// <summary>Checks shared page and footer bounds with real controls and isolated servicing receipts.</summary>
    private async Task RenderAsync(Grid shell)
    {
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var language in new[] { "en", "it" })
        {
            using var json = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            var service = new Mock<IWindowsServicingService>(MockBehavior.Strict);
            service.Setup(source => source.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((WindowsServicingRequest request, IProgress<WindowsServicingProgress>? _, CancellationToken _) =>
                    request.Action == WindowsServicingAction.QuickCheck
                        ? new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.NeedsAttention, WindowsImageHealth.Repairable,
                            [new(WindowsServicingStep.CheckHealth, true, 0, WindowsImageHealth.Repairable, false, "ImageHealthState: Repairable")])
                        : new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.ReviewFileResults, WindowsImageHealth.Healthy,
                            [new(WindowsServicingStep.RestoreHealth, true, 0, WindowsImageHealth.Healthy, false, "ImageHealthState: Healthy"),
                             new(WindowsServicingStep.RepairFiles, true, 0, WindowsImageHealth.Unknown, false, "SFC repair output for review.")]));
            using var cancellation = new CancellationTokenSource(); var tasks = new Mock<ITaskProgressService>();
            tasks.Setup(source => source.StartTask(It.IsAny<string>(), true, true, true)).Returns(cancellation);
            tasks.SetupGet(source => source.CurrentTaskCancellationSource).Returns(cancellation);
            var dialogs = new Mock<IDialogService>();
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
            using var vm = new WindowsStatusViewModel(service.Object, dialogs.Object, Mock.Of<IFilePickerService>(),
                localization.Object, Mock.Of<IApplicationModeService>(), new ExtensionTestDispatcher(), tasks.Object);
            var page = new WindowsStatusPage(vm); var loaded = new TaskCompletionSource(); page.Loaded += (_, _) => loaded.TrySetResult();
            var body = new Grid { Margin = (Thickness)Resources["PageHorizontalMargin"] };
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(body, 1);
            body.Children.Add(new Frame { Content = page });
            var footerLayout = new Grid { Style = (Style)Resources["PageGridStyle"], Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 8) };
            Grid.SetRow(footerLayout, 1);
            var footer = new TaskProgressControl
            {
                AppName = vm.Title + " · " + vm.AnalyzeLabel,
                LastTerminalLine = "Cancellation requested. Waiting for the current operation to stop.",
                IsProgressVisible = Visibility.Visible, CanCancel = Visibility.Visible, IsTaskRunning = true,
                CancelText = strings["Button_Cancel"], ProgressValue = 37, IsIndeterminate = false,
                QueueNextItemName = new string('W', 180), QueueStatusText = "2 / 10", IsQueueInfoVisible = Visibility.Visible
            };
            var detailClicks = 0; var cancelClicks = 0;
            var detailsCommand = new Mock<System.Windows.Input.ICommand>();
            detailsCommand.Setup(command => command.CanExecute(It.IsAny<object>())).Returns(true);
            detailsCommand.Setup(command => command.Execute(It.IsAny<object>())).Callback(() => detailClicks++);
            var cancelCommand = new Mock<System.Windows.Input.ICommand>();
            cancelCommand.Setup(command => command.CanExecute(It.IsAny<object>())).Returns(true);
            cancelCommand.Setup(command => command.Execute(It.IsAny<object>())).Callback(() => cancelClicks++);
            footer.ShowDetailsCommand = detailsCommand.Object; footer.CancelCommand = cancelCommand.Object;
            footerLayout.Children.Add(footer); body.Children.Add(footerLayout);
            shell.Children.Clear(); shell.Children.Add(body); await loaded.Task; service.VerifyNoOtherCalls();
            var primary = (Button)page.FindName("PrimaryActionButton");
            var options = (Expander)page.FindName("RepairOptionsExpander");
            var results = (StackPanel)page.FindName("ResultsPanel");
            var notice = (InfoBar)page.FindName("ReportNotice");
            Assert.False(options.IsExpanded); Assert.Equal(Visibility.Collapsed, results.Visibility);
            Assert.Equal("Diagnosis", primary.CommandParameter); Assert.False(vm.ResetBase);
            await vm.RunCommand.ExecuteAsync("QuickCheck");
            Assert.Single(vm.Steps); Assert.Equal(InfoBarSeverity.Warning, vm.ReportSeverity);
            Assert.Equal(strings["WindowsStatus_StateRepairable"], notice.Message);
            Assert.Equal("Repair", primary.CommandParameter); Assert.Equal(Visibility.Visible, results.Visibility);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var width in new[] { 480, 1100, 1500, 1920 })
            foreach (var sidebarWidth in new[] { 48, 80 })
            {
                shell.RequestedTheme = theme; shell.Background = new SolidColorBrush(theme == ElementTheme.Light ? Colors.White : Colors.Black);
                shell.ColumnDefinitions[0].Width = new GridLength(sidebarWidth);
                _window!.AppWindow.ResizeClient(new SizeInt32(width, 1000)); await Task.Delay(100); shell.UpdateLayout();
                AssertFillsViewport((Border)page.FindName("ScanHero"), body, 16);
                AssertFillsViewport(footerLayout, body);
                foreach (var name in new[] { "TaskTitle", "TerminalButton", "DetailsButton", "CancelButton", "QueueNextText", "QueueCountText" })
                {
                    var element = (FrameworkElement)footer.FindName(name);
                    var left = element.TransformToVisual(footer).TransformPoint(default).X;
                    Assert.True(element.ActualWidth > 0 && left >= 0 && left + element.ActualWidth <= footer.ActualWidth + 1,
                        $"Footer {name} exceeds its container: left={left}, width={element.ActualWidth}, footer={footer.ActualWidth}.");
                }
                var origin = primary.TransformToVisual(shell).TransformPoint(default);
                Assert.True(primary.ActualWidth > 0 && origin.X >= 0 && origin.X + primary.ActualWidth <= shell.ActualWidth + 1);
                var resultsOrigin = results.TransformToVisual(shell).TransformPoint(default);
                var optionsOrigin = options.TransformToVisual(shell).TransformPoint(default);
                Assert.True(resultsOrigin.Y < optionsOrigin.Y);
                Assert.True(origin.Y + primary.ActualHeight < shell.ActualHeight);
                await CaptureAsync(shell, $"windows-status-{language}-{theme}-{width}-sidebar-{sidebarWidth}.png");
            }
            foreach (var name in new[] { "TerminalButton", "DetailsButton", "CancelButton" })
                ((IInvokeProvider)new ButtonAutomationPeer((Button)footer.FindName(name)).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(80);
            Assert.Equal(2, detailClicks); Assert.Equal(1, cancelClicks);
            // Invoke the native main button to verify its bound action uses the confirmation workflow.
            var invocation = (IInvokeProvider)new ButtonAutomationPeer(primary).GetPattern(PatternInterface.Invoke);
            invocation.Invoke();
            for (var attempt = 0; attempt < 100 && (vm.IsBusy || vm.Report?.Request.Action != WindowsServicingAction.Repair); attempt++) await Task.Delay(20);
            Assert.False(vm.IsBusy); Assert.Equal(WindowsImageHealth.Healthy, vm.Report!.ImageHealth);
            Assert.Equal("Diagnosis", primary.CommandParameter);
            Assert.Equal(strings["WindowsStatus_StateReviewFileResults"], notice.Message);
            dialogs.Verify(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Once);
            await CaptureAsync(shell, $"windows-status-{language}-repaired.png");
            options.IsExpanded = true; shell.UpdateLayout();
            var repair = (Button)page.FindName("RepairWindowsButton");
            Assert.True(repair.ActualWidth > 0); Assert.True(repair.IsEnabled);
            options.IsExpanded = false;
            shell.Children.Clear();
        }
        Console.WriteLine("PASS: English/Italian, four widths, light/dark, full-width pages/footers, retained receipts and explicit actions; no native maintenance.");
    }

    /// <summary>Checks that arranged content fills the viewport inside its reserved scrollbar inset.</summary>
    private static void AssertFillsViewport(FrameworkElement element, FrameworkElement viewport, double trailingInset = 0)
    {
        var left = element.TransformToVisual(viewport).TransformPoint(default).X;
        var expectedWidth = viewport.ActualWidth - trailingInset;
        Assert.True(Math.Abs(element.ActualWidth - expectedWidth) <= 1,
            $"{element.GetType().Name} width: {element.ActualWidth}, expected {expectedWidth}.");
        Assert.True(Math.Abs(left) <= 1,
            $"{element.GetType().Name} does not start at the viewport edge: left={left}.");
    }

    private async Task CaptureAsync(UIElement root, string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root); var pixels = await bitmap.GetPixelsAsync();
        await using var output = File.Create(Path.Combine(outputDirectory, name));
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    private void Finish(string? error = null)
    {
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All Windows status checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
