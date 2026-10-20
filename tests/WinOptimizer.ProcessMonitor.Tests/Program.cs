using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Moq;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.ProcessMonitorTests;

/// <summary>Runs the actual monitor page with synthetic process data and no Windows observation or ETW capture.</summary>
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
            _ = new ProcessMonitorTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class ProcessMonitorTestApplication(string outputDirectory) : WinUIApplication
{
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(outputDirectory); InitializeComponent();
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid { Padding = new Thickness(24, 0, 24, 0) }; _window = new Window { Content = shell };
        var deadline = _window.DispatcherQueue.CreateTimer(); deadline.Interval = TimeSpan.FromSeconds(90);
        deadline.Tick += (_, _) => Finish("Process monitor presentation timed out."); deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try { await ContractsAsync(); await RenderAsync(shell); Finish(); }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    private static async Task ContractsAsync()
    {
        var passed = 0;
        foreach (var method in typeof(ProcessMonitorViewModelTests).GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            var data = method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray();
            foreach (var row in data.Length == 0 ? new[] { Array.Empty<object>() } : data)
            {
                var tests = new ProcessMonitorViewModelTests();
                try { if (method.Invoke(tests, row) is Task task) await task; passed++; }
                catch (TargetInvocationException ex) { throw new InvalidOperationException(method.Name, ex.InnerException); }
            }
        }
        Console.WriteLine($"PASS: {passed} production process-monitor view-model cases.");
    }

    private async Task RenderAsync(Grid shell)
    {
        var id = new ProcessIdentity(100, 1000);
        foreach (var language in new[] { "en", "it" })
        {
            using var json = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            var service = new Mock<IProcessMonitorService>(MockBehavior.Strict); var running = false;
            var metrics = new[]
            {
                Metric(id, 0, "Example browser", 12.4, 512), Metric(new(110, 1100), 100, "Example renderer", 3.2, 180),
                Metric(new(120, 1200), 100, "Example networking", 1.1, 64), Metric(new(200, 2000), 0, "Example editor", 0.5, 240),
                Metric(new(4, 0), 0, "System (restricted)", null, 24)
            };
            var ports = new ProcessPort[] { new("TCP4", "192.0.2.10", 55000, "198.51.100.20", 443, "Established"), new("TCP6", "2001:db8::1", 55001, "2001:db8::2", 443, "Established"), new("UDP4", "0.0.0.0", 5353, "", null, "Bound") };
            service.Setup(source => source.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ProcessSnapshot(DateTimeOffset.Now, metrics));
            service.Setup(source => source.InspectAsync(It.IsAny<ProcessIdentity>(), It.IsAny<CancellationToken>())).ReturnsAsync((ProcessIdentity identity, CancellationToken _) =>
                new ProcessInspection(identity, DateTimeOffset.Now, "\"C:\\Program Files\\Example\\Browser.exe\" --profile=Example", "EXAMPLE\\User", "Example browser", "Example Software", ports, []));
            service.Setup(source => source.ReadPortsAsync(It.IsAny<ProcessIdentity>(), It.IsAny<CancellationToken>())).ReturnsAsync((ProcessIdentity identity, CancellationToken _) => new ProcessPortSnapshot(identity, DateTimeOffset.Now, ports, []));
            service.Setup(source => source.ReadOpenFilesAsync(It.IsAny<ProcessIdentity>(), true, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessIdentity identity, bool _, CancellationToken _) =>
                new ProcessFileSnapshot(identity, DateTimeOffset.Now, [new("A0", "File", "C:\\Example profile\\Documents\\Example document.txt"), new("B4", "File", "C:\\Example profile\\Browser\\Cache\\cache-data.bin"), new("C8", "File", "C:\\Program Files\\Example\\Resources\\settings.json")], "Synthetic file references"));
            ProcessNetworkSnapshot Network(ProcessNetworkState state) => new(id, DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now, state,
                [new("TCP", "198.51.100.20", 1_048_576, 8_388_608, 1500, DateTimeOffset.Now), new("TCP6", "2001:db8::2", 65_536, 262_144, 90, DateTimeOffset.Now), new("UDP", "203.0.113.50", 4096, 8192, 20, DateTimeOffset.Now)], 0, 0, false, "");
            service.Setup(source => source.StartNetworkAsync(id, It.IsAny<CancellationToken>())).Callback(() => running = true).Returns(Task.CompletedTask);
            service.Setup(source => source.ReadNetworkAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => running ? Network(ProcessNetworkState.Running) : null);
            service.Setup(source => source.StopNetworkAsync()).ReturnsAsync(() => { var prior = running; running = false; return prior ? Network(ProcessNetworkState.Stopped) : null; });
            using var vm = new ProcessMonitorViewModel(service.Object, localization.Object, Mock.Of<IApplicationModeService>(), new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());
            var page = new ProcessMonitorPage(vm); var loaded = new TaskCompletionSource(); page.Loaded += (_, _) => loaded.TrySetResult();
            shell.Children.Clear(); shell.Children.Add(page); await loaded.Task; await vm.SelectAsync(vm.Rows.First(row => row.Identity == id));
            // Exercise the production header bindings and selector together, without resampling.
            foreach (var (name, mode) in new[] { ("Name", 3), ("Pid", 4), ("Cpu", 1), ("Memory", 2) })
            {
                var header = (Button)page.FindName(name + "ColumnHeader");
                Assert.NotNull(header.Command); header.Command.Execute(header.CommandParameter);
                Assert.Equal(mode, vm.SortMode); Assert.Equal(mode, ((ComboBox)page.FindName("SortSelector")).SelectedIndex);
                var descending = vm.SortDescending; header.Command.Execute(header.CommandParameter);
                Assert.Equal(!descending, vm.SortDescending); Assert.Equal(id, vm.SelectedRow!.Identity);
            }
            vm.SortMode = 0;
            service.Verify(source => source.ReadOpenFilesAsync(It.IsAny<ProcessIdentity>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.False(running); vm.LicenseAccepted = true; await vm.LoadFilesCommand.ExecuteAsync(null); await vm.StartNetworkCommand.ExecuteAsync(null);
            Assert.Equal(3, vm.Files.Count); Assert.Equal(3, vm.Hosts.Count);
            var tabs = (Pivot)page.FindName("DetailsTabs");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var width in new[] { 760, 1100, 1500 })
            foreach (var tab in new[] { 0, 1, 2, 3 })
            {
                shell.RequestedTheme = theme; shell.Background = new SolidColorBrush(theme == ElementTheme.Light ? Colors.White : Colors.Black);
                _window!.AppWindow.ResizeClient(new SizeInt32(width, 1100)); tabs.SelectedIndex = tab;
                // Pivot uses compositor transitions; wait for the selected item's content before rendering.
                await Task.Delay(650); shell.UpdateLayout();
                var inspector = (Border)page.FindName("Inspector"); var list = (ListView)page.FindName("ProcessesList");
                Assert.True(list.ActualHeight >= 160); Assert.Equal(page.ActualWidth < 1000 ? 1 : 0, Grid.GetRow(inspector));
                var headers = (Grid)page.FindName("ProcessHeaders");
                var container = (ListViewItem)list.ContainerFromIndex(0);
                var rowGrid = FindRowGrid(container);
                Assert.NotNull(rowGrid);
                Assert.Equal(headers.ColumnDefinitions.Select(column => column.Width), rowGrid.ColumnDefinitions.Select(column => column.Width));
                Assert.Equal(headers.Padding.Left, rowGrid.Padding.Left);
                Assert.Equal(headers.Padding.Right, rowGrid.Padding.Right);
                Assert.InRange(rowGrid.ActualHeight, 40, 48);
                var cpuCell = rowGrid.Children.OfType<StackPanel>().Single();
                Assert.Equal(2, Grid.GetColumn(cpuCell)); Assert.Single(cpuCell.Children.OfType<ProgressBar>());
                foreach (var control in new FrameworkElement[] { inspector, list, (TextBox)page.FindName("SearchBox") })
                {
                    var point = control.TransformToVisual(shell).TransformPoint(default);
                    Assert.True(control.ActualWidth > 0 && point.X >= 0 && point.X + control.ActualWidth <= shell.ActualWidth + 1, "A control exceeds the client width.");
                }
                if (tab != 0)
                {
                    var detailsList = (ListView)page.FindName(tab == 1 ? "PortsList" : tab == 2 ? "OpenFilesList" : "HostTrafficList");
                    Assert.True(detailsList.ActualHeight >= 90, $"Tab {tab} leaves no usable scrolling space.");
                }
                await CaptureAsync(shell, $"process-monitor-{language}-{theme}-{width}-tab{tab}.png");
                if (width == 1100 && tab == 0)
                {
                    var cpuHeader = (Button)page.FindName("CpuColumnHeader");
                    cpuHeader.Command.Execute(cpuHeader.CommandParameter); await Task.Delay(80); shell.UpdateLayout();
                    Assert.EndsWith("↓", (string)cpuHeader.Content);
                    await CaptureAsync(shell, $"process-monitor-{language}-{theme}-{width}-cpu-sort.png");
                    vm.SortMode = 0; await Task.Delay(80);
                }
            }
            _window!.AppWindow.ResizeClient(new SizeInt32(760, 600)); tabs.SelectedIndex = 3; await Task.Delay(650); shell.UpdateLayout();
            var scroller = (ScrollViewer)page.FindName("PageScroll"); Assert.True(scroller.ScrollableHeight > 0);
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true); await Task.Delay(80);
            await CaptureAsync(shell, $"process-monitor-{language}-short.png");
            await vm.DeactivateAsync(); Assert.False(running); shell.Children.Clear();
        }
        Console.WriteLine("PASS: real process tree, four inspector tabs, EN/IT, light/dark, three widths, short windows and navigation cleanup. Synthetic data; no live process scan, download or ETW session.");
    }

    /// <summary>Finds the actual data-template root after ListView virtualization creates its container.</summary>
    private static Grid? FindRowGrid(DependencyObject root)
    {
        if (root is Grid { Name: "ProcessRowGrid" } row) return row;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindRowGrid(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    private static ProcessMetric Metric(ProcessIdentity id, int parent, string name, double? cpu, int memoryMiB) =>
        new(new(id, parent, name, "C:\\Program Files\\Example\\" + name + ".exe", 1000, memoryMiB * 1024L * 1024, memoryMiB * 2048L * 1024, 100_000, 200_000, 12, 200, ""), cpu, 65_536, 131_072);

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
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All process monitor checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
