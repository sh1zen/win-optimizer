using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;

namespace WinOptimizer.PopupTests;

public sealed partial class PreviewApplication
{
    /// <summary>Exercises the real Notch view with simulated Windows hardware and Settings launches.</summary>
    private async Task CheckNotchQuickSettingsAsync()
    {
        var log = Mock.Of<ILogService>();
        var localization = ExtensionLocalization();
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(item => item.ReadSnapshotAsync()).ReturnsAsync(new UserPreferencesSnapshot(new Dictionary<string, object>
        {
            [NotchExtension.PreferenceKey] = true,
            [NotchExtension.ModulesPreferenceKey] = (int)NotchModule.QuickSettings
        }, log));
        var state = new DesktopQuickSettings(80, new DesktopVolume(62, false), WiFi: true, Bluetooth: false,
            Battery: new DesktopPower(OnAc: true, Percent: 91, IsCharging: true), PowerMode: DesktopPowerMode.Balanced,
            NightLight: false, DoNotDisturb: true);
        var settings = new Mock<IDesktopQuickSettings>(MockBehavior.Strict);
        var opened = new List<DesktopSettingsPage>();
        var levels = new List<(string Setting, int Value)>();
        bool failMute = true;
        settings.Setup(service => service.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => state);
        settings.Setup(service => service.SetBrightnessAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((int value, CancellationToken _) => { levels.Add(("Brightness", value)); state = state with { Brightness = value }; })
            .Returns(Task.CompletedTask);
        settings.Setup(service => service.SetVolumeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((int value, CancellationToken _) => { levels.Add(("Volume", value)); state = state with { Volume = new DesktopVolume(value, false) }; })
            .Returns(Task.CompletedTask);
        settings.Setup(service => service.SetRadioAsync(DesktopRadio.WiFi, false, It.IsAny<CancellationToken>()))
            .Callback(() => state = state with { WiFi = false }).Returns(Task.CompletedTask);
        settings.Setup(service => service.SetRadioAsync(DesktopRadio.Bluetooth, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        settings.Setup(service => service.SetMuteAsync(true, It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (failMute) return Task.FromException(new IOException("Rejected mute."));
            state = state with { Volume = new DesktopVolume(state.Volume!.Value.Percent, true) };
            return Task.CompletedTask;
        });
        settings.Setup(service => service.SetPowerModeAsync(DesktopPowerMode.Performance, It.IsAny<CancellationToken>()))
            .Callback(() => state = state with { PowerMode = DesktopPowerMode.Performance }).Returns(Task.CompletedTask);
        settings.Setup(service => service.SetNightLightAsync(true, It.IsAny<CancellationToken>()))
            .Callback(() => state = state with { NightLight = true }).Returns(Task.CompletedTask);
        settings.Setup(service => service.SetDoNotDisturbAsync(false, It.IsAny<CancellationToken>()))
            .Callback(() => state = state with { DoNotDisturb = false }).Returns(Task.CompletedTask);
        settings.Setup(service => service.OpenAsync(It.IsAny<DesktopSettingsPage>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopSettingsPage page, CancellationToken _) => opened.Add(page)).Returns(Task.CompletedTask);
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            extension => window = new NotchWindow(extension, localization, log, Mock.Of<IDesktopMediaSession>(), quickSettings: settings.Object));
        await notch.LoadAsync();
        var root = (Grid)window!.Content;
        await LoadedAsync(root);
        var panel = (NotchQuickSettings)root.FindName("QuickSettingsPanel");
        T Control<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        static void Toggle(ToggleButton button) => ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();

        settings.Verify(service => service.ReadAsync(It.IsAny<CancellationToken>()), Times.Never);
        Invoke((Button)root.FindName("Grip"));
        var brightness = Control<Slider>("Brightness");
        var volume = Control<Slider>("Volume");
        var wifi = Control<ToggleButton>("WiFi");
        var bluetooth = Control<ToggleButton>("Bluetooth");
        var mute = Control<ToggleButton>("Mute");
        var nightLight = Control<ToggleButton>("NightLight");
        var doNotDisturb = Control<ToggleButton>("DoNotDisturb");
        var performance = Control<ToggleButton>("Performance");
        var status = Control<TextBlock>("SettingsError");
        await WaitForNotchAsync(() => ((Grid)root.FindName("Panel")).Visibility == Visibility.Visible && brightness.IsEnabled);
        Require(brightness.Value == 80 && volume.Value == 62 && wifi.IsChecked == true && bluetooth.IsChecked == false &&
            nightLight.IsChecked == false && doNotDisturb.IsChecked == true &&
            Control<ToggleButton>("Balanced").IsChecked == true && Control<TextBlock>("BatteryValue").Text.Contains("91"),
            "Quick Settings did not show the current Windows values.");

        brightness.Value = 78;
        brightness.Value = 73;
        volume.Value = 51;
        volume.Value = 48;
        await WaitForNotchAsync(() => levels.Count == 2);
        Require(levels.SequenceEqual([("Brightness", 73), ("Volume", 48)]), "Slider writes were not coalesced to the final values.");
        Toggle(wifi);
        await WaitForNotchAsync(() => wifi.IsChecked == false && wifi.IsEnabled && state.WiFi == false);
        Toggle(bluetooth);
        await WaitForNotchAsync(() => opened.Contains(DesktopSettingsPage.Bluetooth) && bluetooth.IsEnabled);
        Require(bluetooth.IsChecked == false, "A denied Bluetooth operation displayed an unconfirmed state.");

        Toggle(mute);
        await WaitForNotchAsync(() => status.Visibility == Visibility.Visible && mute.IsEnabled);
        Require(mute.IsChecked == false, "A failed change did not restore the Windows mute state.");
        failMute = false;
        Toggle(mute);
        await WaitForNotchAsync(() => mute.IsChecked == true && performance.IsEnabled);
        Toggle(performance);
        await WaitForNotchAsync(() => performance.IsChecked == true && nightLight.IsEnabled);
        Toggle(nightLight);
        await WaitForNotchAsync(() => nightLight.IsChecked == true && nightLight.IsEnabled && state.NightLight == true);
        Toggle(doNotDisturb);
        await WaitForNotchAsync(() => doNotDisturb.IsChecked == false && doNotDisturb.IsEnabled && state.DoNotDisturb == false);
        Require(opened.SequenceEqual([DesktopSettingsPage.Bluetooth]), "Night light or Do not disturb launched Windows Settings.");

        state = state with { Brightness = null, PowerMode = null, NightLight = null, DoNotDisturb = null };
        var powerSettings = Control<Button>("PowerSettings");
        await WaitForNotchAsync(() => !brightness.IsEnabled && powerSettings.Visibility == Visibility.Visible && powerSettings.IsEnabled);
        Require(!nightLight.IsEnabled && !doNotDisturb.IsEnabled, "An unavailable control still allowed edits.");
        Invoke(powerSettings);
        await WaitForNotchAsync(() => opened.Contains(DesktopSettingsPage.Power));
        Console.WriteLine("PASS: Notch Quick Settings readings, slider edits, radio permission handling, confirmed changes and Windows Settings links.");
    }
}
