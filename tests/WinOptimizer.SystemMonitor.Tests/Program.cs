using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using Moq;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;

namespace WinOptimizer.SystemMonitorTests;

/// <summary>Runs saved-preference tests and the real widget with synthetic telemetry on the UI thread.</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new MonitorTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

/// <summary>Exercises production sizing and captures each scale without sampling hardware or changing preferences.</summary>
public sealed partial class MonitorTestApplication(string output) : Microsoft.UI.Xaml.Application
{
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        InitializeComponent();
        Directory.CreateDirectory(output);
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid();
        _window = new Window { Content = shell };
        var deadline = _window.DispatcherQueue.CreateTimer();
        deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("SysMonitor tests timed out.");
        deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try
            {
                var count = 0;
                foreach (var type in new[] { typeof(ExtensionsViewModelTests), typeof(SystemMonitorPresentationTests) })
                foreach (var method in type.GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
                {
                    var cases = method.GetCustomAttributes<InlineDataAttribute>().Select(data => data.GetData(method)).SelectMany(data => data).ToArray();
                    foreach (var arguments in cases.Length == 0 ? new[] { Array.Empty<object>() } : cases)
                    {
                        try { if (method.Invoke(Activator.CreateInstance(type), arguments) is Task task) await task; count++; }
                        catch (TargetInvocationException ex) { throw new InvalidOperationException(type.Name + "." + method.Name, ex.InnerException); }
                    }
                }
                await CheckExpandedSettingsGridAsync(shell);
                await CheckNativeScalingAsync();
                await CheckNativeAlignmentAsync();
                await CheckNativeChartsAsync();
                deadline.Stop();
                Finish($"PASS: {count} SysMonitor preference/presentation cases, responsive settings grid and preference binding, 8 native scale/layout cases, centered widget and hardware charts in both themes.", true);
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    /// <summary>Checks labeled toggle tiles, width-dependent wrapping and saves through the production settings page.</summary>
    private async Task CheckExpandedSettingsGridAsync(Grid shell)
    {
        var saved = new SystemMonitorOptions { ShowFans = false, ShowGpuUsage = true };
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(saved);
        preferences.Setup(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Succeeded());
        var host = new Mock<ISystemMonitorWidgetHost>();
        host.Setup(h => h.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>())).Returns(Task.CompletedTask);
        var startup = new Mock<ISystemMonitorStartupService>();
        startup.Setup(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Succeeded());
        using var vm = new ExtensionsViewModel(preferences.Object, CreateLocalization(), host.Object, new ExtensionTestDispatcher(), startup.Object);
        var page = new SystemMonitorSettingsPage(vm) { Width = 760, RequestedTheme = ElementTheme.Dark };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Loaded += (_, _) => loaded.TrySetResult();
        shell.Children.Add(page);
        try
        {
            await loaded.Task;
            Assert.True(await vm.LoadAsync());
            shell.UpdateLayout();
            var grid = Assert.IsType<UniformWrapPanel>(page.FindName("ExpandedMetricsGrid"));
            var tiles = grid.Children.Cast<ToggleButton>().ToArray();
            Assert.Equal(12, tiles.Length);
            var labels = new[] { vm.TemperaturesLabel, vm.FansLabel, vm.MemoryLabel, vm.CpuLabel, vm.NetworkLabel,
                vm.GpuUsageLabel, vm.GpuMemoryLabel, vm.ClockSpeedLabel, vm.PowerLabel, vm.VoltageLabel,
                vm.StorageActivityLabel, vm.StorageThroughputLabel };
            var icons = tiles.Select((tile, index) =>
            {
                Assert.True(tile.IsEnabled);
                Assert.Equal(labels[index], new ToggleButtonAutomationPeer(tile).GetName());
                var content = Assert.IsType<StackPanel>(tile.Content);
                Assert.Equal(labels[index], Assert.IsType<TextBlock>(content.Children[1]).Text);
                return Assert.IsType<FluentIcons.WinUI.SymbolIcon>(content.Children[0]).Symbol;
            }).ToArray();
            Assert.Equal(tiles.Length, icons.Distinct().Count());

            Point Origin(int index) => tiles[index].TransformToVisual(grid).TransformPoint(new Point());
            Assert.True(Origin(3).X > Origin(0).X);
            Assert.Equal(Origin(0).Y, Origin(3).Y);
            Assert.True(Origin(4).Y > Origin(0).Y);
            Assert.Equal(Origin(4).Y, Origin(5).Y);
            Assert.Equal(tiles[4].ActualHeight, tiles[5].ActualHeight);
            var scroll = Assert.IsType<ScrollViewer>(page.Content);
            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            await SavePreviewAsync(await RenderAsync(grid), "expanded-settings.png");

            page.Width = 420;
            shell.UpdateLayout();
            Assert.True(Origin(1).X > Origin(0).X);
            Assert.Equal(Origin(0).Y, Origin(1).Y);
            Assert.True(Origin(2).Y > Origin(0).Y);
            Assert.Equal(Origin(0).X, Origin(2).X);
            Assert.All(tiles, tile => Assert.InRange(tile.ActualWidth, 1, grid.ActualWidth));

            preferences.Verify(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<SystemMonitorOptions>()), Times.Never);
            var fans = new ToggleButtonAutomationPeer(tiles[1]);
            var gpuUsage = new ToggleButtonAutomationPeer(tiles[5]);
            Assert.False(tiles[1].IsChecked);
            Assert.True(tiles[5].IsChecked);
            ((IToggleProvider)fans.GetPattern(PatternInterface.Toggle)).Toggle();
            ((IToggleProvider)gpuUsage.GetPattern(PatternInterface.Toggle)).Toggle();
            Assert.True(await vm.FlushAsync());
            var updated = saved with { ShowFans = true, ShowGpuUsage = false };
            Assert.Equal(updated, vm.Options);
            preferences.Verify(p => p.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, updated), Times.Once);
            host.Verify(h => h.ApplyOptionsAsync(updated), Times.Once);
        }
        finally { shell.Children.Remove(page); }
    }

    /// <summary>Checks the expanded widget's alignment with a partial sensor row and optional diagnostics.</summary>
    private async Task CheckNativeAlignmentAsync()
    {
        var vm = new SystemMonitorReadingsViewModel(CreateLocalization());
        var widget = new SystemMonitorWindow(vm);
        var sample = new SystemMonitorSnapshot(DateTimeOffset.UnixEpoch, 10, 32UL << 30, 18UL << 30, 20480, 11468,
            [new("GPU Core", 68), new("GPU Hot Spot", 74), new("Composite", 38),
                new("Warning Temperature", 69), new("Critical Temperature", 74)], [], ["Synthetic sensor notice"]);
        try
        {
            var options = new SystemMonitorOptions { IsCompact = false };
            widget.ApplyOptions(options, ElementTheme.Dark);
            widget.Show();
            for (var i = 0; i < 60; i++)
                widget.Publish(sample with { CollectedAt = sample.CollectedAt.AddSeconds(i), CpuUsagePercent = 10 + 8 * Math.Sin(i / 6d) });

            var root = Assert.IsType<Grid>(widget.Content);
            var panel = Assert.IsType<SystemMonitorPanel>(Assert.IsType<Viewbox>(Assert.Single(root.Children)).Child);
            var sizeButton = Assert.IsType<Button>(panel.FindName("SizeButton"));
            var diagnostics = Assert.IsType<Button>(panel.FindName("DiagnosticsButton"));
            var temperatures = Assert.IsType<Border>(panel.FindName("TemperaturesCard"));
            var fans = Assert.IsType<Border>(panel.FindName("FansCard"));
            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                widget.ApplyOptions(options, theme);
                var bitmap = await RenderAsync(root);
                var center = panel.ActualWidth / 2;
                var pixel = 1 / root.XamlRoot.RasterizationScale;
                Assert.InRange(CenterOf(Assert.IsType<StackPanel>(panel.FindName("WidgetTitle")), panel), center - pixel, center + pixel);
                Assert.InRange(CenterOf(sizeButton, panel), center - pixel, center + pixel);
                Assert.Equal(Visibility.Visible, diagnostics.Visibility);
                foreach (var name in new[] { "TemperaturesCard", "FansCard", "MemoryCard", "CpuReadings", "NetworkReadings" })
                {
                    var card = Assert.IsType<Border>(panel.FindName(name));
                    Assert.InRange(CenterOf(card, panel), center - pixel, center + pixel);
                    Assert.InRange(card.ActualWidth, temperatures.ActualWidth - pixel, temperatures.ActualWidth + pixel);
                }

                var tiles = Assert.Single(Descendants<UniformWrapPanel>(temperatures)).Children.Cast<FrameworkElement>().ToArray();
                Assert.Equal(5, tiles.Length);
                Assert.InRange(Math.Abs(CenterOf(tiles[1], panel) - center), 0, pixel);
                Assert.InRange(Math.Abs((CenterOf(tiles[3], panel) + CenterOf(tiles[4], panel)) / 2 - center), 0, pixel);
                Assert.All(tiles, tile => Assert.InRange(Math.Abs(tile.ActualWidth - tiles[0].ActualWidth), 0, pixel));
                var empty = Assert.Single(Descendants<TextBlock>(fans), label => label.Text == vm.UnavailableLabel);
                Assert.Equal(Visibility.Visible, empty.Visibility);
                Assert.Equal(TextAlignment.Center, empty.TextAlignment);
                Assert.Equal(Visibility.Collapsed, Assert.Single(Descendants<ScrollViewer>(fans)).Visibility);
                await SavePreviewAsync(bitmap, $"expanded-{theme.ToString().ToLowerInvariant()}.png");
            }

            widget.Publish(sample with
            {
                Temperatures = Enumerable.Range(1, 12).Select(i => new SystemMonitorSensor($"Temperature {i}", 40 + i)).ToArray(),
                Fans = [new("CPU Fan", 1200)],
                Notices = []
            });
            await RenderAsync(root);
            Assert.Equal(Visibility.Collapsed, diagnostics.Visibility);
            Assert.InRange(Math.Abs(CenterOf(sizeButton, panel) - panel.ActualWidth / 2), 0, 1 / root.XamlRoot.RasterizationScale);
            Assert.Equal(Visibility.Visible, Assert.Single(Descendants<ScrollViewer>(fans)).Visibility);
            Assert.True(Assert.Single(Descendants<ScrollViewer>(temperatures)).ScrollableHeight > 0);
        }
        finally { widget.CloseFromHost(); }
    }

    /// <summary>Measures an element's horizontal center in the widget's unscaled coordinates.</summary>
    private static double CenterOf(FrameworkElement element, FrameworkElement panel) =>
        element.TransformToVisual(panel).TransformPoint(new Point(element.ActualWidth / 2, 0)).X;

    /// <summary>Checks rendered scaling, layout changes, header hit regions and work-area scrolling using the production window.</summary>
    private async Task CheckNativeScalingAsync()
    {
        var vm = new SystemMonitorReadingsViewModel(CreateLocalization());
        var widget = new SystemMonitorWindow(vm);
        try
        {
            widget.Publish(new(DateTimeOffset.UnixEpoch, 25, 16UL << 30, 8UL << 30, 1024, 2048,
                [new("CPU Package", 45)], [new("CPU Fan", 1200)], []));
            widget.Show();
            var root = Assert.IsType<Grid>(widget.Content);
            var viewbox = Assert.IsType<Viewbox>(Assert.Single(root.Children));
            var panel = Assert.IsType<SystemMonitorPanel>(viewbox.Child);
            foreach (var compact in new[] { true, false })
            foreach (var scale in new[] { 50d, 100d, 135d, 200d })
            {
                widget.ApplyOptions(new() { IsCompact = compact, ScalePercent = scale }, ElementTheme.Dark);
                root.UpdateLayout();
                var image = await RenderAsync(root);
                var width = compact ? SystemMonitorPanel.CompactWidth : SystemMonitorPanel.ExpandedWidth;
                Assert.InRange(root.ActualWidth, width * scale / 100 - 1, width * scale / 100 + 1);
                var origin = panel.TransformToVisual(root).TransformPoint(new Point());
                var edge = panel.TransformToVisual(root).TransformPoint(new Point(panel.ActualWidth, panel.ActualHeight));
                // Fractional scales round the layout to physical pixels before the Viewbox fits it uniformly.
                var pixel = 1 / root.XamlRoot.RasterizationScale;
                Assert.InRange(edge.X - origin.X, viewbox.Width - pixel, viewbox.Width + pixel);
                Assert.InRange(edge.Y - origin.Y, viewbox.Height - pixel, viewbox.Height + pixel);
                Assert.True(panel.DragRegion.ActualHeight > 0);
                Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0, $"Empty render for compact={compact}, scale={scale}, size={root.ActualWidth}x{root.ActualHeight}.");
                var area = DisplayArea.GetFromWindowId(widget.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                Assert.True(widget.AppWindow.Size.Width <= area.Width && widget.AppWindow.Size.Height <= area.Height);
            }
            // Long diagnostics must remain scrollable even on a large display at the largest scale.
            var workArea = DisplayArea.GetFromWindowId(widget.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            widget.ShowError(string.Join('\n', Enumerable.Repeat("Synthetic diagnostic", workArea.Height / 10 + 1)));
            root.UpdateLayout();
            await RenderAsync(root);
            var viewport = Assert.IsType<ScrollViewer>(panel.FindName("ReadingsViewport"));
            Assert.True(viewport.ScrollableHeight > 0);
            var readings = Assert.IsType<StackPanel>(panel.FindName("ReadingsSurface"));
            Assert.InRange(Math.Abs(CenterOf(readings, panel) - panel.ActualWidth / 2), 0, 1 / root.XamlRoot.RasterizationScale);
            Assert.InRange(widget.AppWindow.Size.Height, 1,
                DisplayArea.GetFromWindowId(widget.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea.Height);
        }
        finally { widget.CloseFromHost(); }
    }

    /// <summary>Renders all chart categories with synthetic histories and verifies visible traces in both palettes.</summary>
    private async Task CheckNativeChartsAsync()
    {
        var vm = new SystemMonitorReadingsViewModel(CreateLocalization());
        var widget = new SystemMonitorWindow(vm);
        try
        {
            var options = new SystemMonitorOptions { IsCompact = false, ShowTemperatures = false, ShowFans = false,
                ShowMemory = false, ShowCpu = false, ShowNetwork = false, ShowGpuUsage = true, ShowGpuMemory = true,
                ShowClockSpeed = true, ShowPower = true, ShowVoltage = true, ShowStorageActivity = true, ShowStorageThroughput = true };
            widget.ApplyOptions(options, ElementTheme.Dark);
            widget.Show();
            for (var i = 0; i < 60; i++) widget.Publish(ChartSample(i));
            var root = Assert.IsType<Grid>(widget.Content);
            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                widget.ApplyOptions(options, theme);
                root.UpdateLayout();
                var bitmap = await RenderAsync(root);
                var charts = Descendants<SystemMonitorMetricChart>(root).ToArray();
                Assert.Equal(7, charts.Length);
                Assert.All(charts, chart =>
                {
                    Assert.True(chart.ActualWidth > 0 && chart.ActualHeight > 0);
                    Assert.Contains(Descendants<Polyline>(chart), line => line.Points.Count > 1);
                });
                await SavePreviewAsync(bitmap, $"hardware-charts-{theme.ToString().ToLowerInvariant()}.png");
            }
            var gpu = Descendants<SystemMonitorMetricChart>(root).Single(chart => chart.Section is MonitorMetricSection { Kind: SystemMonitorMetricKind.GpuUsage });
            var legend = Assert.Single(Descendants<UniformWrapPanel>(gpu));
            var legendRow = legend.Children[0];
            var trace = Descendants<Polyline>(gpu).First();
            var previousPoint = trace.Points.Last();
            widget.Publish(ChartSample(60));
            Assert.Same(legendRow, legend.Children[0]);
            Assert.Same(trace, Descendants<Polyline>(gpu).First());
            Assert.NotEqual(previousPoint, trace.Points.Last());
        }
        finally { widget.CloseFromHost(); }
    }

    private static SystemMonitorSnapshot ChartSample(int index)
    {
        var wave = (1 + Math.Sin(index / 6d)) / 2;
        return new(DateTimeOffset.UnixEpoch.AddSeconds(index), 25, null, null, null, null, [], [], [])
        {
            HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, "GPU Core · NVIDIA GeForce RTX 3070", 30 + 40 * wave),
                new(SystemMonitorMetricKind.GpuUsage, "GPU Memory Controller · NVIDIA GeForce RTX 3070", 5 + 10 * wave),
                new(SystemMonitorMetricKind.GpuMemory, "GPU Memory Used · NVIDIA GeForce RTX 3070", 2 + wave),
                new(SystemMonitorMetricKind.GpuMemory, "GPU Memory Total · NVIDIA GeForce RTX 3070", 8),
                new(SystemMonitorMetricKind.ClockSpeed, "GPU Core · NVIDIA GeForce RTX 3070", 900 + 600 * wave),
                new(SystemMonitorMetricKind.ClockSpeed, "GPU Memory · NVIDIA GeForce RTX 3070", 6001),
                new(SystemMonitorMetricKind.Power, "CPU Package · CPU · Intel Core i9", 10 + 20 * wave),
                new(SystemMonitorMetricKind.Power, "GPU Package · NVIDIA GeForce RTX 3070", 35 + 40 * wave),
                new(SystemMonitorMetricKind.Voltage, "GPU Core · NVIDIA GeForce RTX 3070", 0.6 + 0.3 * wave),
                new(SystemMonitorMetricKind.StorageActivity, "Total Activity · SSD", 10 + 60 * wave),
                new(SystemMonitorMetricKind.StorageThroughput, "Read · SSD", 1048576 * (1 + 10 * wave))]
        };
    }

    /// <summary>Finds rendered production controls without exposing their implementation as a public test API.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    /// <summary>Waits for a real compositor frame before reading pixels from a newly shown or resized window.</summary>
    private static async Task<RenderTargetBitmap> RenderAsync(FrameworkElement root)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendered = null;
        rendered = (_, _) => { CompositionTarget.Rendering -= rendered; frame.TrySetResult(); };
        CompositionTarget.Rendering += rendered;
        root.UpdateLayout();
        await frame.Task;
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        return bitmap;
    }

    /// <summary>Saves the actual WinUI render to the native suite's preview directory.</summary>
    private async Task SavePreviewAsync(RenderTargetBitmap bitmap, string fileName)
    {
        await using var preview = File.Create(Path.Combine(output, fileName));
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, preview.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }

    /// <summary>Uses packaged English labels while isolating preferences and hardware providers.</summary>
    private static ILocalizationService CreateLocalization()
    {
        using var resource = typeof(MonitorTestApplication).Assembly.GetManifestResourceStream("en.json")!;
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(resource)!;
        var localization = new Mock<ILocalizationService>();
        localization.Setup(l => l.GetString(It.IsAny<string>())).Returns((string key) => strings.GetValueOrDefault(key, key));
        return localization.Object;
    }

    /// <summary>Leaves a machine-readable result and ends the isolated native host.</summary>
    private void Finish(string message, bool passed = false)
    {
        File.WriteAllText(Path.Combine(output, "result.txt"), message);
        Console.WriteLine(message);
        Environment.Exit(passed ? 0 : 1);
    }
}
