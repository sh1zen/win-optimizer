using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Moq;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Customize.Pages;
using WinOptimizer.UI.Features.Customize.Services;
using WinOptimizer.UI.Features.Customize.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;

namespace WinOptimizer.ContextMenuTests;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Pass preview output, fixture icon path and application theme.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new ContextMenuApplication(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Enum.Parse<ApplicationTheme>(args[2]));
        });
    }
}

/// <summary>Renders the production page with fake registry boundaries and verifies native icon loading and toggle layout.</summary>
public sealed partial class ContextMenuApplication : Microsoft.UI.Xaml.Application
{
    private readonly string _outputDirectory;
    private readonly string _iconPath;
    private Window? _window;
    public ContextMenuApplication(string outputDirectory, string iconPath, ApplicationTheme theme)
    {
        _outputDirectory = outputDirectory; _iconPath = iconPath; RequestedTheme = theme;
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        InitializeComponent(); Directory.CreateDirectory(_outputDirectory);
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid(); _window = new Window { Content = shell };
        _window.AppWindow.ResizeClient(new SizeInt32(1240, 900));
        var deadline = _window.DispatcherQueue.CreateTimer(); deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("Context-menu checks timed out."); deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try { await CheckAsync(shell); deadline.Stop(); Finish(); }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    private async Task CheckAsync(Grid shell)
    {
        await CheckContractsAsync();
        using var strings = Assembly.GetExecutingAssembly().GetManifestResourceStream("en.json")!;
        var dictionary = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(strings) ?? throw new InvalidOperationException("Missing fixture localization.");
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => dictionary.GetValueOrDefault(key, key));
        var mode = new Mock<IApplicationModeService>();
        var service = new Mock<IContextMenuService>();
        var entries = new ContextMenuEntry[]
        {
            new("a", "Alpha Editor", "Open with Alpha", @"User\*\shell\Alpha", true) { ProgramPath = @"C:\Fixture\alpha.exe", TargetType = "*" },
            new("b", "Blue Archive", "Compress files", @"Machine (64-bit)\*\shell\Compress", true) { ProgramPath = @"C:\Fixture\archive.exe", TargetType = "*", IsMachineWide = true },
            new("c", "Blue Archive", "Extract into a folder", @"Machine (64-bit)\Directory\shell\Extract", false) { ProgramPath = @"C:\Fixture\archive.exe", TargetType = "Directory", IsMachineWide = true },
            new("d", "Blue Archive", "Open archive here", @"User\Directory\Background\shell\Archive", true) { ProgramPath = @"C:\Fixture\archive.exe", TargetType = @"Directory\Background" },
            new("e", "Long program name to check wrapping on compact windows", "Open project", @"User\Folder\shell\Project", true) { ProgramPath = @"C:\Fixture\project.exe", TargetType = "Folder" },
            new("f", "Program without artwork", "Open file", @"User\*\shell\Fallback", false) { TargetType = "*" },
        };
        service.Setup(service => service.ReadAsync()).ReturnsAsync(entries);
        service.Setup(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), It.IsAny<bool>()))
            .ReturnsAsync((ContextMenuEntry entry, bool enabled) => entry with { IsEnabled = enabled });
        var bitmap = new BitmapImage();
        using (var file = File.OpenRead(_iconPath)) await bitmap.SetSourceAsync(file.AsRandomAccessStream());
        var icons = new Mock<IProgramIconSource>();
        icons.Setup(source => source.LoadAsync(It.IsAny<string>())).ReturnsAsync(bitmap);
        using var vm = new ContextMenuViewModel(service.Object, localization.Object, mode.Object,
            new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>(), icons.Object);
        var page = new ContextMenuCustomizePage(vm) { Margin = new Thickness(24, 0, 24, 0) };
        shell.Children.Add(page);
        while (!vm.Programs.Any() || vm.IsBusy) await Task.Delay(20);
        Require(vm.Programs.Count == 4 && vm.Programs.Count(program => program.HasIcon) == 3, "Program artwork or grouping missing.");
        var archive = vm.Programs.Single(program => program.Name == "Blue Archive");
        while (!Tree<ToggleSwitch>(page).Any(toggle => ReferenceEquals(toggle.Tag, archive) && toggle.IsLoaded)) await Task.Delay(20);
        var programToggle = Tree<ToggleSwitch>(page).Single(toggle => ReferenceEquals(toggle.Tag, archive));
        Require(!archive.IsExpanded && programToggle.IsOn, "A collapsed program must expose its current master-toggle state.");
        programToggle.IsOn = false;
        while (vm.IsBusy) await Task.Delay(20);
        Require(!archive.IsExpanded && archive.EnabledCount == 0 && !programToggle.IsOn, "The collapsed program toggle did not disable every entry.");
        service.Verify(source => source.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), false), Times.Exactly(2));
        programToggle.IsOn = true;
        while (vm.IsBusy) await Task.Delay(20);
        Require(!archive.IsExpanded && archive.EnabledCount == 3 && programToggle.IsOn, "The collapsed program toggle did not enable every entry.");
        service.Verify(source => source.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), true), Times.Exactly(3));
        await vm.SetEnabledAsync(archive.Entries[1], false);
        Require(programToggle.IsOn && archive.EnabledCount == 2, "Individual changes lost the mixed program-toggle state.");
        service.Invocations.Clear();
        var theme = RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        shell.RequestedTheme = theme;
        shell.Background = new SolidColorBrush(theme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        await Task.Delay(350); shell.UpdateLayout();
        await CaptureAsync(shell, $"context-menu-{theme}-collapsed.png");
        archive.IsExpanded = true;
        foreach (var width in new[] { 1240, 480 })
        {
            _window!.AppWindow.ResizeClient(new SizeInt32(width, 900));
            // Allow the native expander transition to finish before measuring and capturing its children.
            await Task.Delay(600); shell.UpdateLayout();
            foreach (var toggle in Tree<ToggleSwitch>(page).Where(control => control.ActualWidth > 0))
            {
                var origin = toggle.TransformToVisual(page).TransformPoint(new Point(0, 0));
                Require(origin.X >= 0 && origin.X + toggle.ActualWidth <= page.ActualWidth + 1, "A toggle exceeds the page width.");
            }
            await CaptureAsync(shell, $"context-menu-{theme}-{width}.png");
        }
        var offEntry = archive.Entries[1];
        var offToggle = Tree<ToggleSwitch>(page).Single(toggle => ReferenceEquals(toggle.Tag, offEntry));
        offToggle.IsOn = true;
        while (vm.IsBusy) await Task.Delay(20);
        Require(archive.EnabledCount == 3, "Verified toggle did not update the enabled count.");
        service.Verify(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), true), Times.Once);
        vm.Query = "Extract into";
        Require(ReferenceEquals(archive, vm.Programs.Single()) && archive.IsExpanded, "Entry search lost its program or expansion.");
        vm.Query = "";
        mode.SetupGet(service => service.CurrentMode).Returns(WinOptimizerMode.Builder);
        mode.Raise(service => service.ModeChanged += null, EventArgs.Empty);
        Require(Tree<ToggleSwitch>(page).Where(control => control.ActualWidth > 0).All(control => !control.IsEnabled), "Test mode did not disable toggles.");
        Require(Tree<Expander>(page).Any(control => control.IsEnabled && control.IsExpanded), "Read-only inspection cannot expand a program.");
        var dispatcher = new DispatcherService(); dispatcher.Initialize(_window!.DispatcherQueue);
        var nativeIcons = new ProgramIconSource(dispatcher, Mock.Of<ILogService>());
        var invalidIconPath = Path.Combine(_outputDirectory, "invalid\0program.exe");
        var unavailableIcons = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => nativeIcons.LoadAsync(invalidIconPath)));
        Require(unavailableIcons.All(icon => icon == null), "Unsupported icon paths must use fallback artwork.");
        Require(await nativeIcons.LoadAsync(Environment.ProcessPath!) != null, "Native local program icon extraction failed.");
        Require(await nativeIcons.LoadAsync(@"\\invalid-network\program.exe") == null, "Network icon path was not excluded.");
        var iconEntries = new[]
        {
            entries[0] with { ProgramPath = invalidIconPath },
            entries[1] with { ProgramPath = Environment.ProcessPath }
        };
        var iconService = new Mock<IContextMenuService>();
        iconService.Setup(source => source.ReadAsync()).ReturnsAsync(iconEntries);
        using var iconVm = new ContextMenuViewModel(iconService.Object, localization.Object, mode.Object,
            new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>(), nativeIcons);
        await iconVm.RefreshAsync();
        Require(!iconVm.IsError && !iconVm.HasStatus && iconVm.Programs.Count == 2, "An unavailable icon failed context-menu discovery.");
        Require(iconVm.Programs[0].ShowFallbackIcon && iconVm.Programs[1].HasIcon, "One unavailable icon prevented other program artwork from loading.");
        Console.WriteLine("PASS: native icons, fallback artwork, wide/narrow themes, toggle bounds, live counts, search and read-only expansion.");
    }

    /// <summary>Runs the shared context-menu contracts against fake registry and mode boundaries.</summary>
    private static async Task CheckContractsAsync()
    {
        var count = 0;
        foreach (var method in typeof(ContextMenuViewModelTests).GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            var data = method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray();
            foreach (var row in data.Length == 0 ? new[] { Array.Empty<object>() } : data)
            {
                try { if (method.Invoke(new ContextMenuViewModelTests(), row) is Task task) await task; count++; }
                catch (TargetInvocationException ex) { throw new InvalidOperationException(method.Name, ex.InnerException); }
            }
        }
        Console.WriteLine($"PASS: {count} context-menu view-model cases.");
    }

    private static IEnumerable<T> Tree<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Tree<T>(child)) yield return nested;
        }
    }

    private async Task CaptureAsync(UIElement content, string name)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(content);
        await using var output = File.Create(Path.Combine(_outputDirectory, name));
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Finish(string? error = null)
    {
        File.WriteAllText(Path.Combine(_outputDirectory, $"result-{RequestedTheme}.log"), error ?? "All context-menu presentation checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
