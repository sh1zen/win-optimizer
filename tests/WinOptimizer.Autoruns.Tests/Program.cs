using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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

namespace WinOptimizer.AutorunsTests;

/// <summary>Runs real startup UI with fake operating-system boundaries and synthetic registrations.</summary>
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
            _ = new AutorunsTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class AutorunsTestApplication(string outputDirectory) : WinUIApplication
{
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(outputDirectory); InitializeComponent();
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid { Padding = new Thickness(24, 0, 24, 0) }; _window = new Window { Content = shell };
        var deadline = _window.DispatcherQueue.CreateTimer(); deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("Autoruns presentation timed out."); deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try { await ContractsAsync(); await RenderAsync(shell); Finish(); }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    /// <summary>Runs the production view-model cases in the real WinUI runtime.</summary>
    private static async Task ContractsAsync()
    {
        var passed = 0;
        foreach (var method in typeof(AutorunsViewModelTests).GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            var data = method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray();
            foreach (var row in data.Length == 0 ? new[] { Array.Empty<object>() } : data)
            {
                using var tests = new AutorunsViewModelTests();
                try { if (method.Invoke(tests, row) is Task task) await task; passed++; }
                catch (TargetInvocationException ex) { throw new InvalidOperationException(method.Name, ex.InnerException); }
            }
        }
        Console.WriteLine($"PASS: {passed} production autorun view-model cases.");
    }

    /// <summary>Checks real translated controls, normal/advanced views, filters and adaptive selection details.</summary>
    private async Task RenderAsync(Grid shell)
    {
        foreach (var language in new[] { "en", "it" })
        {
            using var json = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            var service = new Mock<IAutorunService>(MockBehavior.Strict);
            service.Setup(source => source.ReadAsync(It.IsAny<AutorunView>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AutorunView view, bool _, CancellationToken _) => new AutorunInventory(view, new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero),
                    view == AutorunView.Startup ? StartupEntries : StartupEntries.Concat(AdvancedEntries).ToArray(), [], "Synthetic accounts and loading points only"));
            using var vm = new AutorunsViewModel(service.Object, Mock.Of<IDialogService>(), localization.Object,
                Mock.Of<IApplicationModeService>(), new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());
            var page = new AutorunsPage(vm); var loaded = new TaskCompletionSource(); page.Loaded += (_, _) => loaded.TrySetResult();
            shell.Children.Clear(); shell.Children.Add(page); await loaded.Task; await vm.LoadAsync();
            service.Verify(source => source.ReadAsync(AutorunView.Startup, false, CancellationToken.None), Times.Once);
            foreach (var advanced in new[] { false, true })
            {
                ((ToggleSwitch)page.FindName("AdvancedToggle")).IsOn = advanced;
                if (advanced)
                {
                    Assert.True(vm.Advanced); Assert.Empty(vm.Rows); Assert.False(vm.RefreshCommand.CanExecute(null));
                    ((CheckBox)page.FindName("LicenseConsent")).IsChecked = true;
                    await vm.RefreshCommand.ExecutionTask!;
                }
                vm.SelectedRow = vm.Rows.First();
                Assert.Equal(!advanced, vm.CanDisable);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                foreach (var width in new[] { 760, 1100, 1500 })
                {
                    shell.RequestedTheme = theme; shell.Background = new SolidColorBrush(theme == ElementTheme.Light ? Colors.White : Colors.Black);
                    _window!.AppWindow.ResizeClient(new SizeInt32(width, 1050)); await Task.Delay(120); shell.UpdateLayout();
                    var list = (ListView)page.FindName("EntriesList"); var details = (Border)page.FindName("DetailsPanel");
                    var grouped = (CollectionViewSource)page.FindName("GroupedEntries");
                    Assert.Equal(vm.Groups.Count, grouped.View.CollectionGroups.Count);
                    Assert.Equal(vm.Rows.Count, list.Items.Count);
                    Assert.Same(vm.SelectedRow, list.SelectedItem);
                    Assert.Equal(advanced ? 3 : 2, vm.Groups.Count);
                    Assert.True(list.ActualHeight >= 140, "The inventory must retain usable scrolling height.");
                    var search = (TextBox)page.FindName("SearchBox");
                    var clear = (Button)page.FindName("ClearFiltersButton");
                    var category = (ComboBox)page.FindName("CategoryFilter");
                    var state = (ComboBox)page.FindName("StateFilter");
                    var microsoft = (CheckBox)page.FindName("MicrosoftFilter");
                    foreach (var control in new FrameworkElement[] { list, details, (Button)page.FindName("ScanButton"), search, clear, category, state }
                        .Concat(advanced ? new FrameworkElement[] { microsoft } : []))
                    {
                        var point = control.TransformToVisual(shell).TransformPoint(default);
                        Assert.True(control.ActualWidth > 0 && point.X >= 0 && point.X + control.ActualWidth <= shell.ActualWidth + 1, "A control exceeds the client width.");
                    }
                    var searchPosition = search.TransformToVisual(shell).TransformPoint(default);
                    var clearPosition = clear.TransformToVisual(shell).TransformPoint(default);
                    var categoryPosition = category.TransformToVisual(shell).TransformPoint(default);
                    var statePosition = state.TransformToVisual(shell).TransformPoint(default);
                    Assert.True(search.ActualWidth >= 120, "Search must retain a usable input width.");
                    Assert.True(searchPosition.X + search.ActualWidth <= clearPosition.X);
                    Assert.True(clearPosition.X + clear.ActualWidth <= categoryPosition.X);
                    Assert.True(categoryPosition.X + category.ActualWidth <= statePosition.X);
                    Assert.True(Math.Abs(searchPosition.Y - categoryPosition.Y) <= 1 && Math.Abs(searchPosition.Y - statePosition.Y) <= 1,
                        "Search must be on the left and category/state filters on the right of the same row.");
                    if (advanced)
                    {
                        Assert.Equal(width < 1050 ? 1 : 0, Grid.GetRow(microsoft));
                        if (width >= 1050) Assert.True(statePosition.X + state.ActualWidth <= microsoft.TransformToVisual(shell).TransformPoint(default).X);
                    }
                    Assert.Equal(width < 1050 ? 1 : 0, Grid.GetRow(details));
                    await CaptureAsync(shell, $"autoruns-{language}-{theme}-{width}-{(advanced ? "advanced" : "startup")}.png");
                }
                _window!.AppWindow.ResizeClient(new SizeInt32(760, 600)); await Task.Delay(120); shell.UpdateLayout();
                Assert.True(((ListView)page.FindName("EntriesList")).ActualHeight >= 140);
                var scroller = (ScrollViewer)page.FindName("PageScroll"); Assert.True(scroller.ScrollableHeight > 0);
                scroller.ChangeView(null, scroller.ScrollableHeight, null, true); await Task.Delay(80);
                await CaptureAsync(shell, $"autoruns-{language}-short-{(advanced ? "advanced" : "startup")}.png");
                scroller.ChangeView(null, 0, null, true);
            }
            vm.SearchText = "not a registration"; Assert.Empty(vm.Rows); vm.ClearFiltersCommand.Execute(null); Assert.NotEmpty(vm.Rows);
            service.Verify(source => source.ReadAsync(AutorunView.Advanced, true, CancellationToken.None), Times.Once);
            ((ToggleSwitch)page.FindName("AdvancedToggle")).IsOn = false; await vm.RefreshCommand.ExecutionTask!;
            Assert.Equal(StartupEntries.Length, vm.Rows.Count); Assert.Null(vm.SelectedRow);
            ((ToggleSwitch)page.FindName("AdvancedToggle")).IsOn = true; await vm.RefreshCommand.ExecutionTask!;
            Assert.Equal(StartupEntries.Length + AdvancedEntries.Length, vm.Rows.Count);
            service.Verify(source => source.ReadAsync(AutorunView.Advanced, true, CancellationToken.None), Times.Exactly(2));
            shell.Children.Clear();
        }
        Console.WriteLine("PASS: native normal/advanced pages, EN/IT, light/dark, three widths, search/filter toolbar, automatic mode/consent loading and details. No native scan, download or startup change.");
    }

    private static readonly AutorunEntry[] StartupEntries =
    [
        new("run:example", "Example sync", "Logon", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"C:\Program Files\Example\Sync.exe --background", @"C:\Program Files\Example\Sync.exe", "Example Software", "", "Current user", true, true, "Original registry type: REG_EXPAND_SZ"),
        new("file:example", "Example audio.lnk", "Startup folders", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup", @"C:\Program Files\Example Audio\Audio.exe", "Audio.exe", "Example Audio", "", "All users", false, true, "Startup file preserved for re-enabling."),
        new("app:example", "Example packaged app", "Windows apps", "Example.Package_1234", "PackagedApp.exe", "PackagedApp.exe", "Example Software", "", "Current user", null, false, "Manage this packaged startup task in Windows."),
        new("task:example", "Example updater", "Scheduled tasks", @"\Example\Updater", "Updater.exe --scheduled", "Updater.exe", "Example Software", "", "Current user", true, true, "Boot/logon task. Changing its state affects all triggers.")
    ];
    private static readonly AutorunEntry[] AdvancedEntries =
    [
        new("explorer:example", "Example context menu", "Explorer", @"HKLM\Software\Classes\*\shellex\ContextMenuHandlers", "Context.dll", "Context.dll", "Example Software", "(Verified) Example Software", "System", true, false, "Explorer extension"),
        new("service:example", "Example service", "Services", @"HKLM\SYSTEM\CurrentControlSet\Services\Example", "Service.exe", "Service.exe", "Example Software", "(Verified) Example Software", "System", true, false, "Automatic service"),
        new("driver:example", "Example driver", "Drivers", @"HKLM\SYSTEM\CurrentControlSet\Services\ExampleDriver", "Driver.sys", "Driver.sys", "Microsoft Corporation", "(Verified) Microsoft Windows", "System", true, false, "Kernel driver; inspect only")
    ];

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
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All autorun checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
