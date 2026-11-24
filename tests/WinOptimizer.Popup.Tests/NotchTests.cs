using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Extensions;
using WinOptimizer.UI.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Notch;
using WinOptimizer.UI.Features.Extensions.Preview;
using WinOptimizer.UI.Features.Extensions.ViewModels;
using WinOptimizer.UI.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace WinOptimizer.PopupTests;

public sealed partial class PreviewApplication
{
    /// <summary>Verifies that the dedicated calendar aligns with Today and shows every week.</summary>
    private async Task CheckNotchCalendarLayoutAsync()
    {
        var localization = ExtensionLocalization();
        var log = Mock.Of<ILogService>();
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(new UserPreferencesSnapshot(
            new Dictionary<string, object>
            {
                [NotchExtension.PreferenceKey] = true,
                [NotchExtension.ModulesPreferenceKey] = (int)(NotchModule.Calendar | NotchModule.Clock)
            }, log));
        var readings = new NotchReadings(log, _ => throw new InvalidOperationException("System counters are disabled."),
            _ => throw new InvalidOperationException("Battery readings are disabled."));
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            extension => window = new NotchWindow(extension, localization, log, Mock.Of<IDesktopMediaSession>(), readings));
        await notch.LoadAsync();
        var root = (Grid)(window ?? throw new InvalidOperationException("The calendar test did not create the Notch.")).Content;
        await LoadedAsync(root);

        var grip = (Button)root.FindName("Grip");
        ((IInvokeProvider)new ButtonAutomationPeer(grip).GetPattern(PatternInterface.Invoke)).Invoke();
        var calendar = (CalendarView)root.FindName("CalendarPanel");
        var today = (Button)root.FindName("TodayButton");
        await WaitForNotchAsync(() => calendar.ActualHeight > 0 && today.ActualHeight > 0);
        var top = calendar.TransformToVisual(today).TransformPoint(default).Y;
        Require(Math.Abs(top) < 1 && Math.Abs(calendar.ActualHeight - today.ActualHeight) < 1,
            $"The calendar and Today cards are misaligned. Calendar height {calendar.ActualHeight}, Today height {today.ActualHeight}, top offset {top}.");

