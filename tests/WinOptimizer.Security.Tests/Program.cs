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
using WinOptimizer.Core.Features.Security;
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

    /// <summary>Checks retained result bindings at compact/wide widths and captures the actual WinUI page.</summary>
    private async Task RenderAsync(Grid shell)
    {
        foreach (var language in new[] { "en", "it" })
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = (await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(resource))!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(localization => localization.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            var backend = new Mock<ISecurityBackend>();
            backend.Setup(backend => backend.ReadAsync(It.IsAny<IReadOnlyList<SecurityCheckId>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<SecurityCheckId> ids, CancellationToken _) => ids.Select(id =>
                    new SecurityObservation(id, id == SecurityCheckId.Updates ? SecurityCheckState.Unknown :
                        id is SecurityCheckId.Antivirus or SecurityCheckId.Firewall or SecurityCheckId.Spyware or SecurityCheckId.Uac ? SecurityCheckState.Good : SecurityCheckState.Attention,
                        id is SecurityCheckId.Isatap or SecurityCheckId.Teredo ? "Automatic" : "Enabled", "Synthetic test evidence", id >= SecurityCheckId.Isatap)).ToArray());
            var tasks = new Mock<ITaskProgressService>(); CancellationTokenSource? current = null;
            tasks.SetupGet(tasks => tasks.CurrentTaskCancellationSource).Returns(() => current);
            tasks.Setup(tasks => tasks.StartTask(It.IsAny<string>(), true, true, true)).Returns(() => current = new());
            tasks.Setup(tasks => tasks.CompleteTask()).Callback(() => { current?.Dispose(); current = null; });
            var mode = new Mock<IApplicationModeService>();
            using var vm = new SecurityViewModel(new SecurityScanService(backend.Object, mode.Object), Mock.Of<IDialogService>(), localization.Object,
                mode.Object, new ExtensionTestDispatcher(), tasks.Object);
            var page = new SecurityPage(vm); shell.Children.Clear(); shell.Children.Add(page);
            _window!.AppWindow.ResizeClient(new SizeInt32(1100, 900)); await Task.Delay(120);
            Assert.False(vm.HasReport);
            var actions = (Panel)page.FindName("ScanActions");
            var buttons = actions.Children.OfType<Button>().ToArray();
            Assert.Equal(new[] { vm.ScanLabel, vm.DeepScanLabel }, buttons.Select(button => (string)button.Content));
            Assert.Same(vm.ScanCommand, buttons[0].Command);
            Assert.Same(vm.DeepScanCommand, buttons[1].Command);
            await CaptureAsync(shell, $"security-{language}-intro.png");
            await vm.DeepScanCommand.ExecuteAsync(null);
            Assert.Equal(5, vm.Categories.Count); Assert.Equal(6, vm.Findings.Count);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var width in new[] { 480, 1100, 1500 })
            {
                shell.RequestedTheme = theme; shell.Background = new SolidColorBrush(theme == ElementTheme.Light ? Colors.White : Colors.Black);
                _window.AppWindow.ResizeClient(new SizeInt32(width, 900)); await Task.Delay(100); shell.UpdateLayout();
                var picker = (ComboBox)page.FindName("CategoryPicker");
                Assert.Equal(width < 900 ? Visibility.Visible : Visibility.Collapsed, picker.Visibility);
                var list = (ListView)page.FindName("FindingsList");
                Assert.Equal(6, list.Items.Count); Assert.True(list.ActualWidth > 200);
                var origin = list.TransformToVisual(shell).TransformPoint(default);
                Assert.True(origin.X >= 0 && origin.X + list.ActualWidth <= shell.ActualWidth + 1);
                Assert.True(list.ActualHeight > 150 && origin.Y + list.ActualHeight <= shell.ActualHeight + 1);
                var content = FindNamed(list, "FindingContent");
                Assert.NotNull(content);
                Assert.Equal(width == 480 ? 3 : 1, Grid.GetColumnSpan(content));
                await CaptureAsync(shell, $"security-{language}-{theme}-{width}.png");
            }
        }
        Console.WriteLine("PASS: English/Italian production Security page, three widths and light/dark themes.");
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
