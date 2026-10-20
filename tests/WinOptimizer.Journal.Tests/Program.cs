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
using WinOptimizer.Application.Journal;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Settings;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.JournalTests;

/// <summary>Runs the production journal UI and merger against synthetic storage, without reading or clearing real logs.</summary>
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
            _ = new JournalTestApplication(Path.GetFullPath(args[0]));
        });
    }
}

public sealed partial class JournalTestApplication(string outputDirectory) : WinUIApplication
{
    private Window? _window;
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(outputDirectory); InitializeComponent();
        UnhandledException += (_, error) => Finish(error.Exception.ToString());
        var shell = new Grid { Padding = new Thickness(24, 12, 24, 20) };
        _window = new Window { Content = shell };
        var deadline = _window.DispatcherQueue.CreateTimer(); deadline.Interval = TimeSpan.FromSeconds(120);
        deadline.Tick += (_, _) => Finish("Journal presentation timed out."); deadline.Start();
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
        foreach (var method in typeof(LogsAndHistoryViewModelTests).GetMethods().Where(method => method.IsDefined(typeof(FactAttribute))))
        {
            var data = method.GetCustomAttributes<InlineDataAttribute>().SelectMany(attribute => attribute.GetData(method)).ToArray();
            foreach (var row in data.Length == 0 ? new[] { Array.Empty<object>() } : data)
            {
                try { if (method.Invoke(new LogsAndHistoryViewModelTests(), row) is Task task) await task; count++; }
                catch (TargetInvocationException ex) { throw new InvalidOperationException(method.Name, ex.InnerException); }
            }
        }
        Console.WriteLine($"PASS: {count} production journal view-model cases.");
    }

    private async Task RenderAsync(Grid shell)
    {
        foreach (var language in new[] { "en", "it" })
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (var width in new[] { 1000, 720, 420 })
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json") ?? throw new IOException("Missing test localization.");
            var strings = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(resource) ?? throw new IOException("Missing strings.");
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            localization.Setup(service => service.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
                .Returns((string key, object[] values) => string.Format(strings[key], values));
            var now = DateTimeOffset.UnixEpoch.AddYears(56).AddHours(12);
            List<AppLogEntry> events =
            [
                new() { Timestamp = now, Level = LogLevel.Error, Topic = LogTopic.Updates, Message = "Could not download the application update: disk is full.",
                    Exception = new("IOException", "Not enough disk space", "UpdateService.DownloadAsync()\nVersionService.CheckAsync()", "Storage operation failed") },
                new() { Timestamp = now.AddMinutes(-1), Level = LogLevel.Info, Topic = LogTopic.Software, Message = "Application inventory completed: 28 installed apps." },
                new() { Timestamp = now.AddMinutes(-2), Level = LogLevel.Info, Topic = LogTopic.SystemInformation, Message = "OS: Windows 11 · Architecture: x64" },
                new() { Timestamp = now.AddMinutes(-2), Level = LogLevel.Info, Topic = LogTopic.SystemInformation, Message = "CPU: Example processor · RAM: 32 GB" },
                new() { Timestamp = now.AddMinutes(-3), Level = LogLevel.Info, Topic = LogTopic.Startup, Message = "Application initialization completed." }
            ];
            List<ChangeHistoryEntry> receipts =
            [
                new() { Timestamp = now.AddSeconds(-10), Kind = HistoryEntryKind.AppRemoved, Name = "Example application" },
                new() { Timestamp = now.AddMinutes(-1), Kind = HistoryEntryKind.SettingChange, Name = "Telemetry", Group = "Privacy", Before = "Enabled", After = "Disabled", Batch = "Configuration import", BatchId = Guid.Parse("6f30c92f-3173-42c4-91a3-bbd890b8409c") }
            ];
            var logs = new Mock<ILogService>(); var history = new Mock<IChangeHistoryService>();
            logs.Setup(service => service.GetFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new LogFileInfo("session.jsonl", now) });
            logs.Setup(service => service.ReadRecentAsync("session.jsonl", null, 32, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new LogPage<AppLogEntry>(events.ToArray(), 0, false));
            history.Setup(service => service.GetFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new LogFileInfo("ChangeHistory.jsonl", now) });
            history.Setup(service => service.ReadRecentAsync("ChangeHistory.jsonl", null, 32, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new LogPage<ChangeHistoryEntry>(receipts.ToArray(), 0, false));
            logs.Setup(service => service.ClearAsync(It.IsAny<CancellationToken>())).Callback(events.Clear).Returns(Task.CompletedTask);
            var dialogs = new Mock<IDialogService>();
            dialogs.Setup(service => service.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
            using var vm = new LogsAndHistoryViewModel(new ActivityJournalService(logs.Object, history.Object), logs.Object, localization.Object, dialogs.Object);
            shell.RequestedTheme = theme;
            shell.Background = new SolidColorBrush(theme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
            var control = new LogsAndHistoryControl { ViewModel = vm, RequestedTheme = theme };
            shell.Children.Clear(); shell.RowDefinitions.Clear();
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); shell.RowDefinitions.Add(new RowDefinition());
            var heading = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 18) };
            heading.Children.Add(new TextBlock { Text = strings["Settings_LogsAndHistory"], FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = strings["Settings_LogsAndHistoryDescription"], TextWrapping = TextWrapping.Wrap });
            shell.Children.Add(heading); Grid.SetRow(control, 1); shell.Children.Add(control); shell.RequestedTheme = theme;
            _window!.AppWindow.ResizeClient(new SizeInt32(width, 800));
            await vm.RefreshCommand.ExecuteAsync(null); await Task.Delay(350); shell.UpdateLayout();
            Assert.Equal(5, vm.Groups.Count);
            foreach (var child in new[] { "JournalSearch", "TopicFilter", "KindFilter", "ClearLogsButton", "TopicGroups" })
            {
                var element = (FrameworkElement)control.FindName(child);
                var point = element.TransformToVisual(shell).TransformPoint(default);
                Assert.True(element.ActualWidth > 0 && point.X >= -1 && point.X + element.ActualWidth <= shell.ActualWidth + 1, $"{child} exceeds width {width}.");
            }
            await CaptureAsync(shell, $"journal-{language}-{theme}-{width}.png");
            if (width != 1000) continue;
            var settings = vm.Groups.Single(group => group.Topic == LogTopic.Settings);
            settings.IsExpanded = true;
            ((ListView)control.FindName("TopicGroups")).ScrollIntoView(settings);
            await Task.Delay(200); await CaptureAsync(shell, $"journal-changes-{language}-{theme}.png");
            settings.IsExpanded = false;
            vm.SearchText = "disk"; await vm.PendingRead;
            Assert.Single(vm.Entries); Assert.Equal(LogTopic.Updates, vm.Entries[0].Record.Topic);
            vm.SearchText = ""; await vm.PendingRead;
            ((ComboBox)control.FindName("KindFilter")).SelectedIndex = (int)JournalFilter.Problems; await vm.PendingRead;
            Assert.Single(vm.Entries); Assert.True(vm.Entries[0].Record.IsProblem);
            await Task.Delay(200); await CaptureAsync(shell, $"journal-problems-{language}-{theme}.png");
            ((ComboBox)control.FindName("KindFilter")).SelectedIndex = (int)JournalFilter.All; await vm.PendingRead;
            var button = (Button)control.FindName("ClearLogsButton");
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(200);
            await (vm.ClearAppLogsCommand.ExecutionTask ?? throw new InvalidOperationException("Native clear action did not start."));
            Assert.All(vm.Entries, entry => Assert.NotNull(entry.Record.Change)); Assert.Equal(2, vm.Entries.Count);
            logs.Verify(service => service.ClearAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        Console.WriteLine("PASS: real grouped control, search, problem filters, native clear action, EN/IT, both themes and three widths. Synthetic storage only.");
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
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All journal checks passed.");
        if (error is not null) Console.Error.WriteLine(error);
        Environment.Exit(error is null ? 0 : 1);
    }
}