        // August 2026 spans six rows in a Sunday-first calendar.
        calendar.FirstDayOfWeek = Windows.Globalization.DayOfWeek.Sunday;
        calendar.SetDisplayDate(new DateTimeOffset(new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Local)));
        var monthViewport = Descendants<ScrollViewer>(calendar).Single(viewer => viewer.Name == "MonthViewScrollViewer");
        await WaitForNotchAsync(() => Descendants<CalendarViewDayItem>(monthViewport).Count(day =>
        {
            if (day.Date.Year != 2026 || day.Date.Month != 8 || day.ActualHeight <= 0) return false;
            var y = day.TransformToVisual(monthViewport).TransformPoint(default).Y;
            return y >= -1 && y + day.ActualHeight <= monthViewport.ViewportHeight + 1;
        }) == 31);
        await CaptureAsync(root, "notch-calendar-six-weeks.png");
        Console.WriteLine("PASS: the full Notch calendar aligns with Today and displays all six weeks.");
    }

    /// <summary>Exercises the separate battery view, including its compact sampling and overflow.</summary>
    private async Task CheckNotchBatteriesLayoutAsync()
    {
        var localization = ExtensionLocalization();
        var log = Mock.Of<ILogService>();
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(new UserPreferencesSnapshot(
            new Dictionary<string, object>
            {
                [NotchExtension.PreferenceKey] = true,
                [NotchExtension.ModulesPreferenceKey] = (int)NotchModule.Batteries,
                [NotchExtension.PinnedModulesPreferenceKey] = (int)NotchModule.Batteries
            }, log));
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.ModulesPreferenceKey, It.IsAny<int>()))
            .ReturnsAsync(OperationResult.Succeeded());
        preferences.Setup(p => p.UpdatePreferencesAsync(It.IsAny<IReadOnlyDictionary<string, object>>()))
            .ReturnsAsync(OperationResult.Succeeded());
        DesktopBattery[] batteries = [new(null, 80, IsCharging: true), new("Mouse", 55)];
        var batterySamples = 0;
        var readings = new NotchReadings(log, _ => throw new InvalidOperationException("System counters are disabled."),
            _ =>
            {
                Interlocked.Increment(ref batterySamples);
                return Task.FromResult<IReadOnlyList<DesktopBattery>>(Volatile.Read(ref batteries));
            });
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            extension => window = new NotchWindow(extension, localization, log, Mock.Of<IDesktopMediaSession>(), readings));
        await notch.LoadAsync();
        var root = (Grid)(window ?? throw new InvalidOperationException("The battery test did not create the Notch.")).Content;
        await LoadedAsync(root);
        var compactBatteries = (ItemsControl)root.FindName("CompactBatteries");
        await WaitForNotchAsync(() => compactBatteries.Items.Count == 2);

        var grip = (Button)root.FindName("Grip");
        ((IInvokeProvider)new ButtonAutomationPeer(grip).GetPattern(PatternInterface.Invoke)).Invoke();
        var panel = (NotchBatteriesPanel)root.FindName("BatteriesPanel");
        var cards = (ItemsControl)panel.FindName("BatteryCards");
        FrameworkElement? Card(int index) => cards.ContainerFromIndex(index) as FrameworkElement;
        await WaitForNotchAsync(() => panel.Visibility == Visibility.Visible && Card(1) is { ActualHeight: > 0 });
        Require(!notch.IsSysInfoEnabled && Volatile.Read(ref batterySamples) == 1,
            "The saved battery module should reuse the compact reading with Sys info off.");
        Require(cards.Items.Cast<NotchBatteryBadge>().Select(card => (card.Label, card.Level, card.ChargingVisibility)).SequenceEqual(
            [("This PC", "80%", Visibility.Visible), ("Mouse", "55%", Visibility.Collapsed)]),
            "The battery cards did not show the levels and charging status for each device.");
        var first = Card(0)!;
        var second = Card(1)!;
        var firstPosition = first.TransformToVisual(panel).TransformPoint(default);
        var secondPosition = second.TransformToVisual(panel).TransformPoint(default);
        Require(firstPosition.Y == secondPosition.Y && firstPosition.X + first.ActualWidth < secondPosition.X &&
            !panel.CanScroll, "Two battery cards did not fit side by side.");
        await CaptureAsync(root, "notch-batteries.png");
        await notch.SetModuleAsync(NotchModule.Uptime, true);
        await notch.SetSysInfoEnabledAsync(true);
        void Select(string tab) => ((ISelectionItemProvider)new RadioButtonAutomationPeer((RadioButton)root.FindName(tab))
            .GetPattern(PatternInterface.SelectionItem)).Select();
        Select("DashboardTab");
        var dashboard = (NotchDashboard)root.FindName("DashboardPanel");
        Require(((ItemsControl)dashboard.FindName("Readings")).Items.Cast<NotchReadingRow>().Single().Module == NotchModule.Uptime,
            "The System view should show the selected system reading.");
        await notch.SetSysInfoEnabledAsync(false);
        Require(panel.Visibility == Visibility.Visible && compactBatteries.Items.Count == 2,
            "Turning off Sys info should leave the battery view and compact pin active.");
        Volatile.Write(ref batteries, Enumerable.Range(1, 8).Select(number => new DesktopBattery($"Device {number}", 80)).ToArray());
        Require(Volatile.Read(ref batterySamples) == 1, "Changing views or Sys info restarted the battery reader.");
        await notch.SetModuleAsync(NotchModule.Batteries, false);
        await notch.SetModuleAsync(NotchModule.Batteries, true);
        await WaitForNotchAsync(() => Volatile.Read(ref batterySamples) == 2);
        Select("BatteriesTab");
        var scroll = (ScrollViewer)panel.FindName("CardsScroll");
        await WaitForNotchAsync(() => cards.Items.Count == 8 && panel.CanScroll);
        scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
        await WaitForNotchAsync(() => scroll.VerticalOffset > 0);
        Console.WriteLine("PASS: the independent Notch battery view reuses readings and fits individual charge cards in a scrolling grid.");
    }

    /// <summary>Exercises Quick access pins with fake file selection, app discovery and preferences.</summary>
    private async Task CheckNotchShortcutsAsync()
    {
        var localization = ExtensionLocalization();
        var log = Mock.Of<ILogService>();
        var preferences = new Mock<IUserPreferencesService>();
        var content = NotchContent.Empty;
        bool failNextWrite = false;
        int writes = 0;
        preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(() => new UserPreferencesSnapshot(
            new Dictionary<string, object>
            {
                [NotchExtension.PreferenceKey] = true,
                [NotchExtension.ModulesPreferenceKey] = (int)(NotchModule.Focus | NotchModule.Shortcuts),
                [NotchExtension.ContentPreferenceKey] = content
            }, log));
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.ContentPreferenceKey, It.IsAny<NotchContent>()))
            .ReturnsAsync((string _, NotchContent value) =>
            {
                writes++;
                if (failNextWrite)
                {
                    failNextWrite = false;
                    return OperationResult.Failed("Shortcut write failed.");
                }
                content = value;
                return OperationResult.Succeeded();
            });

        var file = Path.Combine(Path.GetTempPath(), "Notch Sample.lnk");
        string? selection = null;
        var opened = new List<string>();
        var picks = new List<(Window Owner, string Title)>();
        var calculator = new DesktopApplication(@"shell:AppsFolder\Example.Calculator!App", "Calculator");
        var explorer = new DesktopApplication(@"shell:AppsFolder\Example.Explorer", "Explorer");
        var applications = new Mock<IDesktopApplications>();
        applications.Setup(apps => apps.ReadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { calculator, explorer });
        var iconPixels = Enumerable.Repeat(byte.MaxValue, 24 * 24 * 4).ToArray();
        applications.Setup(apps => apps.ReadIconAsync(calculator.Target, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SoftwareBitmap.CreateCopyFromBuffer(iconPixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                24, 24, BitmapAlphaMode.Premultiplied));
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            extension => window = new NotchWindow(extension, localization, log, Mock.Of<IDesktopMediaSession>(),
                openShortcut: opened.Add, pickShortcutFile: (owner, title) =>
                {
                    picks.Add((owner, title));
                    return selection;
                }, applications: applications.Object));
        int optimizerRequests = 0;
        notch.OpenOptimizerRequested += (_, _) => optimizerRequests++;
        await notch.LoadAsync();
        var shown = window ?? throw new InvalidOperationException("Quick access did not create the Notch.");
        var root = (Grid)shown.Content;
        await LoadedAsync(root);
        Button ButtonNamed(string name) => (Button)root.FindName(name);
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        void SelectTab(string name) => ((ISelectionItemProvider)new RadioButtonAutomationPeer((RadioButton)root.FindName(name))
            .GetPattern(PatternInterface.SelectionItem)).Select();
        var shortcuts = (ItemsControl)root.FindName("ShortcutItems");
        var status = (TextBlock)root.FindName("ShortcutsStatus");
        var header = (Grid)root.FindName("ShortcutsHeader");
        var scroll = (ScrollViewer)root.FindName("ShortcutsPage");
        Button? AppTile() => Descendants<Button>(shortcuts).SingleOrDefault(button => button.Name == "AddFavoriteApp");
        Button? RemoveButton() => Descendants<Button>(shortcuts).SingleOrDefault(button => button.Name == "RemoveShortcut");
        Button? OpenButton() => Descendants<Button>(shortcuts).SingleOrDefault(button => button.Tag is NotchShortcut && button.Name != "RemoveShortcut");
        var add = ButtonNamed("AddShortcut");
        var edit = ButtonNamed("EditShortcuts");
        Invoke(ButtonNamed("Grip"));
        await WaitForNotchAsync(() => ((Grid)root.FindName("Panel")).Visibility == Visibility.Visible);
        SelectTab("ShortcutsTab");
        await WaitForNotchAsync(() => AppTile() is { ActualWidth: > 0 } && scroll.ViewportHeight > 0);
        Require(edit.Visibility == Visibility.Collapsed && status.Text == "Use + to pin an app.", "The empty state did not point to the add tile.");
        var tile = AppTile()!;
        var tileOrigin = tile.TransformToVisual(scroll).TransformPoint(default);
        var statusOrigin = status.TransformToVisual(scroll).TransformPoint(default);
        var headerOrigin = header.TransformToVisual(scroll).TransformPoint(default);
        Require(headerOrigin.Y + header.ActualHeight < 0 && statusOrigin.Y >= 0 && tileOrigin.Y > statusOrigin.Y &&
            Math.Abs(tileOrigin.X + tile.ActualWidth / 2 - scroll.ViewportWidth / 2) <= 2 &&
            Math.Abs(statusOrigin.Y + tileOrigin.Y + tile.ActualHeight - scroll.ViewportHeight) <= 2,
            $"Quick access did not center its empty state below the fixed header. Tile at {tileOrigin}, hint at {statusOrigin}, " +
            $"viewport {scroll.ViewportWidth} × {scroll.ViewportHeight}.");
        await CaptureAsync(root, "notch-shortcuts-empty.png");

        Invoke(add);
        await WaitForNotchAsync(() => picks.Count == 1);
        Require(ReferenceEquals(picks[0].Owner, shown) && picks[0].Title == "Add file" && writes == 0 && optimizerRequests == 0,
            "The Notch did not own the picker, or cancellation opened the optimizer or saved a shortcut.");
        selection = file;
        Invoke(add);
        await WaitForNotchAsync(() => OpenButton() is not null && RemoveButton() is not null);
        Require(content.Shortcuts.Single().Path == file && writes == 1 && optimizerRequests == 0 &&
            edit.Content is "Edit" && RemoveButton()!.Visibility == Visibility.Collapsed && OpenButton()!.IsEnabled,
            "Adding a file did not persist an openable tile without a removal button.");

        Invoke(edit);
        await WaitForNotchAsync(() => RemoveButton()?.Visibility == Visibility.Visible);
        Require(edit.Content is "Done" && !OpenButton()!.IsEnabled && RemoveButton() is
            { HorizontalAlignment: HorizontalAlignment.Right, VerticalAlignment: VerticalAlignment.Top } remove &&
            AutomationProperties.GetName(remove) == "Remove Notch Sample from Quick access",
            "Editing did not show a labeled removal action in the top-right corner of the tile.");
        await CaptureAsync(root, "notch-shortcuts-edit.png");
        failNextWrite = true;
        Invoke(RemoveButton()!);
        await WaitForNotchAsync(() => status.Text == "Shortcut write failed.");
        Require(content.Shortcuts.Single().Path == file && RemoveButton()?.Visibility == Visibility.Visible,
            "A failed save removed the confirmed shortcut.");

        Invoke(edit);
        await WaitForNotchAsync(() => OpenButton()?.IsEnabled == true && RemoveButton()?.Visibility == Visibility.Collapsed);
        Invoke(edit);
        await WaitForNotchAsync(() => RemoveButton()?.Visibility == Visibility.Visible);
        Invoke(RemoveButton()!);
        await WaitForNotchAsync(() => content.Shortcuts.Length == 0 && edit.Visibility == Visibility.Collapsed);
        Require(shortcuts.Items.Count == 1 && status.Text == "Use + to pin an app." && writes == 3 && opened.Count == 0,
            "Removing the shortcut did not persist the empty list, or it opened the pinned file.");

        var appPicker = (StackPanel)root.FindName("AppPicker");
        var appChoice = (ComboBox)root.FindName("AppChoice");
        await WaitForNotchAsync(() => AppTile() is not null);
        Require(AutomationProperties.GetName(AppTile()!) == "Add app", "The add tile did not have an accessible name.");
        Invoke(AppTile()!);
        await WaitForNotchAsync(() => appChoice.IsEnabled && appChoice.Items.Count == 2);
        Invoke(ButtonNamed("CancelApp"));
        Require(appPicker.Visibility == Visibility.Collapsed && writes == 3,
            "Cancelling the app picker changed the saved pins.");

        Invoke(AppTile()!);
        await WaitForNotchAsync(() => appChoice.Items.Count == 2);
        appChoice.SelectedItem = calculator;
        Invoke(ButtonNamed("ConfirmApp"));
        await WaitForNotchAsync(() => OpenButton() is { IsEnabled: true } &&
            Descendants<Image>(shortcuts).Any(image => image.Source is not null));
        Require(content.Shortcuts.Single() is { AppName: "Calculator" } favorite && favorite.Path == calculator.Target &&
            AutomationProperties.GetName(OpenButton()!) == calculator.Name && writes == 4 && shortcuts.Items.Count == 2,
            "Pinning an installed app did not save its identity and show an openable tile with an icon.");
        var source = Descendants<Image>(shortcuts).Single(image => image.Source is not null).Source;
        Require(source is WriteableBitmap pixels && pixels.PixelBuffer.ToArray().SequenceEqual(iconPixels),
            "The rendered icon should own its copied pixels after the shell bitmap is released.");
        SelectTab("FocusTab");
        SelectTab("ShortcutsTab");
        await WaitForNotchAsync(() => Descendants<Image>(shortcuts).Any(image => ReferenceEquals(image.Source, source)));
        applications.Verify(apps => apps.ReadIconAsync(calculator.Target, It.IsAny<CancellationToken>()), Times.Once);
        var restored = JsonSerializer.Deserialize<NotchContent>(JsonSerializer.Serialize(content with
        {
            Shortcuts = [new(file, IsFolder: false), .. content.Shortcuts,
                new(@"shell:AppsFolder\..\other", IsFolder: false) { AppName = "Invalid" }]
        }))!.Normalize();
        Require(restored.Shortcuts.SequenceEqual(new[] { new NotchShortcut(file, IsFolder: false), content.Shortcuts.Single() }),
            "Quick access did not restore valid file and app pins together.");

        Invoke(AppTile()!);
        await WaitForNotchAsync(() => appChoice.Items.Count == 1);
        Require(Equals(appChoice.Items[0], explorer), "The picker offered an app that was already pinned.");
        Invoke(ButtonNamed("CancelApp"));
        Invoke(OpenButton()!);
        await WaitForNotchAsync(() => opened.SequenceEqual([calculator.Target]) && ((Grid)root.FindName("Panel")).Visibility == Visibility.Collapsed);
        Invoke(ButtonNamed("Grip"));
        await WaitForNotchAsync(() => ((Grid)root.FindName("Panel")).Visibility == Visibility.Visible);
        Invoke(edit);
        await WaitForNotchAsync(() => RemoveButton()?.Visibility == Visibility.Visible);
        Invoke(RemoveButton()!);
        await WaitForNotchAsync(() => content.Shortcuts.Length == 0 && edit.Visibility == Visibility.Collapsed);
        Require(writes == 5, "Removing an app did not update the saved pins.");
        Console.WriteLine("PASS: Notch file and app selection, pinned icons, activation, cancelled changes, edit mode and persistence.");
    }

    /// <summary>Shows every available charge when pinned and keeps the default compact bar at clock only.</summary>
    private async Task CheckNotchBatteriesAsync()
    {
        var localization = ExtensionLocalization();
        var log = Mock.Of<ILogService>();
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(new UserPreferencesSnapshot(
            new Dictionary<string, object>
            {
                [NotchExtension.PreferenceKey] = true,
                [NotchExtension.ModulesPreferenceKey] = (int)(NotchModule.Clock | NotchModule.Batteries),
                [NotchExtension.SysInfoPreferenceKey] = true
            }, log));
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.ModulesPreferenceKey, It.IsAny<int>()))
            .ReturnsAsync(OperationResult.Succeeded());
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.PinnedModulesPreferenceKey, It.IsAny<int>()))
            .ReturnsAsync(OperationResult.Succeeded());

        IReadOnlyList<DesktopBattery> batteryReadings =
            [new(null, 100, IsCharging: true), new("Headset", 54), new("Travel Mouse", 0), new("Keyboard", null)];
        var batteryReads = 0;
        var readings = new NotchReadings(log, _ => throw new InvalidOperationException("System counters are disabled."),
            _ =>
            {
                batteryReads++;
                return Task.FromResult(batteryReadings);
            });
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            extension => window = new NotchWindow(extension, localization, log, Mock.Of<IDesktopMediaSession>(), readings));
        await notch.LoadAsync();
        var shown = window ?? throw new InvalidOperationException("The battery test did not create the Notch.");
        var root = (Grid)shown.Content;
        await LoadedAsync(root);
        var compactBatteries = (ItemsControl)root.FindName("CompactBatteries");
        var baselineSize = shown.AppWindow.Size;
        await WaitForNotchAsync(() => batteryReads == 1);
        Require(compactBatteries.Visibility == Visibility.Collapsed && compactBatteries.Items.Count == 0 &&
            ((TextBlock)root.FindName("CompactTime")).Visibility == Visibility.Visible &&
            ((Border)root.FindName("CompactHandle")).Visibility == Visibility.Collapsed,
            "The default compact bar did not show only the clock.");

        StackPanel[] Badges() => Descendants<StackPanel>(compactBatteries).Where(item => item.Name == "CompactBatteryBadge").ToArray();
        await notch.SetPinnedAsync(NotchModule.Batteries, true);
        await WaitForNotchAsync(() => Badges() is [var pc, _, var mouse] &&
            pc.FindName("CompactBatteryCharging") is FrameworkElement { ActualWidth: > 0 } && mouse.ActualWidth > 0);
        Require(batteryReads == 1, "Pinning batteries restarted their background reader.");
        var badges = Badges();
        var pcBadge = badges[0];
        var pcIcon = (Border)pcBadge.FindName("CompactBatteryIcon");
        var pcLabel = (TextBlock)pcBadge.FindName("CompactBatteryLabel");
        var pcLevel = (TextBlock)pcBadge.FindName("CompactBattery");
        var charging = (FrameworkElement)pcBadge.FindName("CompactBatteryCharging");
        var labelOrigin = pcLabel.TransformToVisual(pcBadge).TransformPoint(default);
        var levelOrigin = pcLevel.TransformToVisual(pcBadge).TransformPoint(default);
        var chargingOrigin = charging.TransformToVisual(pcBadge).TransformPoint(default);
        Require(pcLabel.Text == "Battery" && pcLevel.Text == "100%" && charging.Visibility == Visibility.Visible &&
            labelOrigin.X >= pcIcon.ActualWidth && labelOrigin.X == levelOrigin.X &&
            labelOrigin.Y + pcLabel.ActualHeight <= levelOrigin.Y &&
            chargingOrigin.X >= levelOrigin.X + pcLevel.ActualWidth &&
            chargingOrigin.X + charging.ActualWidth <= pcBadge.ActualWidth && pcBadge.ActualHeight <= root.ActualHeight,
            "The PC battery label, percentage or charging icon overlapped.");
        Require(badges.Skip(1).Select(badge => ((TextBlock)badge.FindName("CompactBatteryLabel")).Text)
                .SequenceEqual(["Headset", "Travel Mouse"]) &&
            badges.Skip(1).Select(badge => ((TextBlock)badge.FindName("CompactBattery")).Text).SequenceEqual(["54%", "0%"]) &&
            badges.Skip(1).All(badge => ((FrameworkElement)badge.FindName("CompactBatteryCharging")).Visibility == Visibility.Collapsed) &&
            ToolTipService.GetToolTip(badges[2]) is "Travel Mouse · 0%" &&
            shown.AppWindow.Size.Width > baselineSize.Width &&
            badges.All(badge => badge.TransformToVisual(root).TransformPoint(default).X + badge.ActualWidth <= root.ActualWidth),
            "The compact bar did not fit and label all Bluetooth batteries with known percentages.");
        await CaptureAsync(root, "notch-compact-batteries.png");

        await notch.SetModuleAsync(NotchModule.Batteries, false);
        Require(compactBatteries.Visibility == Visibility.Collapsed && compactBatteries.Items.Count == 0 &&
            shown.AppWindow.Size.Width == baselineSize.Width, "Disabling batteries did not restore the compact bar.");
        batteryReadings = [new("Headset", 54)];
        await notch.SetModuleAsync(NotchModule.Batteries, true);
        await WaitForNotchAsync(() => Badges() is [var headset] &&
            headset.FindName("CompactBattery") is TextBlock { Text: "54%" });
        Require(Badges()[0].FindName("CompactBatteryLabel") is TextBlock { Text: "Headset" },
            "The Bluetooth battery disappeared when there was no PC battery.");
        await notch.SetPinnedAsync(NotchModule.Batteries, false);
        Require(compactBatteries.Visibility == Visibility.Collapsed && compactBatteries.Items.Count == 0 &&
            notch.Modules.HasFlag(NotchModule.Batteries), "Unpinning batteries changed the enabled module.");
        Console.WriteLine("PASS: the compact Notch defaults to clock only and shows all available battery levels when pinned.");
    }

    /// <summary>Checks the compact layout and reordering against a saved profile and native readouts.</summary>
    private async Task CheckCompactOrderAsync()
    {
        const NotchModule modules = NotchModule.Cpu | NotchModule.Gpu | NotchModule.Memory | NotchModule.Traffic | NotchModule.Clock;
        var localization = ExtensionLocalization();
        var log = Mock.Of<ILogService>();
        var preferences = new Mock<IUserPreferencesService>();
        var saved = new Dictionary<string, object>
        {
            [NotchExtension.PreferenceKey] = true,
            [NotchExtension.ModulesPreferenceKey] = (int)modules,
            [NotchExtension.PinnedModulesPreferenceKey] = (int)modules
        };
        preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(() => new UserPreferencesSnapshot(saved, log));
        var failNextSave = true;
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.CompactOrderPreferenceKey, It.IsAny<NotchModule[]>()))
            .ReturnsAsync((string key, NotchModule[] order) =>
            {
                if (failNextSave)
                {
                    failNextSave = false;
                    return OperationResult.Failed("Order save failed.");
                }
                saved[key] = JsonSerializer.SerializeToElement(order);
                return OperationResult.Succeeded();
            });

        var monitor = new Mock<ISystemMonitorService>();
        monitor.Setup(m => m.StopAsync()).Returns(Task.CompletedTask);
        monitor.Setup(m => m.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SystemMonitorSnapshot(
            DateTimeOffset.Now, 6, 100, 36, 708, 0, [], [], [])
        {
            HardwareMetrics = [new(SystemMonitorMetricKind.GpuUsage, "Sample GPU", 1)]
        });
        var readings = new NotchReadings(log, _ => monitor.Object,
            _ => throw new InvalidOperationException("Batteries are disabled."));
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            extension => window = new NotchWindow(extension, localization, log, Mock.Of<IDesktopMediaSession>(), readings));
        await notch.LoadAsync();
        var root = (Grid)(window ?? throw new InvalidOperationException("The order test did not create the Notch.")).Content;
        await LoadedAsync(root);
        var readouts = (StackPanel)root.FindName("CompactReadouts");
        string[] VisibleOrder() => readouts.Children.OfType<FrameworkElement>()
            .Where(item => item.Visibility == Visibility.Visible).Select(item => item.Name).ToArray();
        string[] original = ["CompactCpu", "CompactGpu", "CompactMemory", "CompactTraffic", "CompactTime"];
        Require(VisibleOrder().SequenceEqual(original), "The default compact order did not match the metrics and clock layout.");
        var cpu = (TextBlock)root.FindName("CpuValue");
        await WaitForNotchAsync(() => cpu.Text == "6%" && cpu.ActualWidth > 0);
        await CaptureAsync(root, "notch-compact-order.png");

        (NotchModule, double)[] targets = [(NotchModule.Cpu, 25), (NotchModule.Gpu, 100), (NotchModule.Memory, 175),
            (NotchModule.Traffic, 250), (NotchModule.Clock, 325)];
        var quick = new NotchCompactDrag(NotchModule.Cpu, new(25, 15), targets);
        Require(!quick.Move(new(28, 16)) && quick.CanClick && !quick.Move(new(36, 15)) && !quick.Hold(),
            "A quick click or movement before holding was treated as a reorder.");
        var drag = new NotchCompactDrag(NotchModule.Gpu, new(100, 15), targets);
        Require(drag.Hold() && !drag.CanClick && !drag.Move(new(120, 15)) && !drag.Move(new(90, 15)) &&
            drag.Move(new(20, 15)) && drag.Target == NotchModule.Cpu &&
            drag.Move(new(260, 15)) && drag.Target == NotchModule.Traffic,
            "The held readout did not follow the targets crossed in both directions.");

        await notch.MoveCompactSlotAsync(drag.Source, drag.Target);
        Require(notch.HasError && VisibleOrder().SequenceEqual(original) && !saved.ContainsKey(NotchExtension.CompactOrderPreferenceKey),
            "A failed order save changed the confirmed layout.");
        await notch.MoveCompactSlotAsync(drag.Source, drag.Target);
        Require(VisibleOrder().SequenceEqual(["CompactCpu", "CompactMemory", "CompactTraffic", "CompactGpu", "CompactTime"]) &&
            notch.PinnedModules == modules, "Saving the drop did not move the live readout independently of the pins.");
        saved[NotchExtension.PreferenceKey] = false;
        using var restored = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            _ => throw new InvalidOperationException("The off Notch must not create a window."));
        await restored.LoadAsync();
        Require(restored.CompactOrder.SequenceEqual(notch.CompactOrder), "The compact order was lost after reload.");

        const NotchModule audioAndClock = NotchModule.AudioVisualizer | NotchModule.Clock;
        saved[NotchExtension.ModulesPreferenceKey] = (int)audioAndClock;
        saved[NotchExtension.PinnedModulesPreferenceKey] = (int)audioAndClock;
        saved[NotchExtension.CompactOrderPreferenceKey] = JsonSerializer.SerializeToElement(new[]
        {
            NotchModule.AudioVisualizer, NotchModule.Calendar, NotchModule.Clock, NotchModule.AudioVisualizer
        });
        using var audioRestored = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            _ => throw new InvalidOperationException("The off Notch must not create a window."));
        await audioRestored.LoadAsync();
        Require(audioRestored.Modules == audioAndClock && audioRestored.PinnedModules == audioAndClock &&
            audioRestored.CompactOrder.SequenceEqual([NotchModule.AudioVisualizer, NotchModule.Clock, NotchModule.Cpu,
                NotchModule.Gpu, NotchModule.Memory, NotchModule.Traffic, NotchModule.Batteries, NotchModule.FrameRate]),
            "The saved visualizer position and pin did not survive compact-order normalization.");
        Console.WriteLine("PASS: compact metrics layout, drag movement, failed order save and restored order.");
    }

    /// <summary>Exercises the native overlay and detail switches with isolated preferences and readings.</summary>
    private async Task CheckNotchAsync(Grid shell)
    {
        var localization = ExtensionLocalization();
        Mock.Get(localization).SetupGet(service => service.CurrentLanguage).Returns("en");
        var log = Mock.Of<ILogService>();
        var preferences = new Mock<IUserPreferencesService>();
        bool enabled = false;
        bool failNextSave = false;
        bool failNextModuleSave = false;
        bool failNextPinSave = false;
        bool failNextPlacementSave = false;
        bool failNextContentSave = false;
        var placement = NotchPlacement.Default;
        int modules = (int)NotchExtension.DefaultModules;
        bool? sysInfoEnabled = null;
        int pinnedModules = (int)NotchModule.Clock;
        var file = Path.Combine(Path.GetTempPath(), "Notch Sample.lnk");
        var folder = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var savedShortcuts = new NotchShortcut[] { new(file, IsFolder: false), new(folder, IsFolder: true) };
        var content = NotchContent.Empty with { Shortcuts = savedShortcuts };
        var writes = new List<bool>();
        preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(() => new UserPreferencesSnapshot(
            new Dictionary<string, object>
            {
                [NotchExtension.PreferenceKey] = enabled,
                [NotchExtension.ModulesPreferenceKey] = modules,
                [NotchExtension.SysInfoPreferenceKey] = JsonSerializer.SerializeToElement(sysInfoEnabled),
                [NotchExtension.PinnedModulesPreferenceKey] = pinnedModules,
                [NotchExtension.PlacementPreferenceKey] = placement,
                [NotchExtension.ContentPreferenceKey] = JsonSerializer.SerializeToElement(content)
            }, log));
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.PreferenceKey, It.IsAny<bool>())).ReturnsAsync((string _, bool value) =>
        {
            writes.Add(value);
            if (failNextSave) { failNextSave = false; return OperationResult.Failed("Preference write failed."); }
            enabled = value;
            return OperationResult.Succeeded();
        });
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.ModulesPreferenceKey, It.IsAny<int>())).ReturnsAsync((string _, int value) =>
        {
            if (failNextModuleSave) { failNextModuleSave = false; return OperationResult.Failed("Module write failed."); }
            modules = value;
            return OperationResult.Succeeded();
        });
        preferences.Setup(p => p.UpdatePreferencesAsync(It.IsAny<IReadOnlyDictionary<string, object>>()))
            .ReturnsAsync((IReadOnlyDictionary<string, object> changes) =>
            {
                modules = (int)changes[NotchExtension.ModulesPreferenceKey];
                sysInfoEnabled = (bool)changes[NotchExtension.SysInfoPreferenceKey];
                return OperationResult.Succeeded();
            });
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.PinnedModulesPreferenceKey, It.IsAny<int>())).ReturnsAsync((string _, int value) =>
        {
            if (failNextPinSave) { failNextPinSave = false; return OperationResult.Failed("Pin write failed."); }
            pinnedModules = value;
            return OperationResult.Succeeded();
        });
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.ContentPreferenceKey, It.IsAny<NotchContent>()))
            .ReturnsAsync((string _, NotchContent value) =>
            {
                if (failNextContentSave) { failNextContentSave = false; return OperationResult.Failed("Content write failed."); }
                content = value;
                return OperationResult.Succeeded();
            });
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.PlacementPreferenceKey, It.IsAny<NotchPlacement>()))
            .ReturnsAsync((string _, NotchPlacement value) =>
            {
                if (failNextPlacementSave) { failNextPlacementSave = false; return OperationResult.Failed("Placement write failed."); }
                placement = value;
                return OperationResult.Succeeded();
            });
        preferences.Setup(p => p.GetPreferenceAsync(SystemMonitorOptions.PreferenceKey, It.IsAny<SystemMonitorOptions>())).ReturnsAsync(new SystemMonitorOptions());
        var startup = Mock.Of<ISystemMonitorStartupService>(s => s.ApplyAsync(It.IsAny<SystemMonitorOptions>()) == Task.FromResult(OperationResult.Succeeded()));
        var dispatcher = new ExtensionTestDispatcher();
        using var extensions = new ExtensionsViewModel(preferences.Object, localization, Mock.Of<ISystemMonitorWidgetHost>(), dispatcher, startup);
        using var catalog = new UtilitiesCatalogViewModel(localization, dispatcher);
        var preview = new PreviewSettingsViewModel(localization, () => Task.FromResult(new PreviewSettingsSnapshot(false, false)),
            (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        var media = new Mock<IDesktopMediaSession>();
        using var cover = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, cover);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2, 2, 96, 96,
            [57, 54, 192, 255, 98, 77, 225, 255, 137, 89, 88, 255, 172, 107, 130, 255]);
        await encoder.FlushAsync();
        cover.Seek(0);
        var playback = new DesktopMediaState("Sample track", "Sample artist", RandomAccessStreamReference.CreateFromStream(cover),
            true, true, false, true, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(100))
        {
            TrackRevision = 7,
            SeekRange = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(85))
        };
        var seeks = new List<(long Revision, TimeSpan Position)>();
        var hasMediaSession = false;
        media.Setup(m => m.ReadAsync()).ReturnsAsync(() => hasMediaSession ? playback : null);
        media.Setup(m => m.SeekAsync(It.IsAny<DesktopMediaState>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync((DesktopMediaState shown, TimeSpan position) =>
            {
                seeks.Add((shown.TrackRevision, position));
                playback = playback with { Position = position };
                return true;
            });
        media.Setup(m => m.ControlAsync(DesktopMediaCommand.PlayPause)).ReturnsAsync(() =>
        {
            playback = playback with { IsPlaying = !playback.IsPlaying };
            return true;
        });
        var monitor = new Mock<ISystemMonitorService>();
        monitor.Setup(m => m.StopAsync()).Returns(Task.CompletedTask);
        var gpuSamples = 0;
        Func<bool>? graphicsEnabled = null;
        monitor.Setup(m => m.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var sample = Interlocked.Increment(ref gpuSamples);
            return new SystemMonitorSnapshot(DateTimeOffset.Now, 42, 16UL << 30, 6UL << 30, 4 << 20, 2 << 10, [], [], [])
            {
                HardwareMetrics = graphicsEnabled?.Invoke() != true ? [] : [new(SystemMonitorMetricKind.GpuUsage, "GPU Core · Sample GPU", 68),
                    .. Enumerable.Range(1, 6).Select(index => new SystemMonitorMetric(SystemMonitorMetricKind.GpuUsage,
                        $"GPU Engine {index} · Sample GPU", 8 * index + (sample % 2 == 0 ? 5 : 0)))]
            };
        });
        var graphicsRequests = new List<bool>();
        var dailyTrafficRequests = 0;
        var weatherQueries = new List<DesktopWeatherLocation>();
        var photonQueries = new List<string>();
        var firstPlace = new DesktopWeatherLocation("Rome, New York, United States", 43.21, -75.46);
        var selectedPlace = new DesktopWeatherLocation("Rome, Lazio, Italy", 41.9, 12.5);
        var readings = new NotchReadings(log, useGraphics =>
        {
            graphicsEnabled = useGraphics;
            graphicsRequests.Add(useGraphics());
            return monitor.Object;
        }, _ => Task.FromResult<IReadOnlyList<DesktopBattery>>([new(null, 100, IsCharging: true), new("Headset", 54)]),
        (place, _) =>
        {
            weatherQueries.Add(place);
            var firstHour = new DateTime(2026, 11, 23, 19, 0, 0);
            var firstDay = DateOnly.FromDateTime(firstHour);
            var hours = Enumerable.Range(0, 4).Select(hour => new DesktopWeatherHour(firstHour.AddHours(hour), 18 - hour, 0, false)).ToArray();
            var days = Enumerable.Range(0, 7).Select(day => new DesktopWeatherDay(firstDay.AddDays(day), 21 - day, 12 - day, 61)).ToArray();
            return Task.FromResult(new DesktopWeatherReading(place.Label, 32, 0, true) { Hours = hours, Days = days });
        }, () => new(true, "SampleCall"), () => 0.5, readToday: _ =>
        {
            dailyTrafficRequests++;
            return Task.FromResult(new ConnectionDailyUsage(DateOnly.FromDateTime(DateTime.Today), 2L << 20, 6L << 20, 1, 1));
        });
        var opened = new List<string>();
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
            extension => window = new NotchWindow(extension, localization, log, media.Object, readings, openShortcut: opened.Add,
                shareFile: _ => Task.CompletedTask),
            (query, _) =>
            {
                photonQueries.Add(query);
                return Task.FromResult<IReadOnlyList<DesktopWeatherLocation>>([firstPlace, selectedPlace]);
            });
        var overview = new UtilitiesPage(catalog, extensions, preview, notch);
        var settingsHost = new Frame();
        shell.Children.Add(overview);
        try
        {
            await LoadedAsync(overview);
            var toggle = (ToggleSwitch)overview.FindName("NotchToggle");
            static void Toggle(ToggleSwitch control) => ((IToggleProvider)new ToggleSwitchAutomationPeer(control).GetPattern(PatternInterface.Toggle)).Toggle();
            Require(toggle.IsEnabled && !toggle.IsOn && window is null, "The notch should be off on the first launch.");
            failNextSave = true;
            Toggle(toggle);
            await notch.FlushAsync();
            Require(!toggle.IsOn && !notch.IsRunning && notch.HasError, "A failed save left the notch running or enabled.");

            Toggle(toggle);
            await notch.FlushAsync();
            var shown = window ?? throw new InvalidOperationException("The switch did not create a notch window.");
            var root = (Grid)shown.Content;
            await LoadedAsync(root);
            var compact = shown.AppWindow.Size;
            var display = DisplayArea.Primary;
            var work = display.WorkArea;
            var position = shown.AppWindow.Position;
            Require(shown.AppWindow.IsVisible && !shown.AppWindow.IsShownInSwitchers && enabled && toggle.IsOn,
                "The enabled notch was not shown as a desktop overlay.");
            Require(Math.Abs(position.X - display.OuterBounds.X - work.X - (work.Width - compact.Width) / 2) <= 1 &&
                position.Y == display.OuterBounds.Y + work.Y && compact.Height == (int)Math.Round(30 * root.XamlRoot.RasterizationScale) &&
                root.ActualHeight <= 31,
                $"The compact notch was not centered at the top of the primary display. Window {position.X},{position.Y} " +
                $"at {compact.Width}×{compact.Height}, display origin {display.OuterBounds.X},{display.OuterBounds.Y}, " +
                $"work area {work.X},{work.Y} at {work.Width}×{work.Height}, content height {root.ActualHeight:0.##}.");
            var compactTime = (TextBlock)root.FindName("CompactTime");
            Require(compactTime.Visibility == Visibility.Visible &&
                DateTime.TryParse(compactTime.Text, CultureInfo.CurrentUICulture, DateTimeStyles.None, out _),
                "The enabled clock did not show the local time in the compact notch.");
            await CaptureAsync(root, "notch-compact.png");
            CheckNotchOutline(shown);
            var grip = (Button)root.FindName("Grip");
            var surface = Descendants<Grid>(grip).Single(grid => grid.Name == "GripSurface");
            var gripContent = Descendants<ContentPresenter>(grip).Single(presenter => presenter.Name == "GripContent");
            foreach (var state in new[] { "PointerOver", "Normal", "PointerOver", "Normal" })
            {
                Require(VisualStateManager.GoToState(grip, state, useTransitions: true), $"The compact button did not support {state}.");
                Require(surface.Background is SolidColorBrush brush && brush.Color.A == 0 && gripContent.Opacity == 1,
                    "Entering or leaving the compact Notch changed its appearance.");
            }

            static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            void Select(string name)
            {
                var tab = (RadioButton)root.FindName(name);
                ((ISelectionItemProvider)new RadioButtonAutomationPeer(tab).GetPattern(PatternInterface.SelectionItem)).Select();
            }
            var panel = (Grid)root.FindName("Panel");
            var track = (TextBlock)root.FindName("TrackTitle");
            var playbackButton = (Button)root.FindName("PlaybackButton");
            var playbackIcon = (SymbolIcon)root.FindName("PlaybackIcon");
            Invoke(grip);
            ContentPresenter? playbackFace = null;
            await WaitForNotchAsync(() => panel.Visibility == Visibility.Visible && !playbackButton.IsEnabled &&
                track.Text == "No active media session" &&
                (playbackFace = Descendants<ContentPresenter>(playbackButton).FirstOrDefault()) is { ActualWidth: > 0 });
            Require(playbackIcon.Symbol == Symbol.Play && playbackFace is not null &&
                playbackFace.Background is SolidColorBrush { Color.R: 27, Color.G: 28, Color.B: 32 } &&
                playbackFace.Foreground is SolidColorBrush { Color.R: 130, Color.G: 132, Color.B: 138 },
                "The disabled play button should show a readable muted glyph on the dark media controls.");
            await CaptureAsync(root, "notch-media-empty.png");
            hasMediaSession = true;
            await WaitForNotchAsync(() => panel.Visibility == Visibility.Visible && track.Text == "Sample track");
            Require(playbackButton.IsEnabled && playbackIcon.Symbol == Symbol.Pause,
                "An available playback session should activate the button and show the pause icon.");
            Require(shown.AppWindow.Size.Width > compact.Width && shown.AppWindow.Size.Height > compact.Height,
                "Expanding did not enlarge the native notch window.");
            foreach (var (controlName, labelKey) in new[]
            {
                ("FocusTab", "Notch_FocusTab"), ("FocusDot", "Notch_FocusTab"),
                ("SettingsButton", "Notch_OpenOptimizer"), ("CollapseButton", "Notch_Collapse")
            })
            {
                var control = (Control)root.FindName(controlName);
                Require(FrameworkElementAutomationPeer.CreatePeerForElement(control).GetName() == localization.GetString(labelKey) &&
                    ToolTipService.GetToolTip(control) is null,
                    $"{controlName} should expose its localized name to assistive technology without showing a tooltip.");
            }
            var seek = (Slider)root.FindName("TrackSeek");
            var elapsed = (TextBlock)root.FindName("PositionText");
            var artwork = (Image)root.FindName("Artwork");
            var waveFrame = (Border)root.FindName("MediaWaveFrame");
            var mediaWave = (StackPanel)root.FindName("MediaWave");
            await WaitForNotchAsync(() => artwork.Source is not null && waveFrame.Visibility == Visibility.Visible &&
                mediaWave.Children.OfType<Border>().Any(bar => bar.Height > 2));
            Require(seek.Visibility == Visibility.Visible && seek.Minimum == 5 && seek.Maximum == 85 && seek.Value == 25 &&
                seeks.Count == 0, "The initial player update did not honor the reported seek limits.");
            var scrub = (IRangeValueProvider)new SliderAutomationPeer(seek).GetPattern(PatternInterface.RangeValue);
            scrub.SetValue(40);
            scrub.SetValue(63);
            await WaitForNotchAsync(() => seeks.Count > 0);
            Require(seeks.SequenceEqual([(7L, TimeSpan.FromSeconds(63))]) && elapsed.Text == "01:03",
                $"Scrubbing did not send the final position for the displayed track. Requests: {string.Join(", ", seeks)}. Elapsed: {elapsed.Text}.");
            await CaptureAsync(root, "notch-media.png");
            Invoke((Button)root.FindName("PlaybackButton"));
            await WaitForNotchAsync(() => waveFrame.Visibility == Visibility.Collapsed);
            media.Verify(m => m.ControlAsync(DesktopMediaCommand.PlayPause), Times.Once);

            var viewport = (Grid)root.FindName("Viewport");
            var rail = (StackPanel)root.FindName("PageRail");
            var railOrigin = rail.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point());
            Require(rail.ActualWidth > 0 && railOrigin.X >= viewport.ActualWidth &&
                Math.Abs(railOrigin.Y + (rail.ActualHeight - viewport.ActualHeight) / 2) < 1,
                "The navigation dots were not centered on the right side of the content.");
            Select("FocusDot");
            await WaitForNotchAsync(() => ((Grid)root.FindName("FocusPanel")).Visibility == Visibility.Visible);
            Require(((RadioButton)root.FindName("FocusTab")).IsChecked == true, "The focus dot did not synchronize with the tab.");
            var remaining = (TextBlock)root.FindName("Remaining");
            Require(remaining.Text == "25:00", "The focus timer did not start at 25 minutes.");
            Invoke((Button)root.FindName("TimerToggle"));
            Invoke((Button)root.FindName("CollapseButton"));
            await WaitForNotchAsync(() => shown.AppWindow.Size.Height == compact.Height && remaining.Text != "25:00");
            Require(DateTime.TryParse(compactTime.Text, CultureInfo.CurrentUICulture, DateTimeStyles.None, out _),
                "An unpinned countdown replaced the clock in the compact Notch.");
            await notch.SetPinnedAsync(NotchModule.Focus, true);
            Require(compactTime.Visibility == Visibility.Visible && compactTime.Text == remaining.Text,
                "The pinned countdown stopped or disappeared when the Notch collapsed.");
            Invoke((Button)root.FindName("Grip"));
            await WaitForNotchAsync(() => panel.Visibility == Visibility.Visible);
            await CaptureAsync(root, "notch-focus.png");
            ((ComboBox)root.FindName("Phase")).SelectedIndex = (int)FocusPhase.Custom;
            var duration = (NumberBox)root.FindName("TimerMinutes");
            duration.Value = 12;
            Require(duration.Visibility == Visibility.Visible && remaining.Text == "12:00", "The custom timer did not use the edited duration.");

            Select("CalendarDot");
            var calendar = (CalendarView)root.FindName("CalendarPanel");
            await WaitForNotchAsync(() => calendar.Visibility == Visibility.Visible && calendar.ActualHeight > 220);
            Require(((RadioButton)root.FindName("CalendarTab")).IsChecked == true,
                "The calendar dot did not synchronize with the tab.");
            await CaptureAsync(root, "notch-calendar.png");
            Require(shown.HandlePageWheel(60) && !((RadioButton)root.FindName("FocusTab")).IsChecked.GetValueOrDefault(),
                "A partial touchpad movement changed the page.");
            Require(shown.HandlePageWheel(60), "The vertical wheel step was not handled.");
            await WaitForNotchAsync(() => ((RadioButton)root.FindName("FocusTab")).IsChecked == true);
            Select("CalendarTab");

            shell.Children.Add(settingsHost);
            settingsHost.Navigate(typeof(NotchSettingsPage), notch);
            var details = (NotchSettingsPage)settingsHost.Content;
            await LoadedAsync(details);
            ToggleSwitch ModuleSwitch(NotchModule module) => Descendants<ToggleSwitch>(details)
                .Single(control => control.Tag is NotchModuleOption option && option.Module == module);
            ToggleButton CompactPin(NotchModule module) => Descendants<ToggleButton>(details)
                .Single(control => control.Name == "CompactPin" && control.Tag is NotchModuleOption option && option.Module == module);
            static void TogglePin(ToggleButton button) =>
                ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();
            var sysInfo = (ToggleSwitch)details.FindName("SysInfoToggle");
            var weatherSwitch = ModuleSwitch(NotchModule.Weather);
            var overviewSwitch = (ToggleSwitch)details.FindName("OverviewToggle");
            var overviewTab = (RadioButton)root.FindName("OverviewTab");
            var moduleCount = notch.GeneralModuleOptions.Count + 2;
            Require(!sysInfo.IsOn && !overviewSwitch.IsOn && overviewTab.Visibility == Visibility.Collapsed &&
                ModuleSwitch(NotchModule.Calendar).IsOn && notch.ModuleCountText == $"4/{moduleCount} modules enabled",
                "The details did not show the confirmed default modules.");
            Toggle(overviewSwitch);
            await notch.FlushAsync();
            Require(overviewSwitch.IsOn && content.OverviewEnabled == true && !weatherSwitch.IsOn &&
                overviewTab.Visibility == Visibility.Visible, "Overview should be available independently of Weather.");
            Require(CompactPin(NotchModule.Clock).IsChecked == true &&
                CompactPin(NotchModule.Calendar).Visibility == Visibility.Collapsed,
                "Compact pins did not reflect the saved clock and available modules.");
            failNextModuleSave = true;
            Toggle(weatherSwitch);
            await notch.FlushAsync();
            Require(!weatherSwitch.IsOn && notch.HasError && modules == (int)NotchExtension.DefaultModules,
                "A failed module save changed the confirmed selection.");
            foreach (var option in notch.GeneralModuleOptions.Where(option => !option.IsEnabled).ToArray())
            {
                Toggle(ModuleSwitch(option.Module));
                await notch.FlushAsync();
            }
            var batterySwitch = ModuleSwitch(NotchModule.Batteries);
            var batteryPin = CompactPin(NotchModule.Batteries);
            Require(!sysInfo.IsOn && batterySwitch.IsOn && batteryPin.IsEnabled && batteryPin.IsChecked == false &&
                ((RadioButton)root.FindName("BatteriesTab")).Visibility == Visibility.Visible,
                "The main module list should enable the battery view independently of Sys info and the compact pin.");
            failNextPinSave = true;
            TogglePin(batteryPin);
            await notch.FlushAsync();
            Require(batteryPin.IsChecked == false && notch.HasError && !notch.PinnedModules.HasFlag(NotchModule.Batteries),
                "A failed pin save changed the confirmed pin.");
            TogglePin(batteryPin);
            await notch.FlushAsync();
            Require(batteryPin.IsChecked == true && batterySwitch.IsOn && ((NotchModule)pinnedModules).HasFlag(NotchModule.Batteries),
                "The battery pin button did not save a compact pin independently.");
            Toggle(sysInfo);
            await notch.FlushAsync();
            var dashboardTab = (RadioButton)root.FindName("DashboardTab");
            Require(sysInfo.IsOn && sysInfoEnabled == true && dashboardTab.Visibility == Visibility.Visible &&
                AutomationProperties.GetName(dashboardTab) == "Sys info", "The Sys info switch did not expose the readings in Notch.");
            Require(notch.ModuleCountText == $"{moduleCount}/{moduleCount} modules enabled" &&
                graphicsRequests.SequenceEqual([true]) && dailyTrafficRequests == 1,
                "Enabling Sys info did not start the selected background readers and update the overview.");
            Invoke((Button)details.FindName("OpenSysInfo"));
            var sysPage = (NotchSysInfoPage)settingsHost.Content;
            await LoadedAsync(sysPage);
            var readingSwitches = Descendants<ToggleSwitch>(sysPage).ToArray();
            Require(readingSwitches.All(control => control.IsOn) && readingSwitches
                .Select(control => ((NotchModuleOption)control.Tag).Module).Order()
                .SequenceEqual(Enum.GetValues<NotchModule>().Where(module => (module & NotchExtension.DashboardModules) != 0)),
                "The Sys info arrow did not open the selected dashboard readings.");
            var uptimeSwitch = readingSwitches.Single(control => control.Tag is NotchModuleOption { Module: NotchModule.Uptime });
            Toggle(uptimeSwitch);
            await notch.FlushAsync();
            Require(!uptimeSwitch.IsOn && !notch.Modules.HasFlag(NotchModule.Uptime) && notch.IsSysInfoEnabled,
                "Editing a single reading incorrectly changed the Sys info switch.");
            Toggle(uptimeSwitch);
            await notch.FlushAsync();
            await CaptureAsync(sysPage, "notch-sys-info-settings.png");
            settingsHost.GoBack();
            details = (NotchSettingsPage)settingsHost.Content;
            await LoadedAsync(details);
            sysInfo = (ToggleSwitch)details.FindName("SysInfoToggle");
            var selectedReadings = notch.Modules & NotchExtension.DashboardModules;
            Toggle(sysInfo);
            await notch.FlushAsync();
            Require(sysInfoEnabled == false && dashboardTab.Visibility == Visibility.Collapsed && graphicsEnabled?.Invoke() == false &&
                (notch.Modules & NotchExtension.DashboardModules) == selectedReadings && notch.PinnedModules.HasFlag(NotchModule.Batteries),
                "Disabling Sys info did not hide the readings while retaining choices and pins.");
            await WaitForNotchAsync(() => monitor.Invocations.Count(call => call.Method.Name == nameof(ISystemMonitorService.StopAsync)) == 1);
            Toggle(sysInfo);
            await notch.FlushAsync();
            await CaptureAsync(details, "notch-settings.png");

            Select("WeatherTab");
            var conditions = (TextBlock)root.FindName("WeatherCondition");
            Require(conditions.Text == "Choose a city in Notch settings." && weatherQueries.Count == 0,
                "The weather view queried the provider before a city was configured.");
            var cityBox = (AutoSuggestBox)details.FindName("WeatherCityBox");
            cityBox.Text = "R";
            Invoke((Button)details.FindName("SaveWeatherCity"));
            await notch.FlushAsync();
            Require(notch.WeatherLocation is null && weatherQueries.Count == 0 &&
                ((TextBlock)details.FindName("WeatherSearchStatus")).Text == "Choose a location from the suggestions.",
                "Raw weather input was accepted without a selection.");

            cityBox.ApplyTemplate();
            var input = Descendants<TextBox>(cityBox).Single(box => box.Name == "TextBox");
            input.Focus(FocusState.Programmatic);
            input.Text = "Rome";
            ListViewItem? selectedItem = null;
            await WaitForNotchAsync(() =>
            {
                var list = VisualTreeHelper.GetOpenPopupsForXamlRoot(cityBox.XamlRoot)
                    .SelectMany(popup => Tree<ListView>(popup.Child)).FirstOrDefault(control => control.Name == "SuggestionsList");
                selectedItem = list?.ContainerFromItem(selectedPlace) as ListViewItem;
                return cityBox.IsSuggestionListOpen && selectedItem is not null;
            });
            Require(photonQueries.SequenceEqual(["Rome"]), "The location box did not issue a debounced Photon query.");
            var resultPeer = new ListViewItemAutomationPeer(selectedItem!);
            var resultAction = resultPeer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            Require(resultAction is not null, "The native location suggestion could not be invoked.");
            resultAction!.Invoke();
            await notch.FlushAsync();
            if (panel.Visibility != Visibility.Visible) Invoke((Button)root.FindName("Grip"));
            await WaitForNotchAsync(() => panel.Visibility == Visibility.Visible &&
                ((TextBlock)root.FindName("WeatherTemperature")).Text == "32°C");
            Require(weatherQueries.SequenceEqual([selectedPlace]) && conditions.Text == "Clear" &&
                ((ContentControl)root.FindName("WeatherSymbol")).Content is Viewbox { Child: Canvas },
                $"The weather view did not use the chosen coordinates and Astro's vector icon. " +
                $"Places: {string.Join(", ", weatherQueries.Select(place => place.Label))}. Conditions: {conditions.Text}. " +
                $"Icon: {((ContentControl)root.FindName("WeatherSymbol")).Content?.GetType().Name}.");
            var hoursPanel = (StackPanel)root.FindName("WeatherHourly");
            var weekPanel = (StackPanel)root.FindName("WeatherWeekly");
            var hourly = (Grid)root.FindName("WeatherHours");
            var weekly = (Grid)root.FindName("WeatherDays");
            var weatherTile = (Border)root.FindName("WeatherPage");
            await WaitForNotchAsync(() => weatherTile.ActualHeight > 220 && weekly.ActualHeight > 0 &&
                weekly.TransformToVisual(weatherTile).TransformPoint(default).Y + weekly.ActualHeight <= weatherTile.ActualHeight);
            var hourOrigin = hourly.TransformToVisual(weatherTile).TransformPoint(default);
            var weekOrigin = weekly.TransformToVisual(weatherTile).TransformPoint(default);
            var firstHourCell = (StackPanel)hourly.Children[0];
            var firstDayCell = (StackPanel)weekly.Children[0];
            Require(hoursPanel.Visibility == Visibility.Visible && weekPanel.Visibility == Visibility.Visible &&
                hourly.Children.Count == 4 && weekly.Children.Count == 7 &&
                hourOrigin.X > weekOrigin.X && hourOrigin.Y + hourly.ActualHeight < weekOrigin.Y &&
                ((TextBlock)firstHourCell.Children[2]).Text == "18°" && ((TextBlock)firstDayCell.Children[2]).Text == "21° / 12°" &&
                ((ContentControl)firstHourCell.Children[1]).Content is Viewbox { Child: Canvas },
                "The dedicated weather view did not render the hourly forecast above the weekly highs and lows.");
            Require(((HyperlinkButton)root.FindName("WeatherSource")).Content is FluentIcons.WinUI.FluentIcon,
                "The weather data source should be linked through an icon.");
            await CaptureAsync(root, "notch-weather.png");

            Select("OverviewTab");
            var playerTile = (Grid)root.FindName("MediaPanel");
            var shortcutTile = (Border)root.FindName("ShortcutsCard");
            var calendarTile = (Grid)root.FindName("CalendarPage");
            var shortcutItems = (ItemsControl)root.FindName("ShortcutItems");
            await WaitForNotchAsync(() => playerTile.ActualHeight >= 120 && calendarTile.ActualHeight >= 180 &&
                track.Text == "Sample track" && Descendants<Button>(shortcutItems).Any(button => button.Tag is NotchShortcut));
            Require(new FrameworkElement[] { playerTile, shortcutTile, calendarTile, weatherTile }
                .All(tile => tile.Visibility == Visibility.Visible) &&
                Grid.GetRow(playerTile) == 0 && Grid.GetColumn(playerTile) == 0 &&
                Grid.GetRow(shortcutTile) == 0 && Grid.GetColumn(shortcutTile) == 1 &&
                Grid.GetRow(calendarTile) == 1 && Grid.GetColumn(calendarTile) == 0 &&
                Grid.GetRow(weatherTile) == 1 && Grid.GetColumn(weatherTile) == 1 &&
                ReferenceEquals(VisualTreeHelper.GetParent(calendar), root.FindName("CalendarHost")) &&
                conditions.Text == "Clear" && hoursPanel.Visibility == Visibility.Collapsed && weekPanel.Visibility == Visibility.Collapsed &&
                weatherQueries.SequenceEqual([selectedPlace]),
                "The overview did not present the four live modules in the expected grid.");
            var background = ((SolidColorBrush)root.Background).Color;
            var tileColor = ((SolidColorBrush)playerTile.Background).Color;
            Require(tileColor.R - background.R is > 0 and <= 10 &&
                tileColor.G - background.G is > 0 and <= 10 && tileColor.B - background.B is > 0 and <= 10 &&
                new[] { shortcutTile.Background, calendarTile.Background, weatherTile.Background }
                    .All(brush => brush is SolidColorBrush surface && surface.Color.Equals(tileColor)),
                "The overview did not use the same subtle surface for all four tiles.");
            var month = DateTime.Today;
            bool ContainsCurrentMonth()
            {
                var days = Descendants<CalendarViewDayItem>(calendar).Where(day =>
                    day.Date.Year == month.Year && day.Date.Month == month.Month).ToArray();
                return days.Select(day => day.Date.Day).Distinct().Count() == DateTime.DaysInMonth(month.Year, month.Month) &&
                    days.All(day =>
                    {
                        var position = day.TransformToVisual(calendar).TransformPoint(default);
                        return day.ActualWidth > 0 && day.ActualHeight > 0 && position.X >= 0 && position.Y >= 0 &&
                            position.X + day.ActualWidth <= calendar.ActualWidth &&
                            position.Y + day.ActualHeight <= calendar.ActualHeight;
                    });
            }
            await WaitForNotchAsync(ContainsCurrentMonth);
            await CaptureAsync(root, "notch-overview.png");
            Invoke((Button)root.FindName("PlaybackButton"));
            await WaitForNotchAsync(() => media.Invocations.Count(call => call.Method.Name == nameof(IDesktopMediaSession.ControlAsync)) == 2);
            media.Verify(m => m.ControlAsync(DesktopMediaCommand.PlayPause), Times.Exactly(2));
            await WaitForNotchAsync(() => waveFrame.Visibility == Visibility.Visible &&
                mediaWave.Children.OfType<Border>().Any(bar => bar.Height > 2));
            Require(weatherQueries.SequenceEqual([selectedPlace]), "Starting the overview visualizer restarted weather polling.");
            var focusTile = (Grid)root.FindName("FocusPanel");
            failNextContentSave = true;
            await notch.PlaceOverviewTileAsync(NotchView.Focus, 0);
            Require(notch.HasError && focusTile.Visibility == Visibility.Collapsed && content.OverviewTiles[0] == NotchView.Media,
                "A failed drop save changed the live Overview layout.");
            await notch.PlaceOverviewTileAsync(NotchView.Focus, 0);
            Require(!notch.HasError && playerTile.Visibility == Visibility.Collapsed && focusTile.Visibility == Visibility.Visible &&
                Grid.GetRow(focusTile) == 0 && Grid.GetColumn(focusTile) == 0 && content.OverviewTiles[0] == NotchView.Focus,
                "Dropping a new view did not replace the live tile.");
            await notch.PlaceOverviewTileAsync(NotchView.Focus, 1);
            Require(Grid.GetColumn(focusTile) == 1 && Grid.GetColumn(shortcutTile) == 0 &&
                content.OverviewTiles.Take(2).SequenceEqual([NotchView.Shortcuts, NotchView.Focus]),
                "Dropping a view already in Overview should swap the tiles.");
            await notch.PlaceOverviewTileAsync(NotchView.Focus, 0);
            await notch.PlaceOverviewTileAsync(NotchView.Media, 0);
            Toggle(ModuleSwitch(NotchModule.Shortcuts));
            await notch.FlushAsync();
            Require(shortcutTile.Visibility == Visibility.Collapsed && Grid.GetColumnSpan(playerTile) == 2,
                "The remaining overview module did not fill the top row.");
            Toggle(ModuleSwitch(NotchModule.Shortcuts));
            await notch.FlushAsync();
            Select("CalendarTab");
            Require(calendarTile.Visibility == Visibility.Visible && Grid.GetRow(calendarTile) == 0 &&
                ReferenceEquals(VisualTreeHelper.GetParent(calendar), root.FindName("CalendarHost")),
                "The dedicated calendar did not reuse the overview's native month control.");

            var requestsBeforeDashboard = graphicsRequests.Count;
            var dailyBeforeDashboard = dailyTrafficRequests;
            Require(requestsBeforeDashboard == 2 && dailyBeforeDashboard == 2, "The background readers did not resume after enabling Sys info.");
            Select("DashboardTab");
            var dashboard = (NotchDashboard)root.FindName("DashboardPanel");
            var rows = (ItemsControl)dashboard.FindName("Readings");
            NotchReadingRow Row(NotchModule module) => rows.Items.Cast<NotchReadingRow>().Single(row => row.Module == module);
            await WaitForNotchAsync(() => dashboard.Visibility == Visibility.Visible && Row(NotchModule.Cpu).Value == "42%" &&
                Row(NotchModule.Traffic).Detail == $"Today · {8d:N2} MiB");
            Require(graphicsRequests.Count == requestsBeforeDashboard && Row(NotchModule.Cpu).Value == "42%" &&
                Row(NotchModule.Memory).Value == $"{10d:0.0} / {16d:0.0} GB" &&
                Row(NotchModule.Gpu).Value == "68%" && Row(NotchModule.Traffic).Value.Contains($"{4d:0.0} MB/s") &&
                Row(NotchModule.Uptime).Value.Contains("min") && dailyTrafficRequests == dailyBeforeDashboard,
                "The dashboard did not render the enabled readings.");
            SystemMonitorMetricChart? Chart(NotchModule module) => Descendants<SystemMonitorMetricChart>(dashboard)
                .SingleOrDefault(chart => ReferenceEquals(chart.Section, Row(module).Graph));
            static int Traces(SystemMonitorMetricChart? chart) => chart is null ? 0 :
                Descendants<Microsoft.UI.Xaml.Shapes.Path>(chart).Count(trace =>
                    trace.Data is PathGeometry geometry && geometry.Figures.Any(figure => figure.Segments.OfType<BezierSegment>().Any()));
            await WaitForNotchAsync(() => Traces(Chart(NotchModule.Cpu)) == 1 && Traces(Chart(NotchModule.Memory)) == 1 &&
                Traces(Chart(NotchModule.Gpu)) == 7);
            var gpuChart = Chart(NotchModule.Gpu)!;
            Require(gpuChart.PlotOnly && AutomationProperties.GetHelpText(gpuChart).Contains("GPU Core · Sample GPU: 68%") &&
                gpuChart.ActualHeight <= 45, "The GPU card did not retain its per-sensor traces.");
            var trafficValue = Descendants<TextBlock>(dashboard).Single(block => block.Text == Row(NotchModule.Traffic).Value);
            var todayValue = Descendants<TextBlock>(dashboard).Single(block => block.Text == Row(NotchModule.Traffic).Detail);
            Require(todayValue.TransformToVisual(dashboard).TransformPoint(default).Y >
                trafficValue.TransformToVisual(dashboard).TransformPoint(default).Y,
                "Today's usage should appear below the live traffic rates.");
            double DashboardBottomGap()
            {
                var last = rows.ContainerFromIndex(rows.Items.Count - 1) as FrameworkElement;
                return last is { ActualHeight: > 0 }
                    ? root.ActualHeight - last.TransformToVisual(root).TransformPoint(default).Y - last.ActualHeight
                    : double.PositiveInfinity;
            }
            await WaitForNotchAsync(() => !dashboard.CanScroll && DashboardBottomGap() is >= 9 and <= 12.5);
            await CaptureAsync(root, "notch-dashboard.png");
            var cpuHistory = Row(NotchModule.Cpu).Graph!.Readings.Single().History;

            await notch.SetModuleAsync(NotchModule.Gpu, false);
            Require(graphicsEnabled?.Invoke() == false && graphicsRequests.Count == requestsBeforeDashboard,
                "Disabling GPU sampling replaced the active CPU reader.");
            await notch.SetModuleAsync(NotchModule.Gpu, true);
            Require(graphicsEnabled?.Invoke() == true && graphicsRequests.Count == requestsBeforeDashboard,
                "Enabling GPU sampling replaced the active CPU reader.");

            var historyBeforeCollapse = cpuHistory.Count;
            Invoke((Button)root.FindName("CollapseButton"));
            await WaitForNotchAsync(() => cpuHistory.Count > historyBeforeCollapse);
            Require(!notch.PinnedModules.HasFlag(NotchModule.Cpu) && cpuHistory.All(value => value == 42) &&
                graphicsRequests.Count == requestsBeforeDashboard && dailyTrafficRequests == dailyBeforeDashboard,
                "Collapsing the Notch stopped unpinned readings or inserted a gap in CPU history.");
            Console.WriteLine("PASS: Notch keeps background telemetry without compact pins and draws smooth curves through measured points.");
            foreach (var module in new[] { NotchModule.Cpu, NotchModule.Gpu, NotchModule.Memory, NotchModule.Traffic })
                await notch.SetPinnedAsync(module, true);
            var audioPin = CompactPin(NotchModule.AudioVisualizer);
            TogglePin(audioPin);
            await notch.FlushAsync();
            var requestsBeforeCompact = graphicsRequests.Count;
            var cpuValue = (TextBlock)root.FindName("CpuValue");
            var gpuValue = (TextBlock)root.FindName("GpuValue");
            var memoryValue = (TextBlock)root.FindName("MemoryValue");
            var traffic = (TextBlock)root.FindName("CompactTraffic");
            await WaitForNotchAsync(() => shown.AppWindow.Size.Height == compact.Height && cpuValue.Text == "42%");
            Require(shown.AppWindow.Size.Width > compact.Width && gpuValue.Text == "68%" && memoryValue.Text == $"{62.5d:0}%" &&
                traffic.Text.Contains($"{4d:0.0} MB/s") && graphicsRequests.Count == requestsBeforeCompact,
                "The collapsed widget did not show the enabled live system readings.");
            var compactBatteries = (ItemsControl)root.FindName("CompactBatteries");
            var audio = (StackPanel)root.FindName("CompactAudioVisualizer");
            var micBadge = (StackPanel)root.FindName("CompactMicBadge");
            var compactMic = (TextBlock)root.FindName("CompactMicrophone");
            var wave = (StackPanel)root.FindName("AudioWave");
            await WaitForNotchAsync(() => Descendants<TextBlock>(compactBatteries).Where(item => item.Name == "CompactBattery")
                .Select(item => item.Text).SequenceEqual(["100%", "54%"]) &&
                compactMic.Text == "SampleCall" && wave.Children.OfType<Border>().Any(bar => bar.Height > 2));
            Require(audioPin.IsChecked == true && audio.Visibility == Visibility.Visible && micBadge.Visibility == Visibility.Visible &&
                ReferenceEquals(wave.Parent, audio) && ReferenceEquals(micBadge.Parent, audio) && ReferenceEquals(audio.Parent, root.FindName("CompactReadouts")),
                "One audio pin did not place the microphone activity and waveform together.");
            await CaptureAsync(root, "notch-compact-metrics.png");
            var audioSwitch = ModuleSwitch(NotchModule.AudioVisualizer);
            Toggle(audioSwitch);
            await notch.FlushAsync();
            Require(audio.Visibility == Visibility.Collapsed && micBadge.Visibility == Visibility.Collapsed && audioPin.IsChecked == true,
                "Turning off Audio visualizer did not hide both readings while retaining the saved pin.");
            Toggle(audioSwitch);
            await notch.FlushAsync();
            await WaitForNotchAsync(() => audio.Visibility == Visibility.Visible && micBadge.Visibility == Visibility.Visible &&
                wave.Children.OfType<Border>().Any(bar => bar.Height > 2));
            Invoke((Button)root.FindName("Grip"));
            await WaitForNotchAsync(() => panel.Visibility == Visibility.Visible && dashboard.Visibility == Visibility.Visible);
            Require(ReferenceEquals(cpuHistory, Row(NotchModule.Cpu).Graph!.Readings.Single().History) &&
                cpuHistory.Count > historyBeforeCollapse,
                "Reopening the Notch did not retain the history collected in compact mode.");
            await notch.SetModuleAsync(NotchModule.Cpu, false);
            Require(!rows.Items.Cast<NotchReadingRow>().Any(row => row.Module == NotchModule.Cpu),
                "Turning off CPU did not remove its row immediately.");
            Toggle(ModuleSwitch(NotchModule.Calendar));
            await notch.FlushAsync();
            Require(((RadioButton)root.FindName("CalendarTab")).Visibility == Visibility.Collapsed &&
                overviewTab.Visibility == Visibility.Visible &&
                ((RadioButton)root.FindName("WeatherTab")).Visibility == Visibility.Visible,
                "Turning off Calendar should leave Overview and Weather available.");
            Toggle(overviewSwitch);
            await notch.FlushAsync();
            Require(!overviewSwitch.IsOn && overviewTab.Visibility == Visibility.Collapsed && content.OverviewEnabled == false,
                "Turning Overview off did not hide and save it.");
            Toggle(overviewSwitch);
            await notch.FlushAsync();
            Toggle(ModuleSwitch(NotchModule.Focus));
            await notch.FlushAsync();
            Toggle(ModuleSwitch(NotchModule.Clock));
            await notch.FlushAsync();
            Require(compactTime.Visibility == Visibility.Collapsed, "Disabled modules left text in the compact grip.");

            Select("ShortcutsDot");
            Button? OpenShortcut(NotchShortcut shortcut) => Descendants<Button>(shortcutItems)
                .SingleOrDefault(button => button.Name != "RemoveShortcut" && button.Tag is NotchShortcut item && item == shortcut);
            await WaitForNotchAsync(() => savedShortcuts.All(shortcut => OpenShortcut(shortcut) is { IsEnabled: true }));
            await CaptureAsync(root, "notch-shortcuts.png");
            Invoke(OpenShortcut(savedShortcuts[0])!);
            await WaitForNotchAsync(() => opened.SequenceEqual([file]) && shown.AppWindow.Size.Height == compact.Height);
            Invoke((Button)root.FindName("Grip"));
            await WaitForNotchAsync(() => panel.Visibility == Visibility.Visible && shortcutTile.Visibility == Visibility.Visible);
            Invoke(OpenShortcut(savedShortcuts[1])!);
            await WaitForNotchAsync(() => opened.SequenceEqual([file, folder]) && shown.AppWindow.Size.Height == compact.Height);

            var shape = (ComboBox)details.FindName("ShapeChoice");
            var displays = (ComboBox)details.FindName("DisplayChoice");
            Require(shape.SelectedIndex == 0 && displays.SelectedIndex == 0, "The existing settings did not use the attached primary display.");
            failNextPlacementSave = true;
            shape.SelectedIndex = 1;
            await notch.FlushAsync();
            await WaitForNotchAsync(() => shape.SelectedIndex == 0 && notch.HasError);
            Require(notch.Placement == NotchPlacement.Default, "A failed shape save changed the confirmed placement.");
            shape.SelectedIndex = 1;
            await notch.FlushAsync();
            await WaitForNotchAsync(() => placement.Floating && shape.SelectedIndex == 1);
            var monitorTarget = NotchFrame.DisplayDevices().OrderBy(item => item.Area.IsPrimary).First();
            displays.SelectedItem = displays.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == monitorTarget.Device);
            await notch.FlushAsync();
            await WaitForNotchAsync(() => placement.DisplayDevice == monitorTarget.Device &&
                shown.AppWindow.Position.Y == monitorTarget.Area.OuterBounds.Y + monitorTarget.Area.WorkArea.Y +
                    (int)Math.Round(12 * root.XamlRoot.RasterizationScale));
            CheckNotchOutline(shown, floating: true);
            const string disconnected = "test-disconnected-monitor";
            await notch.SetPlacementAsync(new(true, disconnected));
            await WaitForNotchAsync(() => displays.SelectedItem is ComboBoxItem { Tag: disconnected } &&
                shown.AppWindow.Position.Y == position.Y + (int)Math.Round(12 * root.XamlRoot.RasterizationScale));
            displays.SelectedIndex = 0;
            await notch.FlushAsync();
            shape.SelectedIndex = 0;
            await notch.FlushAsync();
            await WaitForNotchAsync(() => placement == NotchPlacement.Default && shown.AppWindow.Position.Y == position.Y);
            var constrained = NotchFrame.Place(new(40, 26, 360, 18), 1.5, 500, 500, floating: true);
            Require(constrained.Equals(new Windows.Graphics.RectInt32(40, 43, 360, 1)),
                "Floating placement exceeded a narrow work area or lost its work-area offsets.");

            var savedPlacement = new NotchPlacement(true, monitorTarget.Device);
            await notch.SetPlacementAsync(savedPlacement);
            await notch.PlaceOverviewTileAsync(NotchView.Batteries, 2);
            Toggle(toggle);
            await notch.FlushAsync();
            Require(!notch.IsRunning && !enabled && !toggle.IsOn && writes.SequenceEqual([true, true, false]),
                "Turning off the notch did not close the window and save the switch.");
            monitor.Verify(m => m.StopAsync(), Times.AtLeastOnce);
            using var restored = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(),
                _ => throw new InvalidOperationException("A disabled Notch must not create a window."));
            await restored.LoadAsync();
            Require(!restored.IsRunning && !restored.IsEnabled && restored.Modules == (NotchModule)modules &&
                restored.PinnedModules == (NotchModule)pinnedModules && restored.Placement == savedPlacement &&
                restored.IsSysInfoEnabled && restored.IsOverviewEnabled &&
                restored.ModuleCountText == $"{moduleCount - 3}/{moduleCount} modules enabled" && restored.WeatherLocation == selectedPlace &&
                restored.Shortcuts.SequenceEqual(savedShortcuts), "The saved modules, city or shortcuts did not survive reload.");
            await restored.PlaceOverviewTileAsync(NotchView.Media, 2);
            Require(content.OverviewTiles.SequenceEqual([NotchView.Batteries, NotchView.Shortcuts, NotchView.Media, NotchView.Weather]),
                "The restored Overview did not retain the custom tile order.");
            Console.WriteLine("PASS: Notch media artwork, seeking and visualizer, attached and floating placement, navigation, readings and preference restore.");
        }
        finally
        {
            shell.Children.Remove(settingsHost);
            shell.Children.Remove(overview);
        }
    }

    /// <summary>Exercises status notices, frame-rate readings and file sharing through the real Notch window.</summary>
    private async Task CheckNotchDesktopActionsAsync()
    {
        CheckNotchDeviceSources();
        var localization = ExtensionLocalization();
        var log = Mock.Of<ILogService>();
        var preferences = new Mock<IUserPreferencesService>();
        var enabled = NotchModule.Shortcuts | NotchModule.Clock | NotchModule.StatusAlerts | NotchModule.FrameRate;
        preferences.Setup(p => p.ReadSnapshotAsync()).ReturnsAsync(new UserPreferencesSnapshot(
            new Dictionary<string, object>
            {
                [NotchExtension.PreferenceKey] = true,
                [NotchExtension.ModulesPreferenceKey] = (int)enabled,
                [NotchExtension.PinnedModulesPreferenceKey] = (int)(NotchModule.Clock | NotchModule.FrameRate)
            }, log));
        preferences.Setup(p => p.SetPreferenceAsync(NotchExtension.PlacementPreferenceKey, It.IsAny<NotchPlacement>()))
            .ReturnsAsync(OperationResult.Succeeded());

        DesktopLocalDevice speakers = new("speakers", "Speakers");
        var state = new NotchReadingSnapshot(Power: new(false, 22, false), Volume: new(35, false), Brightness: 48,
            BluetoothDevices: [], LocalDevices: [speakers]);
        int baselineReads = 0, batteryReads = 0;
        var frames = Channel.CreateUnbounded<int?>();
        var readings = new NotchReadings(log, _ => throw new InvalidOperationException("System counters are disabled."),
            _ => Task.FromResult<IReadOnlyList<DesktopBattery>>([]), statusSources: new(
                () => { Interlocked.Or(ref baselineReads, 1); return Volatile.Read(ref state).Power; },
                () => { Interlocked.Or(ref baselineReads, 2); return Volatile.Read(ref state).Volume; },
                () => { Interlocked.Or(ref baselineReads, 4); return Volatile.Read(ref state).Brightness; },
                _ => { Interlocked.Or(ref baselineReads, 8); return Task.FromResult(Volatile.Read(ref state).BluetoothDevices!); },
                _ => { Interlocked.Or(ref baselineReads, 16); return Task.FromResult(Volatile.Read(ref state).LocalDevices!); },
                frames.Reader.ReadAllAsync,
                (id, _) =>
                {
                    Require(id == "headset-endpoint", "A device ID was used as a GATT endpoint ID.");
                    batteryReads++;
                    return Task.FromResult<int?>(82);
                }));

        var shared = new List<string>();
        var pickerTitles = new List<string>();
        string? selected = null;
        NotchWindow? window = null;
        using var notch = new NotchExtension(preferences.Object, localization, log, Mock.Of<INotchStartupService>(), owner =>
            window = new NotchWindow(owner, localization, log, Mock.Of<IDesktopMediaSession>(), readings,
                pickShortcutFile: (_, title) => { pickerTitles.Add(title); return selected; },
                shareFile: path => { shared.Add(path); return Task.CompletedTask; }));
        await notch.LoadAsync();
        var root = (Grid)(window ?? throw new InvalidOperationException("The status test did not create the Notch.")).Content;
        await LoadedAsync(root);
        var banner = (Border)root.FindName("StatusBanner");
        var icon = (FontIcon)root.FindName("StatusIcon");
        var label = (TextBlock)root.FindName("StatusLabel");
        var value = (TextBlock)root.FindName("StatusValue");
        var level = (ProgressBar)root.FindName("StatusLevel");
        var readouts = (StackPanel)root.FindName("CompactReadouts");
        var fps = (TextBlock)root.FindName("CompactFps");
        var fpsStrip = (Border)root.FindName("CompactFpsStrip");
        static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 1;
        double CenterY(FrameworkElement element) => element.TransformToVisual(banner).TransformPoint(default).Y + element.ActualHeight / 2;

        void CheckCompactBanner(int shoulder = 7)
        {
            root.UpdateLayout();
            var origin = banner.TransformToVisual(root).TransformPoint(default);
            Require(Near(banner.ActualWidth, 284) && Near(banner.ActualHeight, 34) &&
                Near(origin.X - shoulder, 8) && Near(root.ActualWidth - origin.X - banner.ActualWidth - shoulder, 8) &&
                Near(origin.Y, 8) && Near(root.ActualHeight - origin.Y - banner.ActualHeight, 8),
                $"{label.Text} did not fit the compact frame with equal spacing.");
            Require(banner.CornerRadius == new CornerRadius(12) && banner.Background is LinearGradientBrush &&
                icon.FontSize == 20 && label.FontSize == 13 && value.FontSize == 13 &&
                Near(CenterY(icon), 17) && Near(CenterY(label), 17) && (value.Text.Length == 0 || Near(CenterY(value), 17)),
                $"{label.Text} did not use the shared banner style and centered content.");
        }

        await frames.Writer.WriteAsync(144);
        await WaitForNotchAsync(() => Volatile.Read(ref baselineReads) == 31 && fps.Text == "144 FPS");
        Require(banner.Visibility == Visibility.Collapsed && fpsStrip.Visibility == Visibility.Visible,
            "The initial samples created a notice or failed to show the game FPS strip.");
        await CaptureAsync(root, "notch-game-fps.png");
        var compactHeight = root.ActualHeight;

        Volatile.Write(ref state, state with { Volume = new(57, false) });
        await WaitForNotchAsync(() => banner.Visibility == Visibility.Visible && label.Text == "Volume" && value.Text == "57%" &&
            Near(root.ActualHeight, 50));
        Require(level.Visibility == Visibility.Visible && level.Value == 57 && readouts.Visibility == Visibility.Collapsed,
            "The volume level was not displayed in the temporary compact status banner.");
        CheckCompactBanner();
        Require(level.TransformToVisual(banner).TransformPoint(default).Y >= CenterY(label) + label.ActualHeight / 2 - 1,
            "The volume level overlapped the label.");
        await CaptureAsync(root, "notch-status-volume.png");
        Volatile.Write(ref state, state with { Volume = new(57, true) });
        await WaitForNotchAsync(() => label.Text == "Volume muted" && level.Visibility == Visibility.Collapsed);

        Volatile.Write(ref state, state with { Brightness = 72 });
        await WaitForNotchAsync(() => label.Text == "Brightness" && level.Value == 72 && value.Text == "72%");
        Volatile.Write(ref state, state with { Power = new(false, 20, false) });
        await WaitForNotchAsync(() => label.Text == "Low battery" && value.Text == "20%" && level.Visibility == Visibility.Collapsed);
        Volatile.Write(ref state, state with { Power = new(true, 79, true) });
        await WaitForNotchAsync(() => label.Text == "Charging" && value.Text == "79%");
        CheckCompactBanner();
        Require(icon.Glyph == "\uEBB3" && level.Visibility == Visibility.Collapsed &&
            icon.Foreground is SolidColorBrush accent && value.Foreground is SolidColorBrush percent &&
            accent.Color.G > accent.Color.R && accent.Color.G > accent.Color.B && accent.Color.Equals(percent.Color),
            "The charging banner did not use a green icon and percentage.");
        await CaptureAsync(root, "notch-status-charging.png");
        Volatile.Write(ref state, state with { Power = new(true, 100, false) });
        await WaitForNotchAsync(() => label.Text == "Battery full" && value.Text == "100%");
        Require(icon.Glyph == "\uEBA7" && icon.Foreground is SolidColorBrush batteryAccent && batteryAccent.Color.B > batteryAccent.Color.G,
            "Replacing the charging banner did not restore the normal battery icon and color.");
        Volatile.Write(ref state, state with { Power = new(false, 100, false) });
        await WaitForNotchAsync(() => label.Text == "On battery" && value.Text == "100%");
        CheckCompactBanner();
        await CaptureAsync(root, "notch-status-on-battery.png");
        await notch.SetPlacementAsync(new NotchPlacement(Floating: true));
        await WaitForNotchAsync(() => Near(root.ActualWidth, 300));
        CheckCompactBanner(shoulder: 0);
        await notch.SetPlacementAsync(NotchPlacement.Default);

        Volatile.Write(ref state, state with { BluetoothDevices = [new("headset", "Headset", BatteryEndpointId: "headset-endpoint")] });
        await WaitForNotchAsync(() => label.Text == "Headset connected" && value.Text == "82%");
        await CaptureAsync(root, "notch-status-device.png");
        Volatile.Write(ref state, state with { BluetoothDevices = [] });
        await WaitForNotchAsync(() => label.Text == "Headset disconnected" && value.Text.Length == 0);

        Volatile.Write(ref state, state with { LocalDevices = [speakers, new("usb-headset", "USB headset")] });
        await WaitForNotchAsync(() => label.Text == "USB headset connected" && banner.Visibility == Visibility.Visible);
        Require(icon.Glyph == "\uE772" && value.Text.Length == 0 && level.Visibility == Visibility.Collapsed,
            "The local device notice did not use the device icon and plain label.");
        CheckCompactBanner();
        Volatile.Write(ref state, state with { LocalDevices = [speakers] });
        await WaitForNotchAsync(() => label.Text == "USB headset disconnected" && banner.Visibility == Visibility.Visible);
        await WaitForNotchAsync(() => banner.Visibility == Visibility.Collapsed && readouts.Visibility == Visibility.Visible &&
            Near(root.ActualHeight, compactHeight));
        Require(batteryReads == 1, "The connection status re-read the reported battery without a new connection.");
        Console.WriteLine("PASS: Notch Bluetooth and USB connection notices, physical device deduplication and battery level.");

        await frames.Writer.WriteAsync(null);
        await WaitForNotchAsync(() => fpsStrip.Visibility == Visibility.Collapsed && banner.Visibility == Visibility.Collapsed);
        const double expandedHeight = 250;
        const double bannerHeightIncrease = 42;
        bool HasExpandedHeight(double height)
        {
            root.UpdateLayout();
            return Near(root.ActualHeight, height);
        }

        var panel = (Grid)root.FindName("Panel");
        Invoke((Button)root.FindName("Grip"));
        await WaitForNotchAsync(() => ((Border)root.FindName("ShortcutsCard")).Visibility == Visibility.Visible &&
            panel.Visibility == Visibility.Visible && HasExpandedHeight(expandedHeight));
        var share = (Button)root.FindName("ShareToPhone");
        Invoke(share);
        await WaitForNotchAsync(() => pickerTitles.Count == 1);
        Require(shared.Count == 0, "Cancelling the file picker started sharing.");
        selected = Path.Combine(Path.GetTempPath(), "notch-share.txt");
        Invoke(share);
        await WaitForNotchAsync(() => shared.Count == 1);
        Require(shared.SequenceEqual([selected]) && pickerTitles.SequenceEqual(["Share to phone", "Share to phone"]) &&
            notch.Shortcuts.Count == 0, "The share action did not pass the chosen file to Windows Share independently of pins.");

        Volatile.Write(ref state, state with { Power = new(true, 100, true) });
        await WaitForNotchAsync(() => banner.Visibility == Visibility.Visible && label.Text == "Charging" && value.Text == "100%" &&
            HasExpandedHeight(expandedHeight + bannerHeightIncrease),
            () => $"Charging banner: {label.Text} {value.Text}, {banner.Visibility}, panel {panel.Visibility}, " +
                $"height {root.ActualHeight}, expected {expandedHeight + bannerHeightIncrease}.");
        var expandedBannerBottom = banner.TransformToVisual(root).TransformPoint(default).Y + banner.ActualHeight;
        Require(panel.Visibility == Visibility.Visible && Near(panel.TransformToVisual(root).TransformPoint(default).Y - expandedBannerBottom, 8) &&
            icon.Glyph == "\uEBB5", "The full charging banner overlapped the expanded Notch controls.");
        frames.Writer.TryComplete(new UnauthorizedAccessException("Graphics tracing denied."));
        await WaitForNotchAsync(() => label.Text == "Graphics capture unavailable" && banner.Visibility == Visibility.Visible &&
            HasExpandedHeight(expandedHeight + bannerHeightIncrease));
        Require(fpsStrip.Visibility == Visibility.Collapsed, "A denied graphics trace left a stale FPS number on screen.");
        await WaitForNotchAsync(() => banner.Visibility == Visibility.Collapsed && HasExpandedHeight(expandedHeight));
        Console.WriteLine("PASS: Notch transient state changes, game FPS strip and phone sharing.");
    }

    /// <summary>Checks independently sampled interfaces and shared physical device identities.</summary>
    private static void CheckNotchDeviceSources()
    {
        var changes = new NotchStatusChanges();
        DesktopBluetoothDevice headset = new("headset", "Headset", 82);
        DesktopLocalDevice audioInterface = new("HEADSET", "Headset audio");
        Require(changes.Observe(new(BluetoothDevices: [headset])) is null &&
            changes.Observe(new(LocalDevices: [audioInterface])) is null,
            "A source announced a device that was already present on startup.");
        Require(changes.Observe(new(LocalDevices: [audioInterface, new("jack", "Headphones")]))
            is { TextKey: "Notch_DeviceConnected", Device: "Headphones", Icon: NotchNoticeIcon.Device },
            "An audio jack connection depended on a Bluetooth sample.");
        Require(changes.Observe(new(BluetoothDevices: [])) is null &&
            changes.Observe(new(BluetoothDevices: [headset])) is null &&
            changes.Observe(new(LocalDevices: [new("jack", "Headphones")])) is null,
            "The headset was announced when another interface still represented the same device.");
        Require(changes.Observe(new(BluetoothDevices: []))
            is { TextKey: "Notch_DeviceDisconnected", Device: "Headset", Icon: NotchNoticeIcon.Bluetooth },
            "The headset did not disconnect after both interfaces went away.");
    }

    /// <summary>Checks the actual Win32 region at the shoulder and the bottom of the compact window.</summary>
    private static void CheckNotchOutline(NotchWindow window, bool floating = false)
    {
        var size = window.AppWindow.Size;
        var region = CreateRectRgn(0, 0, 0, 0);
        Require(region != 0, "Could not size a native region for the Notch outline check.");
        try
        {
            Require(GetWindowRgn(WindowNative.GetWindowHandle(window), region) != 0 &&
                PtInRegion(region, 1, 0) != floating && (floating || !PtInRegion(region, 1, size.Height / 2)) &&
                PtInRegion(region, size.Width / 2, size.Height - 2) && !PtInRegion(region, size.Width - 2, size.Height - 2),
                "The Notch region did not form the top shoulder and rounded lower edge.");
        }
        finally { DeleteObject(region); }
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(nint region, int x, int y);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint resource);

    private static async Task WaitForNotchAsync(Func<bool> condition, Func<string>? describeFailure = null)
    {
        var start = TimeProvider.System.GetTimestamp();
        while (!condition() && TimeProvider.System.GetElapsedTime(start) < TimeSpan.FromSeconds(3)) await Task.Delay(25);
        if (!condition())
            throw new InvalidOperationException(describeFailure?.Invoke() ?? "The notch did not complete its transition.");
    }
}
