using System.ComponentModel;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Extensions;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.Preview;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using PreviewCompanion = WinOptimizer.UI.Features.Extensions.Preview.PreviewApplication;

namespace WinOptimizer.PopupTests;

public sealed partial class PreviewApplication
{
    /// <summary>Exercises the extension settings and the shared Preview shortcut state.</summary>
    private async Task CheckExtensionSettingsAsync(Grid shell)
    {
        var localization = ExtensionLocalization();
        Mock.Get(localization).SetupGet(service => service.CurrentLanguage).Returns("en");
        var dispatcher = new ExtensionTestDispatcher();
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(service => service.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(new SystemMonitorOptions());
        preferences.Setup(service => service.SetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>()))
            .ReturnsAsync(OperationResult.Succeeded());
        preferences.Setup(service => service.SetPreferenceAsync(NotchExtension.StartupPreferenceKey, true))
            .ReturnsAsync(OperationResult.Succeeded());
        preferences.Setup(service => service.SetPreferenceAsync(NotchExtension.StartupPreferenceKey, false))
            .ReturnsAsync(OperationResult.Failed("The startup preference could not be saved."));
        var host = new Mock<ISystemMonitorWidgetHost>();
        host.Setup(widget => widget.ApplyOptionsAsync(It.IsAny<SystemMonitorOptions>())).Returns(Task.CompletedTask);
        var startup = new Mock<ISystemMonitorStartupService>();
        startup.Setup(service => service.ApplyAsync(It.IsAny<SystemMonitorOptions>())).ReturnsAsync(OperationResult.Succeeded());
        using var extensions = new ExtensionsViewModel(preferences.Object, localization, host.Object, dispatcher, startup.Object);
        using var catalog = new UtilitiesCatalogViewModel(localization, dispatcher);

        var confirmed = new PreviewSettingsSnapshot(ShortcutEnabled: true, StartsWithWindows: true);
        var pending = new TaskCompletionSource<PreviewSettingsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<(bool Enabled, string Language)>();
        var startupRequests = new List<bool>();
        var opacityRequests = new List<int>();
        var opacityPending = new TaskCompletionSource<PreviewSettingsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opacitySubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = new PreviewSettingsViewModel(localization, () => Task.FromResult(confirmed), (enabled, language) =>
        {
            requests.Add((enabled, language));
            return pending.Task;
        }, enabled =>
        {
            startupRequests.Add(enabled);
            confirmed = confirmed with { StartsWithWindows = enabled };
            return Task.FromResult(confirmed);
        }, opacity =>
        {
            opacityRequests.Add(opacity);
            opacitySubmitted.TrySetResult();
            return opacityPending.Task;
        });
        preferences.Setup(service => service.ReadSnapshotAsync()).ReturnsAsync(new UserPreferencesSnapshot(
            new Dictionary<string, object>(), Mock.Of<ILogService>()));
        using var notch = new NotchExtension(preferences.Object, localization, Mock.Of<ILogService>(), Mock.Of<INotchStartupService>(),
            Mock.Of<IConnectionTrafficService>());
        var overview = new UtilitiesPage(catalog, extensions, preview, notch);
        var details = new PreviewPage(localization, preview);
        shell.Children.Add(overview);
        shell.Children.Add(details);
        try
        {
            await LoadedAsync(overview);
            await LoadedAsync(details);
            var overviewSwitch = (ToggleSwitch)overview.FindName("PreviewToggle");
            var options = (PreviewOptions)details.FindName("Options");
            var detailSwitch = (ToggleSwitch)options.FindName("Shortcut");
            var previewStartupToggle = (ToggleSwitch)options.FindName("Startup");
            CheckStartupOrder((StackPanel)options.Content, previewStartupToggle, localization.GetString("QuickPreview_Shortcut"));
            var opacitySlider = (Slider)options.FindName("OpacitySlider");
            var opacityValue = (TextBlock)options.FindName("OpacityValue");
            var opacityRange = (IRangeValueProvider)new SliderAutomationPeer(opacitySlider).GetPattern(PatternInterface.RangeValue);
            var error = (InfoBar)overview.FindName("PreviewError");
            var cardsPanel = (StackPanel)error.Parent;
            var cards = cardsPanel.Children.OfType<NavigationCard>().ToArray();
            overview.UpdateLayout();
            CheckCardGap(cards[0], cards[1]);
            CheckCardGap(cards[1], cards[2]);
            Console.WriteLine("PASS: Extensions cards have uniform vertical spacing.");
            Require(overviewSwitch.IsOn && detailSwitch.IsOn && requests.Count == 0,
                "Loading Preview did not show the active shortcut on both pages without writing it.");
            Require(opacityRange.Minimum == 0 && opacityRange.Maximum == 10 && opacitySlider.StepFrequency == 1 &&
                options.BackgroundOpacity == 8 && opacityValue.Text == "8/10" && opacityRequests.Count == 0,
                "Preview did not display the default opacity without writing a setting.");
            await CaptureAsync(options, "preview-opacity-slider.png");

            Toggle(overviewSwitch);
            Require(requests.SequenceEqual([(false, "en")]) && !overviewSwitch.IsEnabled && !options.IsEnabled,
                "The overview switch did not disable the shortcut or lock editing during the request.");
            await CompleteAsync(() => pending.SetResult(confirmed = confirmed with { ShortcutEnabled = false }));
            Require(!overviewSwitch.IsOn && !detailSwitch.IsOn && options.StartsWithWindows,
                "Disabling Preview did not update both switches while retaining the sign-in setting.");

            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Toggle(detailSwitch);
            Require(requests.SequenceEqual([(false, "en"), (true, "en")]), "The details switch did not enable the same shortcut.");
            await CompleteAsync(() => pending.SetResult(confirmed = confirmed with { ShortcutEnabled = true }));
            Require(overviewSwitch.IsOn && detailSwitch.IsOn, "Enabling Preview in its details did not update both switches.");

            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Toggle(overviewSwitch);
            await CompleteAsync(() => pending.SetException(new IOException("Preview companion unavailable")));
            Require(overviewSwitch.IsOn && detailSwitch.IsOn && requests.Count == 3 && error.IsOpen &&
                error.Visibility == Visibility.Visible && error.Message == "Preview companion unavailable",
                "A failed change did not restore the confirmed shortcut and display the error.");

            Require(startupRequests.Count == 0, "Changing the Preview shortcut changed sign-in startup.");
            Require(previewStartupToggle.IsOn, "Preview settings did not show the saved sign-in choice.");
            Toggle(previewStartupToggle);
            Require(!options.StartsWithWindows && startupRequests.SequenceEqual([false]) && overviewSwitch.IsOn && detailSwitch.IsOn,
                "Preview settings should change sign-in startup independently of the shortcut.");

            opacityRange.SetValue(5);
            opacityRange.SetValue(4);
            Require(opacityValue.Text == "4/10" && options.IsEnabled, "The slider did not show the current adjustment.");
            await opacitySubmitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(opacityRequests.SequenceEqual([4]) && !options.IsEnabled, "The slider did not submit the final value.");
            await CompleteAsync(() => opacityPending.SetResult(confirmed = confirmed with { BackgroundOpacity = 4 }));
            Require(options.BackgroundOpacity == 4 && options.IsEnabled, "The opacity control did not display the confirmed value.");

            opacityPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            opacitySubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            opacityRange.SetValue(2);
            await opacitySubmitted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await CompleteAsync(() => opacityPending.SetException(new IOException("Opacity could not be saved")));
            Require(options.BackgroundOpacity == 4 && opacityValue.Text == "4/10" && opacityRequests.SequenceEqual([4, 2]),
                "A failed opacity write did not restore the confirmed value.");

            if (DesktopAcrylicController.IsSupported() && _window is { } window)
            {
                var backdrop = PreviewCompanion.CreateBackdrop(PreviewExtensionSettings.DefaultOpacity);
                window.SystemBackdrop = backdrop;
                try { backdrop.TintOpacity = 0.4f; }
                finally { window.SystemBackdrop = null; }
            }
        }
        finally
        {
            shell.Children.Remove(details);
            shell.Children.Remove(overview);
        }

        await CheckWidgetStartupSettingsAsync(shell, extensions, notch);
        Console.WriteLine("PASS: SysMonitor, Preview and Notch settings place sign-in startup directly below activation and save the choice independently.");

        async Task CompleteAsync(Action reply)
        {
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(preview.IsBusy) && !preview.IsBusy) idle.TrySetResult();
            }
            preview.PropertyChanged += OnChanged;
            try
            {
                reply();
                await idle.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { preview.PropertyChanged -= OnChanged; }
        }
    }

    /// <summary>Checks the first settings rows through the production widget pages.</summary>
    private static async Task CheckWidgetStartupSettingsAsync(Grid shell, ExtensionsViewModel extensions, NotchExtension notch)
    {
        var monitor = new SystemMonitorSettingsPage(extensions);
        var notchHost = new Frame();
        shell.Children.Add(monitor);
        shell.Children.Add(notchHost);
        try
        {
            notchHost.Navigate(typeof(NotchSettingsPage), notch);
            var notchSettings = (NotchSettingsPage)notchHost.Content;
            await LoadedAsync(monitor);
            await LoadedAsync(notchSettings);

            var monitorRows = (StackPanel)((ScrollViewer)monitor.Content).Content;
            var monitorStartup = (ToggleSwitch)monitor.FindName("StartupToggle");
            CheckStartupOrder(monitorRows.Children.OfType<StackPanel>().First(), monitorStartup, extensions.EnableLabel);
            Toggle(monitorStartup);
            Require(await extensions.FlushAsync() && extensions.Options.StartWithWindows && !extensions.Options.Enabled,
                "SysMonitor settings should save startup independently of the widget enable switch.");

            var notchRows = (StackPanel)((ScrollViewer)notchSettings.Content).Content;
            var notchStartup = (ToggleSwitch)notchSettings.FindName("StartupToggle");
            CheckStartupOrder(notchRows, notchStartup, notch.EnableLabel);
            notchSettings.UpdateLayout();
            var cards = notchRows.Children.OfType<SettingsCard>().ToArray();
            for (var index = 1; index < cards.Length; index++) CheckCardGap(cards[index - 1], cards[index]);
            Toggle(notchStartup);
            await notch.FlushAsync();
            Require(notch.StartWithWindows && !notch.IsEnabled && !notch.IsRunning,
                "Notch settings should save startup independently of the widget enable switch.");
            Toggle(notchStartup);
            await notch.FlushAsync();
            notchSettings.UpdateLayout();
            var notice = notchRows.Children.OfType<InfoBar>().Single();
            Require(notice.IsOpen && notice.Visibility == Visibility.Visible, "Notch did not show the startup error.");
            CheckCardGap(cards[1], notice);
            CheckCardGap(notice, cards[2]);
            Console.WriteLine("PASS: Notch settings keep the same card spacing with and without a visible error.");
        }
        finally
        {
            shell.Children.Remove(notchHost);
            shell.Children.Remove(monitor);
        }
    }

    private static void CheckStartupOrder(StackPanel rows, ToggleSwitch startup, string enableLabel)
    {
        var enableIndex = Enumerable.Range(0, rows.Children.Count)
            .First(index => rows.Children[index] is SettingsCard card && Equals(card.Header, enableLabel));
        Require(enableIndex + 1 < rows.Children.Count && rows.Children[enableIndex + 1] is SettingsCard next &&
            ReferenceEquals(next.Content, startup), $"The startup switch should appear directly below {enableLabel}.");
    }

    private static void CheckCardGap(FrameworkElement first, FrameworkElement next)
    {
        var gap = next.TransformToVisual(first).TransformPoint(default).Y - first.ActualHeight;
        Require(first.ActualHeight > 0 && next.ActualHeight > 0 && Math.Abs(gap - 12) < 1,
            $"Visible page blocks should be 12 DIPs apart. Measured {gap:0.##}.");
    }

    private static void Toggle(ToggleSwitch control) =>
        ((IToggleProvider)new ToggleSwitchAutomationPeer(control).GetPattern(PatternInterface.Toggle)).Toggle();
}
