using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Moq;
using Windows.Graphics;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Optimize.Pages;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using WinOptimizer.UI.Tests.ViewModels;
using Xunit;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace WinOptimizer.NetworkTests;

/// <summary>Runs Network and Connection contracts and renders the Network page with fake Windows services.</summary>
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
        var connection = await NativeTestCases.RunAsync(typeof(ConnectionViewModelTests));
        Console.WriteLine($"PASS: {connection} Connection view-model cases.");
        var optimize = await NativeTestCases.RunAsync(typeof(OptimizationToolsViewModelTests));
        Console.WriteLine($"PASS: {optimize} production Optimize view-model and layout cases.");
    }

    /// <summary>Exercises speed results, native network controls, and responsive English and Italian layouts.</summary>
    private async Task RenderAsync(Grid shell)
    {
        foreach (var language in new[] { "en", "it" })
        {
            using var json = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
            IReadOnlyList<NetworkTuningState> states = NetworkTuningCatalog.All.Select(item => new NetworkTuningState(item.Id,
                item.Id == "tcp-timestamps" ? "Allowed" : item.Id == "offload-rss" ? "Enabled"
                : item.Input == NetworkTuningInput.Toggle ? "Disabled" : item.Input == NetworkTuningInput.Selection ? "Normal"
                : item.HasRegistryOverride ? null : item.Property == "InitialRto" ? "1000" : item.Minimum.ToString(), true))
                .Select(state => state with { Original = new NetworkTuningValue(state.Value) }).ToArray();
            var service = new Mock<IOptimizationToolsService>();
            service.Setup(source => source.ReadNetworkTuningsAsync()).ReturnsAsync(() => states);
            service.Setup(source => source.SetNetworkTuningAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((string id, string? value, string? _) => states = states.Select(state => state.Id == id ? state with { Value = value } : state).ToArray());
            var dialogs = new Mock<IDialogService>();
            dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
            var speedTest = new Mock<IInternetSpeedTest>();
            speedTest.SetupSequence(source => source.MeasureAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InternetSpeedTestResult(122.5, 22.5, 12)).ReturnsAsync(new InternetSpeedTestResult(144.5, 23.5, 11));
            var connection = new Mock<IConnectionService>();
            connection.Setup(source => source.ReadAsync()).ReturnsAsync(new ConnectionSnapshot(DateTimeOffset.Now, [],
                [new("Work VPN", "vpn.example.test", "Connected", false, true)], ""));
            connection.Setup(source => source.OpenVpnSettingsAsync()).Returns(Task.CompletedTask);
            var settings = new Mock<ICompatibleSettingsRegistry>();
            settings.SetupGet(source => source.IsInitialized).Returns(true);
            settings.Setup(source => source.GetById("gaming-dns-server")).Returns(new SettingDefinition
            {
                Id = "gaming-dns-server", Name = "DNS", Description = "DNS", ComboBox = new()
                {
                    Options = [new() { DisplayName = "Setting_gaming-dns-server_Option_0" },
                        new() { DisplayName = "Setting_gaming-dns-server_Option_1" }]
                }
            });
            var application = new Mock<ISettingApplicationService>();
            application.Setup(source => source.ApplySettingAsync(It.IsAny<ApplySettingRequest>())).ReturnsAsync(OperationResult.Succeeded());
            localization.SetupGet(source => source.CurrentLanguage).Returns(language);
            using var vm = new NetworkOptimizationViewModel(service.Object, speedTest.Object, dialogs.Object,
                connection.Object, settings.Object, application.Object, localization.Object,
                Mock.Of<IApplicationModeService>(), new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());
            var page = new NetworkOptimizePage(vm);
            var loaded = new TaskCompletionSource(); page.Loaded += (_, _) => loaded.TrySetResult();
            shell.Children.Add(page); await loaded.Task; await Task.Delay(80); shell.UpdateLayout();
            Assert.Equal(26, vm.Choices.Count);
            Assert.False(vm.IsError);
            Assert.Equal(11, Descendants<ToggleSwitch>(page).Count(control => control.Visibility == Visibility.Visible));
            Assert.Equal(13, Descendants<NumberBox>(page).Count(control => control.Visibility == Visibility.Visible));
            var selectors = Descendants<ComboBox>(page)
                .Where(control => control.Visibility == Visibility.Visible && control.DataContext is NetworkTuningChoice).ToArray();
            Assert.Equal(2, selectors.Length);
            var timestamps = selectors.Single(control => control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == "tcp-timestamps");
            Assert.True(timestamps.IsEnabled);
            var allowed = Assert.IsType<NetworkTuningOption>(timestamps.SelectedItem);
            Assert.Equal("Allowed", allowed.Value);
            Assert.Equal(language == "it" ? "Consentiti" : "Allowed", allowed.Label);
            var hopLimit = vm.Choices.Single(choice => choice.Definition.Id == "ipv4-hop-limit");
            Assert.Equal(language == "it" ? "Valore originale: 1" : "Original: 1", hopLimit.OriginalLabel);
            Assert.Equal(language == "it" ? "Valore suggerito: 128" : "Suggested: 128", hopLimit.SuggestedLabel);
            service.Verify(source => source.SetNetworkTuningAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
            speedTest.VerifyNoOtherCalls();
            var dns = Descendants<ComboBox>(page).Single(control => AutomationProperties.GetName(control) == vm.DnsTitle);
            Assert.Equal(strings["Setting_gaming-dns-server_Option_0"], dns.Items[0]);
            var applyDns = Descendants<Button>(page).Single(control => Equals(control.Content, vm.DnsApplyLabel));
            Assert.False(applyDns.IsEnabled);
            dns.SelectedIndex = 1;
            Assert.True(applyDns.IsEnabled);
            ((IInvokeProvider)new ButtonAutomationPeer(applyDns).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(40);
            application.Verify(source => source.ApplySettingAsync(It.Is<ApplySettingRequest>(request =>
                request.SettingId == "gaming-dns-server" && request.Enable && Equals(request.Value, 1))), Times.Once);
            shell.UpdateLayout();
            Assert.Contains(Descendants<TextBlock>(page), value => value.Text == "Work VPN");
            var manageVpn = Descendants<Button>(page).Single(control => Equals(control.Content, vm.ManageVpnLabel));
            ((IInvokeProvider)new ButtonAutomationPeer(manageVpn).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(40);
            connection.Verify(source => source.OpenVpnSettingsAsync(), Times.Once);
            var start = (Button)page.FindName("StartSpeedTestButton");
            var invoke = (IInvokeProvider)new ButtonAutomationPeer(start).GetPattern(PatternInterface.Invoke);
            invoke.Invoke(); await Task.Delay(40);
            invoke.Invoke(); await Task.Delay(40);
            var current = language == "it" ? "144,5" : "144.5";
            Assert.Contains(Descendants<TextBlock>(page), value => value.Inlines.OfType<Run>().Select(run => run.Text).SequenceEqual([current, " Mbps"]));
            Assert.Contains(Descendants<TextBlock>(page), value => value.Text == vm.DownloadMetric.Previous);
            speedTest.Verify(source => source.MeasureAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
            var delivery = Descendants<NumberBox>(page).Single(control => control.Visibility == Visibility.Visible && control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == "delivery-background-limit");
            var deliveryChoice = Assert.IsType<NetworkTuningChoice>(delivery.DataContext);
            delivery.Value = 5000; await Task.Delay(40);
            service.Verify(source => source.SetNetworkTuningAsync("delivery-background-limit", "5000", null), Times.Once);
            Assert.Equal(string.Format(strings["OptimizeNetwork_OriginalValue"], strings["OptimizeNetwork_NotConfigured"]), deliveryChoice.OriginalLabel);
            Assert.Equal(string.Format(strings["OptimizeNetwork_SuggestedValue"], strings["OptimizeNetwork_NotConfigured"]), deliveryChoice.SuggestedLabel);
            Assert.Equal(5000, deliveryChoice.NumberValue);
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
            var networkCard = (FrameworkElement)page.FindName("NetworkSummaryCard");
            var networkSettings = (FrameworkElement)page.FindName("NetworkSettingsSection");
            var autoTuning = Descendants<ComboBox>(networkSettings).Single(control => control.Visibility == Visibility.Visible &&
                control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == NetworkTuningCatalog.AutoTuningId);
            autoTuning.SelectedItem = autoTuning.Items.OfType<NetworkTuningOption>().Single(option => option.Value == "Restricted");
            await Task.Delay(40);
            service.Verify(source => source.SetNetworkTuningAsync(NetworkTuningCatalog.AutoTuningId, "Restricted", "Normal"), Times.Once);
            service.Verify(source => source.SetNetworkTuningAsync(NetworkTuningCatalog.RssId, It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
            Assert.Contains(Descendants<TextBlock>(networkCard), text => text.Text == strings["OptimizeNetwork_Option_Restricted"]);
            var rss = Descendants<ToggleSwitch>(networkSettings).Single(control => control.Visibility == Visibility.Visible &&
                control.DataContext is NetworkTuningChoice choice && choice.Definition.Id == NetworkTuningCatalog.RssId);
            ((IToggleProvider)new ToggleSwitchAutomationPeer(rss).GetPattern(PatternInterface.Toggle)).Toggle();
            await Task.Delay(40);
            service.Verify(source => source.SetNetworkTuningAsync(NetworkTuningCatalog.RssId, "Disabled", "Enabled"), Times.Once);
            service.Verify(source => source.SetNetworkTuningAsync(NetworkTuningCatalog.AutoTuningId, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
            Assert.Contains(Descendants<TextBlock>(networkCard), text => text.Text == strings["Common_Off"]);
            timestamps.SelectedItem = timestamps.Items.OfType<NetworkTuningOption>().Single(option => option.Value == "Enabled");
            await Task.Delay(40);
            service.Verify(source => source.SetNetworkTuningAsync("tcp-timestamps", "Enabled", "Allowed"), Times.Once);
            timestamps.SelectedItem = allowed;
            await Task.Delay(40);
            service.Verify(source => source.SetNetworkTuningAsync("tcp-timestamps", "Allowed", "Enabled"), Times.Once);
            Assert.Same(allowed, timestamps.SelectedItem);
            var scroll = Descendants<ScrollViewer>(page).First();
            var speedCard = (FrameworkElement)page.FindName("SpeedTestCard");
            var search = (AutoSuggestBox)page.FindName("NetworkSettingsSearch");
            var dnsControls = (Border)page.FindName("DnsControls");
            var vpnCard = (Border)page.FindName("VpnCard");
            Assert.Same(dns, Assert.Single(Descendants<ComboBox>(networkCard)));
            Assert.Contains(applyDns, Descendants<Button>(networkCard));
            var settingRows = Descendants<Border>(networkSettings)
                .Where(border => border.Name == "NetworkSettingRow").ToArray();
            var settingGroups = Descendants<Border>(networkSettings)
                .Where(border => border.Name == "NetworkSettingsGroup").ToArray();
            Assert.Equal(26, settingRows.Length);
            Assert.Equal(6, settingGroups.Length);
            Assert.All(settingRows, row =>
            {
                var choice = Assert.IsType<NetworkTuningChoice>(row.DataContext);
                var comparison = Descendants<TextBlock>(row).Single(text => text.Name == "NetworkValueComparison");
                Assert.Equal(new[] { choice.OriginalLabel, " · ", choice.SuggestedLabel },
                    comparison.Inlines.OfType<Run>().Select(run => run.Text));
            });
            search.Text = "HostsPriority";
            Assert.Equal("HostsPriority", vm.SettingsSearchText);
            Assert.Equal(Visibility.Collapsed, dnsControls.Visibility);
            Assert.Equal(Visibility.Collapsed, vpnCard.Visibility);
            Assert.Single(settingGroups, border => border.Visibility == Visibility.Visible);
            var matchingRow = Assert.Single(settingRows, border => border.Visibility == Visibility.Visible);
            Assert.Equal("priority-hosts", Assert.IsType<NetworkTuningChoice>(matchingRow.DataContext).Definition.Id);
            var hosts = Descendants<NumberBox>(matchingRow).Single();
            hosts.Value = 12;
            await Task.Delay(40);
            service.Verify(source => source.SetNetworkTuningAsync("priority-hosts", "12", "5"), Times.Once);
            search.Text = "no such setting";
            Assert.Contains(Descendants<TextBlock>(networkSettings), text => text.Text == vm.NoSettingsMatchesLabel && text.Visibility == Visibility.Visible);
            search.Text = vm.DnsTitle;
            Assert.Equal(Visibility.Visible, dnsControls.Visibility);
            Assert.Equal(Visibility.Collapsed, vpnCard.Visibility);
            Assert.False(vm.HasNoSettingsMatches);
            search.Text = "";
            Assert.All(settingRows, row => Assert.Equal(Visibility.Visible, row.Visibility));
            Assert.Equal(Visibility.Visible, dnsControls.Visibility);
            Assert.Equal(Visibility.Visible, vpnCard.Visibility);
            foreach (var width in new[] { 620, 1040, 1300 })
            {
                _window!.AppWindow.ResizeClient(new SizeInt32(width, 900)); await Task.Delay(80); shell.UpdateLayout();
                scroll.ChangeView(null, 0, null, true); await Task.Delay(40);
                var speedPosition = speedCard.TransformToVisual(shell).TransformPoint(default);
                var networkPosition = networkCard.TransformToVisual(shell).TransformPoint(default);
                if (width >= 1040)
                {
                    Assert.True(networkPosition.X > speedPosition.X + speedCard.ActualWidth);
                    Assert.InRange(Math.Abs(networkPosition.Y - speedPosition.Y), 0, 1);
                    Assert.True(speedCard.ActualWidth > networkCard.ActualWidth);
                }
                else
                {
                    Assert.True(networkPosition.Y > speedPosition.Y + speedCard.ActualHeight);
                    Assert.InRange(Math.Abs(networkPosition.X - speedPosition.X), 0, 1);
                }
                var dnsPosition = dns.TransformToVisual(dnsControls).TransformPoint(default);
                var applyPosition = applyDns.TransformToVisual(dnsControls).TransformPoint(default);
                Assert.True(dnsPosition.X >= 0 && dnsPosition.X + dns.ActualWidth <= dnsControls.ActualWidth);
                Assert.True(applyPosition.X >= 0 && applyPosition.X + applyDns.ActualWidth <= dnsControls.ActualWidth);
                if (width == 1040)
                {
                    Assert.True(applyPosition.Y > dnsPosition.Y + dns.ActualHeight);
                    Assert.InRange(Math.Abs(applyPosition.X - dnsPosition.X), 0, 1);
                }
                else
                {
                    Assert.True(applyPosition.X > dnsPosition.X + dns.ActualWidth);
                    Assert.InRange(Math.Abs(applyPosition.Y - dnsPosition.Y), 0, 1);
                }
                if (width == 1040) continue;
                scroll.ChangeView(null, scroll.ScrollableHeight, null, true); await Task.Delay(40);
                Assert.True(local.ActualWidth > 0);
                var bounds = local.TransformToVisual(shell).TransformPoint(default);
                Assert.True(bounds.X > shell.ActualWidth / 2 && bounds.X + local.ActualWidth <= shell.ActualWidth + 1, "Priority editor is not within the right-hand column.");
            }

            shell.Children.Clear();
        }
        Console.WriteLine("PASS: Speed test, inline DNS and VPN controls, searchable network settings, and English/Italian layout at three widths.");
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

    private void Finish(string? error = null)
    {
        File.WriteAllText(Path.Combine(outputDirectory, "result.log"), error ?? "All Network checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
