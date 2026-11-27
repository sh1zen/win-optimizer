using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Moq;
using Windows.Graphics;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Helpers;
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
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(WinOptimizer.UI.App).Module.ModuleHandle);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WinUIApplication.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new StatusTestApplication(Path.GetFullPath(args[0]), new RenderFixture());
        });
    }
}

/// <summary>Provides the current fake-servicing view model to both pages in the real Frame journal.</summary>
public sealed class RenderFixture
{
    public WindowsStatusViewModel? ViewModel { get; set; }

    public IHost CreateHost() => new HostBuilder().ConfigureServices(services => services.AddTransient(
        _ => ViewModel ?? throw new InvalidOperationException("Attach the servicing fixture before navigation."))).Build();
}

public sealed partial class StatusTestApplication(string outputDirectory, RenderFixture fixture) : WinOptimizer.UI.App(fixture.CreateHost())
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

    /// <summary>Checks servicing cards, retained results and guidance with real controls.</summary>
    private async Task RenderAsync(Grid shell)
    {
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var language in new[] { "en", "it" })
        {
            using var json = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            void CheckDiagnoseButton(Button button, WindowsServicingAction diagnosis)
            {
                Assert.Equal(diagnosis.ToString(), button.CommandParameter);
                var label = Assert.Single(Assert.IsType<StackPanel>(button.Content).Children.OfType<TextBlock>());
                Assert.Equal(strings["WindowsStatus_DiagnosisShort"], label.Text);
                Assert.Equal(label.Text, AutomationProperties.GetName(button));
            }
            var localization = new Mock<ILocalizationService>();
            localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            var service = new Mock<IWindowsServicingService>(MockBehavior.Strict);
            var releaseDiagnosis = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stoppedRepair = new TaskCompletionSource<WindowsServicingReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            var interruptRepair = false;
            var quickCheckNeedsRestart = false;
            var diagnosisFoundCorruption = false;
            service.Setup(source => source.RunAsync(It.IsAny<WindowsServicingRequest>(), It.IsAny<IProgress<WindowsServicingProgress>>(), It.IsAny<CancellationToken>()))
                .Returns(async (WindowsServicingRequest request, IProgress<WindowsServicingProgress>? progress, CancellationToken _) =>
                {
                    if (request.Action == WindowsServicingAction.Repair && interruptRepair) return await stoppedRepair.Task;
                    if (request.Action == WindowsServicingAction.Diagnosis)
                    {
                        progress?.Report(new(WindowsServicingStep.AnalyzeStore, 1, 3));
                        await releaseDiagnosis.Task;
                    }
                    return request.Action switch
                    {
                        WindowsServicingAction.QuickCheck => new WindowsServicingReport(request, DateTimeOffset.Now,
                            quickCheckNeedsRestart ? WindowsServicingState.RestartRequired : WindowsServicingState.NeedsAttention, WindowsImageHealth.Repairable,
                            [new(WindowsServicingStep.CheckHealth, true, 0, WindowsImageHealth.Repairable, false, "ImageHealthState: Repairable")]),
                        WindowsServicingAction.Diagnosis when diagnosisFoundCorruption => new WindowsServicingReport(request, DateTimeOffset.Now,
                            WindowsServicingState.NeedsAttention, WindowsImageHealth.Repairable,
                            [new(WindowsServicingStep.ScanHealth, true, 0, WindowsImageHealth.Repairable, false, "ImageHealthState: Repairable")
                             { StoreFindings = new(2, 1, "microsoft-windows-example") }]),
                        WindowsServicingAction.Diagnosis => new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.ReviewFileResults, WindowsImageHealth.Healthy,
                            [new(WindowsServicingStep.ScanHealth, true, 0, WindowsImageHealth.Healthy, false, "ImageHealthState: Healthy"),
                             new(WindowsServicingStep.AnalyzeStore, true, 0, WindowsImageHealth.Unknown, false, "Native analysis")
                             { StoreAnalysis = new(5m * (1L << 30), true) },
                             new(WindowsServicingStep.VerifyFiles, true, 0, WindowsImageHealth.Unknown, false, "SFC verification output for review.")]),
                        WindowsServicingAction.Repair => new WindowsServicingReport(request, DateTimeOffset.Now, WindowsServicingState.ReviewFileResults, WindowsImageHealth.Healthy,
                            [new(WindowsServicingStep.RestoreHealth, true, 0, WindowsImageHealth.Healthy, false, "ImageHealthState: Healthy"),
                             new(WindowsServicingStep.RepairFiles, true, 0, WindowsImageHealth.Unknown, false, "SFC repair output for review.")]),
                        _ => throw new InvalidOperationException($"Unexpected servicing action {request.Action}.")
                    };
                });
            using var cancellation = new CancellationTokenSource();
            using var interruptedCancellation = new CancellationTokenSource();
            var activeTask = cancellation;
            var tasks = new Mock<ITaskProgressService>();
            tasks.Setup(source => source.StartTask(It.IsAny<string>(), true, false, true))
                .Returns(() => activeTask = interruptRepair ? interruptedCancellation : cancellation);
            tasks.SetupGet(source => source.CurrentTaskCancellationSource).Returns(() => activeTask);
            var dialogs = new Mock<IDialogService>();
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
            var notifications = new ScanCancellationNotifications();
            using var vm = new WindowsStatusViewModel(service.Object, dialogs.Object, Mock.Of<IFilePickerService>(),
                localization.Object, Mock.Of<IApplicationModeService>(), new ExtensionTestDispatcher(), tasks.Object, notifications);
            fixture.ViewModel = vm;
            var frame = new Frame
            {
                ContentTransitions = new TransitionCollection
                {
                    new NavigationThemeTransition { DefaultNavigationTransitionInfo = new SuppressNavigationTransitionInfo() }
                }
            };
            Assert.True(frame.Navigate(typeof(WindowsStatusPage)));
            var page = Assert.IsType<WindowsStatusPage>(frame.Content);
            var loaded = new TaskCompletionSource();
            page.Loaded += (_, _) => loaded.TrySetResult();
            var body = new Grid { Margin = (Thickness)Resources["PageHorizontalMargin"] };
            Grid.SetColumn(body, 1);
            body.Children.Add(frame);
            var cancellationToast = new InfoBar { VerticalAlignment = VerticalAlignment.Bottom };
            body.Children.Add(cancellationToast);
            var canNavigateForward = true;
            using var scanPresentation = new ScanCancellationPresentation(notifications, frame, cancellationToast,
                () => canNavigateForward = frame.CanGoForward);
            shell.Children.Clear(); shell.Children.Add(body); await loaded.Task; service.VerifyNoOtherCalls();
            var heading = Assert.Single(Descendants(page).OfType<PageHeader>());
            Assert.Equal(language == "en" ? "Repair" : "Riparazione", heading.Title);
            var primary = (Button)page.FindName("PrimaryActionButton");
            var quickCheck = (ToggleSwitch)page.FindName("QuickCheckSwitch");
            var overviewProgress = (WindowsStatusProgressView)page.FindName("OperationProgress");
            var hero = (Border)page.FindName("ScanHero");
            Assert.Equal(Visibility.Collapsed, ((NavigationCard)page.FindName("ReviewReport")).Visibility);
            Assert.Equal(Visibility.Collapsed, overviewProgress.Visibility);
            Assert.False(quickCheck.IsOn);
            CheckDiagnoseButton(primary, WindowsServicingAction.Diagnosis);
            Assert.False(vm.ResetBase);
            var toggle = ToggleProvider(quickCheck);
            toggle.Toggle();
            CheckDiagnoseButton(primary, WindowsServicingAction.QuickCheck);
            service.VerifyNoOtherCalls();
            toggle.Toggle();
            var runningDiagnosis = InvokeServicingAsync(primary, vm, WindowsServicingAction.Diagnosis);
            try
            {
                for (var attempt = 0; attempt < 100 && vm.OperationProgress == null; attempt++) await Task.Delay(10);
                Assert.Equal(strings["WindowsStatus_StepAnalyzeStore"], vm.ProgressTitle);
                Assert.Equal(Visibility.Visible, overviewProgress.Visibility);
                Assert.Equal(Visibility.Collapsed, hero.Visibility);
                Assert.True(((Button)overviewProgress.FindName("CancelOperationButton")).IsEnabled);
                var progressBar = (ProgressBar)overviewProgress.FindName("OperationProgressBar");
                Assert.False(progressBar.IsIndeterminate);
                Assert.InRange(progressBar.Value, 33, 34);
            }
            finally { releaseDiagnosis.TrySetResult(); }
            await runningDiagnosis;
            var reportPage = Assert.IsType<WindowsStatusResultsPage>(frame.Content);
            await CheckDiagnosisReportAsync(reportPage, shell, strings);
            frame.GoBack();
            page = Assert.IsType<WindowsStatusPage>(frame.Content);
            await Task.Delay(50);
            quickCheck = (ToggleSwitch)page.FindName("QuickCheckSwitch");
            ToggleProvider(quickCheck).Toggle();
            await InvokeServicingAsync((Button)page.FindName("PrimaryActionButton"), vm, WindowsServicingAction.QuickCheck);
            reportPage = Assert.IsType<WindowsStatusResultsPage>(frame.Content);
            await Task.Delay(50);
            var resultRepair = Assert.IsType<Button>(reportPage.FindName("ReportRepairButton"));
            Assert.Equal(InfoBarSeverity.Warning, vm.ReportSeverity);
            Assert.Equal(strings["WindowsStatus_HealthRepairable"], Assert.IsType<TextBlock>(reportPage.FindName("ReportHeading")).Text);
            Assert.Equal(strings["WindowsStatus_StateRepairable"], Assert.IsType<TextBlock>(reportPage.FindName("ReportMessage")).Text);
            Assert.Equal(strings["WindowsStatus_RepairShort"], AutomationProperties.GetName(resultRepair));
            Assert.Equal(Visibility.Visible, resultRepair.Visibility);
            Assert.True(resultRepair.IsEnabled);
            Assert.Equal(strings["WindowsStatus_QuickCheckDetails"], Assert.IsType<TextBlock>(reportPage.FindName("ProblemCount")).Text);
            Assert.Equal(strings["WindowsStatus_ProblemCategory"], Assert.IsType<TextBlock>(reportPage.FindName("ProblemCategory")).Text);
            var resultCard = Assert.IsType<Border>(reportPage.FindName("ReportCard"));
            foreach (var width in new[] { 480, 1100 })
            {
                _window!.AppWindow.ResizeClient(new SizeInt32(width, 1000));
                await Task.Delay(100);
                shell.UpdateLayout();
                AssertControlInside(resultRepair, resultCard);
                Assert.Equal(width == 480 ? 1 : 0, Grid.GetRow(resultRepair));
                var rightEdge = resultRepair.TransformToVisual(resultCard).TransformPoint(default).X + resultRepair.ActualWidth;
                Assert.InRange(resultCard.ActualWidth - rightEdge, 23, 26);
            }
            frame.GoBack();
            page = Assert.IsType<WindowsStatusPage>(frame.Content);
            await Task.Delay(50);
            primary = (Button)page.FindName("PrimaryActionButton");
            quickCheck = (ToggleSwitch)page.FindName("QuickCheckSwitch");
            CheckDiagnoseButton(primary, WindowsServicingAction.QuickCheck);
            ToggleProvider(quickCheck).Toggle();
            CheckDiagnoseButton(primary, WindowsServicingAction.Diagnosis);
            ToggleProvider(quickCheck).Toggle();

            var repairableReport = vm.Report;
            var confirmationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).Returns(() =>
            {
                confirmationRequested.TrySetResult();
                return Task.FromResult(new ConfirmationResponse());
            });
            var repairOption = Assert.IsType<Button>(page.FindName("RepairWindowsButton"));
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(repairOption).GetPattern(PatternInterface.Invoke)).Invoke();
            await confirmationRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.IsAssignableFrom<Task>(vm.RunCommand.ExecutionTask).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(page, frame.Content);
            Assert.Same(repairableReport, vm.Report);
            CheckDiagnoseButton(primary, WindowsServicingAction.QuickCheck);
            Assert.Equal(Visibility.Visible, ((NavigationCard)page.FindName("ReviewReport")).Visibility);
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });

            await InvokeServicingAsync(primary, vm, WindowsServicingAction.QuickCheck);
            reportPage = Assert.IsType<WindowsStatusResultsPage>(frame.Content);
            await Task.Delay(50);
            shell.UpdateLayout();
            var previousReport = vm.Report;
            interruptRepair = true;
            var reportRepair = Assert.IsType<Button>(reportPage.FindName("ReportRepairButton"));
            AssertControlInside(reportRepair, reportPage);
            Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(reportRepair).GetPattern(PatternInterface.Invoke)).Invoke();
            for (var attempt = 0; attempt < 100 && !vm.IsOperationRunning; attempt++) await Task.Delay(10);
            Assert.True(vm.IsOperationRunning);
            var progressView = Assert.Single(Descendants(reportPage).OfType<WindowsStatusProgressView>());
            var cancelOperation = Assert.IsType<Button>(progressView.FindName("CancelOperationButton"));
            await Task.Delay(50);
            shell.UpdateLayout();
            Assert.Equal(Visibility.Visible, progressView.Visibility);
            AssertControlInside(cancelOperation, progressView);
            Assert.Same(vm.CancelOperationCommand, cancelOperation.Command);
            Assert.True(cancelOperation.IsEnabled && cancelOperation.Command.CanExecute(null));
            cancelOperation.Command.Execute(null);
            Assert.True(interruptedCancellation.IsCancellationRequested);
            Assert.False(cancellationToast.IsOpen);
            stoppedRepair.SetResult(new(new(WindowsServicingAction.Repair), DateTimeOffset.Now,
                WindowsServicingState.Cancelled, WindowsImageHealth.Unknown, []));
            for (var attempt = 0; attempt < 100 && !cancellationToast.IsOpen; attempt++) await Task.Delay(10);
            Assert.True(cancellationToast.IsOpen);
            Assert.Equal(strings["WindowsStatus_StateCancelled"], cancellationToast.Message);
            Assert.Same(previousReport, vm.Report);
            Assert.False(frame.CanGoForward);
            Assert.False(canNavigateForward);
            cancellationToast.IsOpen = false;
            interruptRepair = false;
            page = Assert.IsType<WindowsStatusPage>(frame.Content);
            await Task.Delay(50);
            primary = (Button)page.FindName("PrimaryActionButton");
            quickCheck = (ToggleSwitch)page.FindName("QuickCheckSwitch");
            hero = (Border)page.FindName("ScanHero");
            var cards = (Grid)page.FindName("ServicingCards");
            var repairCard = (Border)page.FindName("RepairCard");
            var cleanupCard = (Border)page.FindName("CleanupCard");
            var repair = (Button)page.FindName("RepairWindowsButton");
            var cleanup = (Button)page.FindName("CleanupWindowsButton");
            var guidance = (Button)page.FindName("GuidanceButton");
            var review = (NavigationCard)page.FindName("ReviewReport");
            Assert.Equal(Visibility.Visible, review.Visibility);
            Assert.Equal("Repair", repair.CommandParameter);
            Assert.Equal("Cleanup", cleanup.CommandParameter);
            Assert.True(repair.IsEnabled && cleanup.IsEnabled);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var width in new[] { 480, 1100, 1500, 1920 })
            foreach (var sidebarWidth in new[] { 48, 80 })
            {
                shell.RequestedTheme = theme; shell.Background = new SolidColorBrush(theme == ElementTheme.Light ? Colors.White : Colors.Black);
                shell.ColumnDefinitions[0].Width = new GridLength(sidebarWidth);
                _window!.AppWindow.ResizeClient(new SizeInt32(width, 1000)); await Task.Delay(100); shell.UpdateLayout();
                AssertFillsViewport(hero, body, 16);
                AssertFillsViewport(cards, body, 16);
                var origin = primary.TransformToVisual(shell).TransformPoint(default);
                Assert.True(primary.ActualWidth > 0 && origin.X >= 0 && origin.X + primary.ActualWidth <= shell.ActualWidth + 1);
                Assert.True(origin.Y + primary.ActualHeight < shell.ActualHeight);
                AssertCardLayout(cards, repairCard, cleanupCard, page.ActualWidth >= 980);
                AssertControlInside(repair, repairCard);
                AssertControlInside(cleanup, cleanupCard);
                AssertControlInside(guidance, page);
                AssertControlInside(quickCheck, hero);
                Assert.True(quickCheck.TransformToVisual(hero).TransformPoint(default).X + quickCheck.ActualWidth
                    <= primary.TransformToVisual(hero).TransformPoint(default).X);
                if (width == 480 && sidebarWidth == 80)
                    await CheckGuidanceAsync(guidance, strings, theme);
            }
            // The overview opens a report, and the report's repair action updates it in place.
            await InvokeServicingAsync(repair, vm, WindowsServicingAction.Repair);
            Assert.IsType<WindowsStatusResultsPage>(frame.Content);
            frame.GoBack();
            page = Assert.IsType<WindowsStatusPage>(frame.Content);
            await Task.Delay(50);
            primary = (Button)page.FindName("PrimaryActionButton");
            CheckDiagnoseButton(primary, WindowsServicingAction.QuickCheck);
            await InvokeServicingAsync(primary, vm, WindowsServicingAction.QuickCheck);
            reportPage = Assert.IsType<WindowsStatusResultsPage>(frame.Content);
            await Task.Delay(50);
            resultRepair = Assert.IsType<Button>(reportPage.FindName("ReportRepairButton"));
            AssertControlInside(resultRepair, Assert.IsType<Border>(reportPage.FindName("ReportCard")));
            await InvokeServicingAsync(resultRepair, vm, WindowsServicingAction.Repair);
            Assert.Same(reportPage, frame.Content);
            Assert.Equal(WindowsImageHealth.Healthy, vm.Report!.ImageHealth);
            Assert.Equal(strings["WindowsStatus_OperationCompleted"], Assert.IsType<TextBlock>(reportPage.FindName("ReportHeading")).Text);
            Assert.Equal(strings["WindowsStatus_StateReviewFileResults"], Assert.IsType<TextBlock>(reportPage.FindName("ReportMessage")).Text);
            Assert.Equal(Visibility.Collapsed, resultRepair.Visibility);
            dialogs.Verify(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Exactly(4));

            frame.GoBack();
            page = Assert.IsType<WindowsStatusPage>(frame.Content);
            await Task.Delay(50);
            quickCheckNeedsRestart = true;
            await InvokeServicingAsync((Button)page.FindName("PrimaryActionButton"), vm, WindowsServicingAction.QuickCheck);
            reportPage = Assert.IsType<WindowsStatusResultsPage>(frame.Content);
            if (!reportPage.IsLoaded)
            {
                var reportLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reportPage.Loaded += (_, _) => reportLoaded.TrySetResult();
                await reportLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            var health = Assert.IsType<TextBlock>(reportPage.FindName("HealthOutcome"));
            Assert.Equal(strings["WindowsStatus_RestartRequiredTitle"], Assert.IsType<TextBlock>(reportPage.FindName("ReportHeading")).Text);
            Assert.Equal(strings["WindowsStatus_StateRestartRequired"], Assert.IsType<TextBlock>(reportPage.FindName("ReportMessage")).Text);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(reportPage.FindName("ReportRepairButton")).Visibility);
            Assert.Equal(strings["WindowsStatus_HealthRepairable"], health.Text);
            Assert.Equal(Visibility.Visible, health.Visibility);

            frame.GoBack();
            page = Assert.IsType<WindowsStatusPage>(frame.Content);
            await Task.Delay(50);
            diagnosisFoundCorruption = true;
            ToggleProvider((ToggleSwitch)page.FindName("QuickCheckSwitch")).Toggle();
            await InvokeServicingAsync((Button)page.FindName("PrimaryActionButton"), vm, WindowsServicingAction.Diagnosis);
            reportPage = Assert.IsType<WindowsStatusResultsPage>(frame.Content);
            await Task.Delay(50);
            Assert.Equal(string.Format(strings["WindowsStatus_AffectedComponentCount"], 1), Assert.IsType<TextBlock>(reportPage.FindName("ProblemCount")).Text);
            Assert.Equal("microsoft-windows-example", Assert.IsType<TextBlock>(reportPage.FindName("ProblemCategory")).Text);
            shell.Children.Clear();
            fixture.ViewModel = null;
        }
        Console.WriteLine("PASS: English/Italian, four widths, light/dark, Info popup, inline progress, report details, and cancellation toast with return to Repair.");
    }

    private static IToggleProvider ToggleProvider(ToggleSwitch toggle) =>
        Assert.IsAssignableFrom<IToggleProvider>(new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle));

    /// <summary>Checks that the diagnosis summary and its measurements fit the card at both widths.</summary>
    private async Task CheckDiagnosisReportAsync(WindowsStatusResultsPage page, Grid shell, Dictionary<string, string> strings)
    {
        var card = Assert.IsType<Border>(page.FindName("ReportCard"));
        var heading = Assert.IsType<TextBlock>(page.FindName("ReportHeading"));
        var message = Assert.IsType<TextBlock>(page.FindName("ReportMessage"));
        var size = Assert.IsType<TextBlock>(page.FindName("StoreSize"));
        var advice = Assert.IsType<TextBlock>(page.FindName("StoreCleanupAdvice"));
        foreach (var width in new[] { 480, 1100 })
        {
            _window!.AppWindow.ResizeClient(new SizeInt32(width, 1000));
            await Task.Delay(100);
            shell.UpdateLayout();
            AssertControlInside(heading, card);
            AssertControlInside(message, card);
            AssertControlInside(size, card);
            AssertControlInside(advice, card);
        }
        Assert.Equal(strings["WindowsStatus_OperationCompleted"], heading.Text);
        Assert.Equal(strings["WindowsStatus_StateReviewFileResults"], message.Text);
        Assert.Equal(string.Format(strings["WindowsStatus_StoreSize"], $"{5:N2} GiB"), size.Text);
        Assert.Equal(strings["WindowsStatus_CleanupRecommended"], advice.Text);
    }

    /// <summary>Waits for the invoked button to produce a fresh servicing report.</summary>
    private static async Task InvokeServicingAsync(Button button, WindowsStatusViewModel vm, WindowsServicingAction action)
    {
        var previous = vm.Report;
        var invocation = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        invocation.Invoke();
        for (var attempt = 0; attempt < 100 && (vm.IsBusy || ReferenceEquals(previous, vm.Report)); attempt++) await Task.Delay(20);
        Assert.False(vm.IsBusy);
        Assert.NotSame(previous, vm.Report);
        Assert.Equal(action, vm.Report?.Request.Action);
    }

    /// <summary>Invokes the Info button and checks the open flyout at a narrow window width.</summary>
    private static async Task CheckGuidanceAsync(Button button, Dictionary<string, string> strings, ElementTheme theme)
    {
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        Assert.False(flyout.IsOpen);
        Assert.Equal(strings["WindowsStatus_Maintenance"], AutomationProperties.GetName(button));
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void WhenOpened(object? sender, object args) => opened.TrySetResult();
        void WhenClosed(object? sender, object args) => closed.TrySetResult();
        flyout.Opened += WhenOpened;
        flyout.Closed += WhenClosed;
        try
        {
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
            invoke.Invoke();
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var content = Assert.IsType<ScrollViewer>(flyout.Content);
            Assert.Equal(theme, content.ActualTheme);
            Assert.InRange(content.ActualWidth, 1, button.XamlRoot.Size.Width);
            var texts = Descendants(content).OfType<TextBlock>().ToArray();
            Assert.Contains(texts, text => text.Text == strings["WindowsStatus_Maintenance"]);
            Assert.Contains(texts, text => text.Text == strings["WindowsStatus_Hint"]);
            Assert.Contains(texts, text => text.Text == strings["WindowsStatus_Logs"] && text.IsTextSelectionEnabled);
            var link = Assert.Single(Descendants(content).OfType<HyperlinkButton>());
            Assert.Equal(strings["WindowsStatus_Docs"], Assert.IsType<TextBlock>(link.Content).Text);
            Assert.Equal(new Uri("https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/repair-a-windows-image"), link.NavigateUri);
            flyout.Hide();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(flyout.IsOpen);
        }
        finally
        {
            flyout.Opened -= WhenOpened;
            flyout.Closed -= WhenClosed;
        }
    }

    /// <summary>Verifies that both cards share a row or fill the viewport in reading order.</summary>
    private static void AssertCardLayout(Grid cards, Border repair, Border cleanup, bool sideBySide)
    {
        var repairOrigin = repair.TransformToVisual(cards).TransformPoint(default);
        var cleanupOrigin = cleanup.TransformToVisual(cards).TransformPoint(default);
        if (sideBySide)
        {
            Assert.InRange(Math.Abs(repairOrigin.Y - cleanupOrigin.Y), 0, 1);
            Assert.True(cleanupOrigin.X > repairOrigin.X + repair.ActualWidth);
            Assert.InRange(Math.Abs(cleanupOrigin.X + cleanup.ActualWidth - cards.ActualWidth), 0, 1);
            return;
        }

        AssertFillsViewport(repair, cards);
        AssertFillsViewport(cleanup, cards);
        Assert.True(cleanupOrigin.Y > repairOrigin.Y + repair.ActualHeight);
    }

    /// <summary>Checks that the action fits inside the surface that presents it.</summary>
    private static void AssertControlInside(FrameworkElement control, FrameworkElement surface)
    {
        var origin = control.TransformToVisual(surface).TransformPoint(default);
        Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0);
        Assert.InRange(origin.X, 0, surface.ActualWidth - control.ActualWidth + 1);
        Assert.InRange(origin.Y, 0, surface.ActualHeight - control.ActualHeight + 1);
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

    private void Finish(string? error = null)
    {
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All Windows status checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
