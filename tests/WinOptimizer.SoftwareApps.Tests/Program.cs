using WinOptimizer.UI.Tests.Helpers;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Moq;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Tests.ViewModels;
using WinOptimizer.UI.Tests.Services;
using Xunit;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Converters;
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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        WinUIApplication.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new SortingTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class SortingTestApplication(string resultPath) : WinUIApplication
{
    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        InitializeComponent();
        Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
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
            var count = await NativeTestCases.RunAsync(typeof(AppSortingTests), typeof(WindowsAppsViewModelTests), typeof(ExternalAppsViewModelTests), typeof(LeftoverAppsViewModelTests), typeof(UninstallResidueViewModelTests), typeof(UninstallResidueReviewTests), typeof(SoftwareAppsViewModelTests), typeof(SelectedAppsProviderTests), typeof(ConfigExportServiceTests));
            await CaptureLeftoverPreviewsAsync();
            Finish($"PASS: {count} Software & Apps cases on the WinUI dispatcher; English/Italian Leftover previews at two widths.", true);
        }
        catch (Exception ex) { Finish(ex.ToString()); }
    }

    private void Finish(string message, bool passed = false)
    {
        File.WriteAllText(resultPath, message);
        Console.WriteLine(message);
        Environment.Exit(passed ? 0 : 1);
    }

    /// <summary>Renders the production control with fake scan evidence and actual translated resources.</summary>
    private async Task CaptureLeftoverPreviewsAsync()
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
                var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(content);
                Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
                await using var output = File.Create(Path.Combine(Path.GetDirectoryName(resultPath)!, $"leftover-{language}-{width}.png"));
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
                await encoder.FlushAsync();
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
