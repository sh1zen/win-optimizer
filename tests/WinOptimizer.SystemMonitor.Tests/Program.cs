using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using Moq;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.ViewModels;
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
                await CheckNativeScalingAsync();
                await CheckNativeChartsAsync();
                deadline.Stop();
                Finish($"PASS: {count} SysMonitor preference/presentation cases, 8 native scale/layout cases and hardware charts in both themes.", true);
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

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
            for (var i = 0; i < 60; i++)
            {
                var wave = (1 + Math.Sin(i / 6d)) / 2;
                widget.Publish(new(DateTimeOffset.UnixEpoch.AddSeconds(i), 25, null, null, null, null, [], [], [])
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
                });
            }
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
                await using var preview = File.Create(Path.Combine(output, $"hardware-charts-{theme.ToString().ToLowerInvariant()}.png"));
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, preview.AsRandomAccessStream());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth,
                    (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
                await encoder.FlushAsync();
            }
        }
        finally { widget.CloseFromHost(); }
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
