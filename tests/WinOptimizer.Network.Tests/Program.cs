using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
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
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Optimize.Pages;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.NetworkTests;

/// <summary>Runs production Optimize contracts and renders the Network page with fake Windows services only.</summary>
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
            _ = new NetworkTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class NetworkTestApplication(string outputDirectory) : WinUIApplication
{
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(outputDirectory);
        InitializeComponent();
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
            Padding = new Thickness(16, 0, 16, 0)
        };
        _window = new Window { Content = shell };
        var timeout = _window.DispatcherQueue.CreateTimer(); timeout.Interval = TimeSpan.FromSeconds(60);
        timeout.Tick += (_, _) => Finish("Network presentation timed out."); timeout.Start();
        shell.Loaded += async (_, _) =>
        {
            try { await ContractsAsync(); await RenderAsync(shell); Finish(); }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    /// <summary>Executes the main test project's real view-model cases inside the native WinUI runtime.</summary>
    private static async Task ContractsAsync()
    {
        var passed = 0;
        foreach (var method in typeof(OptimizationToolsViewModelTests).GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            var rows = method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray();
            foreach (var row in rows.Length == 0 ? new[] { Array.Empty<object>() } : rows)
            {
                try { if (method.Invoke(new OptimizationToolsViewModelTests(), row) is Task task) await task; passed++; }
                catch (TargetInvocationException ex) { throw new InvalidOperationException(method.Name, ex.InnerException); }
            }
        }
        Console.WriteLine($"PASS: {passed} production Optimize view-model and layout cases.");
    }

    /// <summary>Exercises native controls, cancellation, all 25 cards, and wide/narrow English and Italian layouts.</summary>
    private async Task RenderAsync(Grid shell)
    {
        foreach (var language in new[] { "en", "it" })
        {
            using var json = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            IReadOnlyList<NetworkTuningState> states = NetworkTuningCatalog.All.Select(item => new NetworkTuningState(item.Id,
                item.Input == NetworkTuningInput.Toggle ? "Disabled" : item.Input == NetworkTuningInput.Selection ? "Normal"
                : item.Owner == NetworkTuningOwner.NameResolution ? null : item.Property == "InitialRto" ? "1000" : item.Minimum.ToString(), true)).ToArray();
            var service = new Mock<IOptimizationToolsService>();
            service.Setup(source => source.ReadNetworkTuningsAsync()).ReturnsAsync(() => states);
            service.Setup(source => source.SetNetworkTuningAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((string id, string? value, string? _) => states = states.Select(state => state.Id == id ? state with { Value = value } : state).ToArray());
            var dialogs = new Mock<IDialogService>();
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
            using var vm = new NetworkOptimizationViewModel(service.Object, dialogs.Object, localization.Object, Mock.Of<IApplicationModeService>(), new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());
            var page = new NetworkOptimizePage(vm);
            var loaded = new TaskCompletionSource(); page.Loaded += (_, _) => loaded.TrySetResult();
            shell.Children.Add(page); await loaded.Task; await Task.Delay(80); shell.UpdateLayout();
            Assert.Equal(25, vm.Choices.Count);
            Assert.Equal(12, Descendants<ToggleSwitch>(page).Count(control => control.Visibility == Visibility.Visible));
            Assert.Equal(12, Descendants<NumberBox>(page).Count(control => control.Visibility == Visibility.Visible));
            Assert.Single(Descendants<ComboBox>(page), control => control.Visibility == Visibility.Visible);
            service.Verify(source => source.SetNetworkTuningAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
            var toggle = Descendants<ToggleSwitch>(page).Single(control => control.Visibility == Visibility.Visible && control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == "tcp-ecn");
            ((IToggleProvider)new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
            await Task.Delay(40);
            Assert.Equal("Enabled", vm.Choices.Single(choice => choice.Definition.Id == "tcp-ecn").State.Value);
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
            ((IToggleProvider)new ToggleSwitchAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
            await Task.Delay(40); Assert.True(toggle.IsOn);
            service.Verify(source => source.SetNetworkTuningAsync("tcp-ecn", "Enabled", "Disabled"), Times.Once);
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
            var local = Descendants<NumberBox>(page).Single(control => control.Visibility == Visibility.Visible && control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == "priority-local");
            local.Value = 4; await Task.Delay(40);
            Assert.Equal("4", vm.Choices.Single(choice => choice.Definition.Id == "priority-local").State.Value);
            var clear = Descendants<Button>(page).Single(control => control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == "priority-local" && control.Content as string == choice.ResetLabel);
            ((IInvokeProvider)new ButtonAutomationPeer(clear).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(40);
            Assert.Null(vm.Choices.Single(choice => choice.Definition.Id == "priority-local").State.Value);
            Assert.True(double.IsNaN(local.Value));
            local.Value = 4; await Task.Delay(40);
            foreach (var (id, value) in new[] { ("priority-hosts", 5d), ("priority-dns", 6d), ("priority-netbt", 7d) })
            {
                var number = Descendants<NumberBox>(page).Single(control => control.Visibility == Visibility.Visible && control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == id);
                number.Value = value; await Task.Delay(40);
                Assert.Equal(value.ToString(), vm.Choices.Single(choice => choice.Definition.Id == id).State.Value);
            }
            var selector = Descendants<ComboBox>(page).Single(control => control.Visibility == Visibility.Visible);
            selector.SelectedIndex = 2; await Task.Delay(40);
            Assert.Equal("Restricted", vm.Choices.Single(choice => choice.Definition.Id == "tcp-auto-tuning").State.Value);
            var scroll = Descendants<ScrollViewer>(page).First();
            foreach (var width in new[] { 620, 1300 })
            {
                _window!.AppWindow.ResizeClient(new SizeInt32(width, 900)); await Task.Delay(80); shell.UpdateLayout();
                scroll.ChangeView(null, 0, null, true); await Task.Delay(40);
                await CaptureAsync(shell, $"network-{language}-{width}-top.png");
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Task.Delay(40);
                await CaptureAsync(shell, $"network-{language}-{width}-priorities.png");
                Assert.True(local.ActualWidth > 0);
                var bounds = local.TransformToVisual(shell).TransformPoint(default);
                Assert.True(bounds.X > shell.ActualWidth / 2 && bounds.X + local.ActualWidth <= shell.ActualWidth + 1, "Priority editor is not within the right-hand column.");
            }
            shell.Children.Clear();
        }
        Console.WriteLine("PASS: 25 native cards, toggle/selection/number edits, confirmation cancellation, and English/Italian previews at two widths.");
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
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All Network checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
