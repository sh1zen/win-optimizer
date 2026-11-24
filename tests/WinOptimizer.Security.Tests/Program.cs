using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Moq;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Security;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Security;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.SecurityTests;

/// <summary>Exercises the production page with synthetic evidence and no system-security changes.</summary>
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
            _ = new SecurityTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class SecurityTestApplication(string outputDirectory) : WinUIApplication
{
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(outputDirectory);
        InitializeComponent(); UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid(); _window = new Window { Content = shell };
        var deadline = _window.DispatcherQueue.CreateTimer(); deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("Security presentation timed out."); deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try { await ContractsAsync(); await RenderAsync(shell); Finish(); }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    private static async Task ContractsAsync()
    {
        var count = 0;
        foreach (var method in typeof(SecurityViewModelTests).GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            using var tests = new SecurityViewModelTests();
            if (method.Invoke(tests, null) is Task task) await task;
            count++;
        }
        Console.WriteLine($"PASS: {count} security view-model checks.");
    }

    /// <summary>Checks scan actions and result bindings at compact and wide widths in the actual WinUI page.</summary>
    private async Task RenderAsync(Grid shell)
    {
        var window = Assert.IsType<Window>(_window);
        shell.RequestedTheme = ElementTheme.Dark;
        shell.Background = new SolidColorBrush(Colors.Black);
        foreach (var language in new[] { "en", "it" })
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = (await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(resource))!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(localization => localization.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            var backend = new Mock<ISecurityBackend>();
            TaskCompletionSource? pendingRead = null;
            backend.Setup(backend => backend.ReadAsync(It.IsAny<IReadOnlyList<SecurityCheckId>>(), It.IsAny<CancellationToken>()))
                .Returns(async (IReadOnlyList<SecurityCheckId> ids, CancellationToken token) =>
                {
                    if (pendingRead is { } hold) await hold.Task.WaitAsync(token);
                    return ids.Select(id =>
                        new SecurityObservation(id, id == SecurityCheckId.Updates ? SecurityCheckState.Unknown :
                            id is SecurityCheckId.Antivirus or SecurityCheckId.Firewall or SecurityCheckId.Spyware or SecurityCheckId.Uac ? SecurityCheckState.Good : SecurityCheckState.Attention,
                            id is SecurityCheckId.Isatap or SecurityCheckId.Teredo ? "Automatic" : "Enabled",
                            id == SecurityCheckId.Uac ? "EnableLUA=1; ConsentPromptBehaviorAdmin=5" : "Synthetic test evidence",
                            id >= SecurityCheckId.Isatap)).ToArray();
                });
            var tasks = new Mock<ITaskProgressService>(); CancellationTokenSource? current = null;
            tasks.SetupGet(tasks => tasks.CurrentTaskCancellationSource).Returns(() => current);
            tasks.Setup(tasks => tasks.StartTask(It.IsAny<string>(), true, true, true)).Returns(() => current = new());
            tasks.Setup(tasks => tasks.CompleteTask()).Callback(() => { current?.Dispose(); current = null; });
            var mode = new Mock<IApplicationModeService>();
            using var vm = new SecurityViewModel(new SecurityScanService(backend.Object, mode.Object), Mock.Of<IDialogService>(), localization.Object,
                mode.Object, new ExtensionTestDispatcher(), tasks.Object);
            var page = new SecurityPage(vm); shell.Children.Clear(); shell.Children.Add(page);
            var intro = (ScrollViewer)page.FindName("ScanIntro");
            var actions = (Panel)page.FindName("ScanActions");
            var scanAction = (ContentControl)page.FindName("ScanAction");
            var scan = (Button)page.FindName("ScanButton");
            var deepScan = (Button)page.FindName("DeepScanButton");
            foreach (var width in new[] { 480, 1100 })
            {
                window.AppWindow.ResizeClient(new SizeInt32(width, 900));
                await Task.Delay(120); shell.UpdateLayout();
                var wide = width >= 900;
                Assert.False(vm.HasReport);
                Assert.Equal(Visibility.Visible, intro.Visibility);
                Assert.Equal(Visibility.Collapsed, actions.Visibility);
                Assert.Equal(wide ? 1 : 0, Grid.GetColumn(scanAction));
                Assert.Equal(wide ? 0 : 1, Grid.GetRow(scanAction));
                Assert.Equal(136, scan.ActualWidth);
                Assert.Equal(scan.ActualWidth, scan.ActualHeight);
                Assert.Equal(new CornerRadius(68), scan.CornerRadius);
                Assert.True(deepScan.ActualWidth > 0);
                await CaptureAsync(shell, wide ? $"security-{language}-intro.png" : $"security-{language}-intro-{width}.png");
            }
            Assert.Equal(vm.ScanLabel, scan.Content);
            Assert.Equal(vm.DeepScanLabel, deepScan.Content);
            Assert.Same(vm.ScanCommand, scan.Command);
            Assert.Same(vm.DeepScanCommand, deepScan.Command);
            backend.VerifyNoOtherCalls();

            pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanning = Assert.IsAssignableFrom<IAsyncRelayCommand>(scan.Command).ExecuteAsync(scan.CommandParameter);
            await RenderScanProgressAsync(shell, page, language);
            var cancel = Assert.IsType<Button>(page.FindName("CancelScanButton"));
            var token = current!.Token;
            cancel.Command.Execute(cancel.CommandParameter);
            Assert.True(token.IsCancellationRequested);
            await scanning;
            pendingRead = null;
            Assert.False(vm.HasReport);
            Assert.Equal(Visibility.Visible, intro.Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<ScrollViewer>(page.FindName("ScanProgressView")).Visibility);

            await Assert.IsAssignableFrom<IAsyncRelayCommand>(scan.Command).ExecuteAsync(scan.CommandParameter);
            Assert.True(vm.HasReport);
            Assert.Equal(Visibility.Collapsed, intro.Visibility);
            Assert.Equal(Visibility.Visible, actions.Visibility);
            var rescan = (Button)page.FindName("RescanButton");
            var deepRescan = (Button)page.FindName("DeepRescanButton");
            Assert.Same(vm.ScanCommand, rescan.Command);
            Assert.Same(vm.DeepScanCommand, deepRescan.Command);
            await RenderStandardResultsAsync(shell, page, language);
            pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var deepScanning = Assert.IsAssignableFrom<IAsyncRelayCommand>(deepRescan.Command).ExecuteAsync(deepRescan.CommandParameter);
            Assert.Equal(Visibility.Visible, Assert.IsType<ScrollViewer>(page.FindName("ScanProgressView")).Visibility);
            Assert.False(vm.HasReport);
            pendingRead.SetResult();
            await deepScanning;
            pendingRead = null;
            Assert.Equal(5, vm.Categories.Count); Assert.Equal(6, vm.Findings.Count);
            await Task.Delay(100);
            shell.UpdateLayout();
            var deepList = Assert.IsType<ListView>(page.FindName("FindingsList"));
            var fixable = vm.Findings.First(row => row.CanSelect);
            var fixableContainer = Assert.IsType<ListViewItem>(deepList.ContainerFromItem(fixable));
            var checkbox = Assert.IsType<CheckBox>(FindNamed(fixableContainer, "FindingSelection"));
            Assert.True(checkbox.IsEnabled);
            Assert.Equal(false, checkbox.IsChecked);
            checkbox.IsChecked = true;
            Assert.True(fixable.IsSelected);
            Assert.True(vm.FixCommand.CanExecute(null));
            vm.SelectAllCommand.Execute(null);
            Assert.All(vm.Findings, row => Assert.Equal(row.CanSelect, row.IsSelected));
            vm.SelectAllCommand.Execute(null);
            Assert.All(vm.Findings, row => Assert.False(row.IsSelected));
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var width in new[] { 480, 1100, 1500 })
            {
                shell.RequestedTheme = theme; shell.Background = new SolidColorBrush(theme == ElementTheme.Light ? Colors.White : Colors.Black);
                window.AppWindow.ResizeClient(new SizeInt32(width, 900)); await Task.Delay(100); shell.UpdateLayout();
                var picker = (ComboBox)page.FindName("CategoryPicker");
                Assert.Equal(width < 900 ? Visibility.Visible : Visibility.Collapsed, picker.Visibility);
                var sidebar = (Border)page.FindName("CategorySidebar");
                Assert.Equal(width < 900 ? Visibility.Collapsed : Visibility.Visible, sidebar.Visibility);
                var list = (ListView)page.FindName("FindingsList");
                Assert.Equal(6, list.Items.Count); Assert.True(list.ActualWidth > 200);
                var origin = list.TransformToVisual(shell).TransformPoint(default);
                Assert.True(origin.X >= 0 && origin.X + list.ActualWidth <= shell.ActualWidth + 1);
                Assert.True(list.ActualHeight > 150 && origin.Y + list.ActualHeight <= shell.ActualHeight + 1);
                var metrics = Assert.IsType<UniformWrapPanel>(FindNamed(list, "FindingMetrics"));
                Assert.Equal(3, metrics.Children.Count);
                var lastMetricOrigin = metrics.Children[2].TransformToVisual(metrics).TransformPoint(default);
                Assert.Equal(width == 480, lastMetricOrigin.Y > 0);
                await CaptureAsync(shell, $"security-{language}-{theme}-{width}.png");
            }
        }
        Console.WriteLine("PASS: English/Italian production Security page, three widths and light/dark themes.");
    }

    /// <summary>Checks a standard report, its displayed technical evidence, and the unavailable-check shortcut.</summary>
    private async Task RenderStandardResultsAsync(Grid shell, SecurityPage page, string language)
    {
        var window = Assert.IsType<Window>(_window);
        shell.RequestedTheme = ElementTheme.Dark;
        shell.Background = new SolidColorBrush(Colors.Black);
        window.AppWindow.ResizeClient(new SizeInt32(1100, 680));
        await Task.Delay(100);
        shell.UpdateLayout();

        var vm = page.ViewModel;
        Assert.Equal(SecurityArea.Weaknesses, vm.SelectedCategory?.Area);
        Assert.Equal(0, vm.ReviewCount);
        Assert.Equal(1, vm.UnavailableCount);
        Assert.False(vm.FixCommand.CanExecute(null));
        var list = Assert.IsType<ListView>(page.FindName("FindingsList"));
        var uac = Assert.Single(vm.Findings);
        var checkbox = Assert.IsType<CheckBox>(FindNamed(list, "FindingSelection"));
        Assert.False(checkbox.IsEnabled);
        Assert.Equal(false, checkbox.IsChecked);
        var expander = Assert.IsType<Expander>(FindNamed(list, "FindingDetails"));
        expander.IsExpanded = true;
        await Task.Delay(100);
        shell.UpdateLayout();
        Assert.True(uac.AreDetailsExpanded);
        Assert.Contains("EnableLUA=1; ConsentPromptBehaviorAdmin=5", uac.Evidence);
        var evidence = Assert.IsType<ItemsControl>(FindNamed(list, "StructuredEvidence"));
        Assert.Equal(new SecurityEvidenceValue("EnableLUA", "1"), evidence.Items[0]);
        Assert.Equal(new SecurityEvidenceValue("ConsentPromptBehaviorAdmin", "5"), evidence.Items[1]);
        await CaptureAsync(shell, $"security-{language}-results.png");

        var unavailable = Assert.IsType<Button>(page.FindName("ViewUnavailableButton"));
        Assert.Equal(Visibility.Visible, unavailable.Visibility);
        var shortcut = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(unavailable).GetPattern(PatternInterface.Invoke));
        shortcut.Invoke();
        await Task.Delay(100);
        shell.UpdateLayout();
        Assert.Equal(SecurityArea.Updates, vm.SelectedCategory?.Area);
        var unavailableRow = Assert.Single(vm.Findings);
        Assert.True(unavailableRow.IsUnavailable);
        Assert.True(unavailableRow.AreDetailsExpanded);
        Assert.Equal("Synthetic test evidence", unavailableRow.DetailsText);
        Assert.True(uac.AreDetailsExpanded);
    }

    /// <summary>Checks the live scan view and captures the wide and compact layouts.</summary>
    private async Task RenderScanProgressAsync(Grid shell, SecurityPage page, string language)
    {
        var window = Assert.IsType<Window>(_window);
        shell.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 24, 25, 28));
        var progress = Assert.IsType<ScrollViewer>(page.FindName("ScanProgressView"));
        var cover = Assert.IsType<ItemsControl>(page.FindName("ScanCoverageCards"));
        var cancel = Assert.IsType<Button>(page.FindName("CancelScanButton"));
        var details = Assert.IsType<Button>(page.FindName("ScanDetailsButton"));
        var options = Assert.IsType<Button>(page.FindName("DeepScanOptionsButton"));
        var flyout = Assert.IsType<Flyout>(options.Flyout);
        Assert.Equal(Visibility.Visible, progress.Visibility);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<ScrollViewer>(page.FindName("ScanIntro")).Visibility);
        Assert.True(Assert.IsType<ProgressBar>(page.FindName("ScanProgressBar")).IsIndeterminate);
        Assert.True(cancel.IsEnabled);
        Assert.True(details.IsEnabled);
        Assert.Equal(5, cover.Items.Count);
        foreach (var width in new[] { 480, 1100 })
        {
            window.AppWindow.ResizeClient(new SizeInt32(width, width < 900 ? 900 : 680));
            await Task.Delay(350);
            shell.UpdateLayout();
            Assert.Equal(width >= 900 ? 0 : 1, Grid.GetRow(cancel));
            var items = cover.Items.Select(item => Assert.IsAssignableFrom<FrameworkElement>(cover.ContainerFromItem(item))).ToArray();
            var tops = items.Select(item => item.TransformToVisual(cover).TransformPoint(default).Y).ToArray();
            Assert.Equal(width >= 900 ? 1 : 3, tops.Distinct().Count());
            Assert.All(items, item =>
            {
                var left = item.TransformToVisual(cover).TransformPoint(default).X;
                Assert.InRange(left + item.ActualWidth, 1, cover.ActualWidth + 1);
            });
            await CaptureAsync(shell, $"security-{language}-progress-{width}.png");
        }
        flyout.ShowAt(options);
        await Task.Yield();
        var contents = Assert.IsType<StackPanel>(flyout.Content);
        var checks = Assert.Single(contents.Children.OfType<ItemsControl>());
        Assert.Equal(SecurityCheck.All.Count(check => check.DeepOnly), checks.Items.Count);
        flyout.Hide();
    }

    private static FrameworkElement? FindNamed(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement element && element.Name == name) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindNamed(VisualTreeHelper.GetChild(parent, index), name) is { } match) return match;
        return null;
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
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All security checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
