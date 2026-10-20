using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Moq;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Resources;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using WinOptimizer.UI.Tests.Helpers;

namespace WinOptimizer.PopupTests;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass a preview output directory.");
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new PreviewApplication(output);
        });
    }
}

/// <summary>Exercises the shared handler and real setting templates without provider calls or Windows changes.</summary>
public sealed partial class PreviewApplication : Microsoft.UI.Xaml.Application
{
    private readonly string _output;
    private Window? _window;

    public PreviewApplication(string output)
    {
        _output = output;
        UnhandledException += (_, args) => Finish(args.Exception.ToString());
        DebugSettings.XamlResourceReferenceFailed += (_, args) => File.AppendAllText(Path.Combine(_output, "resource-errors.log"), args.Message + Environment.NewLine);
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var shell = new Grid();
        _window = new Window { Content = shell };
        _window.AppWindow.ResizeClient(new SizeInt32(1240, 1000));
        var deadline = _window.DispatcherQueue.CreateTimer();
        deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish("Popup checks timed out.");
        deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try
            {
                await CheckModalQueueAsync();
                await CheckFramesAsync(shell);
                await CheckTechnicalDetailsAsync(shell);
                await CheckSponsorsAsync(shell);
                await CheckApplicationCloseAsync();
                deadline.Stop(); Finish();
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    /// <summary>Verifies FIFO preparation and release after an exception using the real modal owner.</summary>
    private static async Task CheckModalQueueAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var first = PopupHandler.RunModalAsync(async () => { order.Add(1); await release.Task; order.Add(2); return 1; });
        var second = PopupHandler.RunModalAsync(() => { order.Add(3); return Task.FromResult(2); });
        Require(order.SequenceEqual(new[] { 1 }), "Modal preparation overlapped.");
        release.SetResult(); await Task.WhenAll(first, second);
        Require(order.SequenceEqual(new[] { 1, 2, 3 }), "Modal order changed.");
        try { await PopupHandler.RunModalAsync<int>(() => throw new InvalidOperationException("test failure")); throw new Exception("A modal error was swallowed."); }
        catch (InvalidOperationException ex) when (ex.Message == "test failure") { }
        Require(await PopupHandler.RunModalAsync(() => Task.FromResult(7)) == 7, "A failed modal retained the queue.");
        Console.WriteLine("PASS: shared modal queue order, error propagation and release.");
    }

    /// <summary>Checks native dialogs, menus and tooltips against the host theme and existing native actions.</summary>
    private async Task CheckFramesAsync(Grid shell)
    {
        var fixture = new MenuFixture();
        shell.Children.Add(fixture); await LoadedAsync(fixture);
        fixture.Label = "Localized action"; fixture.Selected = true;
        await FlyoutAsync(fixture.Popup, true, fixture.Open);
        var action = (RadioMenuFlyoutItem)fixture.Popup.Items.Single();
        Require(ReferenceEquals(action, fixture.OriginalAction) && action.Text == "Localized action" && action.IsChecked,
            "Compiled menu resource bindings or identity changed.");
        ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(action).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(80);
        Require(fixture.Clicks == 1, "A compiled menu resource lost its original action.");
        fixture.Popup.Hide();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            shell.RequestedTheme = theme;
            var anchor = new Button { Content = "Popup actions", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24) };
            shell.Children.Clear(); shell.Children.Add(anchor);
            await LoadedAsync(anchor);
            var clicked = 0;
            var item = new MenuFlyoutItem { Text = "Existing action" };
            item.Click += (_, _) => clicked++;
            var radio = new RadioMenuFlyoutItem { Text = "Selected order", IsChecked = true, GroupName = "order" };
            anchor.Resources["Action"] = item; anchor.Resources["Radio"] = radio;
            PopupHandler.AttachMenu(anchor, ["Action", "Radio"]);
            var menu = (MenuFlyout)anchor.Flyout;
            Require(ReferenceEquals(menu.Items[0], item) && ReferenceEquals(menu.Items[1], radio) && radio.IsChecked,
                "Menu actions, order or radio state changed during attachment.");
            await FlyoutAsync(menu, true, () => PopupHandler.SetIsOpen(anchor, true));
            var menuPresenters = VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot)
                .SelectMany(popup => Tree<MenuFlyoutPresenter>(popup.Child)).ToArray();
            Require(menuPresenters.Length > 0 && menuPresenters.All(presenter => presenter.ActualTheme == theme),
                $"Menu presenters: expected={theme}, found={menuPresenters.Length}, themes={string.Join(',', menuPresenters.Select(presenter => presenter.ActualTheme))}.");
            ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(80);
            Require(clicked == 1, "The shared menu did not invoke its original action.");
            menu.Hide();
            var tooltip = PopupHandler.CreateToolTip(anchor, "Measured day details");
            ToolTipService.SetToolTip(anchor, tooltip);
            Require(Equals(tooltip.Content, "Measured day details") && ReferenceEquals(tooltip.Style, Resources["Popup.ToolTip"]), "Tooltip content or shared frame changed.");
            var tipOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tooltip.Opened += (_, _) => tipOpened.TrySetResult();
            tooltip.IsOpen = true; await tipOpened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(tooltip.RequestedTheme == theme, "The tooltip uses the wrong host theme.");
            tooltip.IsOpen = false;

            var before = 0; var after = 0; var primaryClicks = 0;
            var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
            {
                Title = "Shared dialog", Content = new TextBlock { Text = "Native validation and result", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "Confirm", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
            });
            dialog.PrimaryButtonClick += (_, e) => { if (++primaryClicks == 1) e.Cancel = true; };
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Opened += (_, _) => opened.TrySetResult();
            var showing = PopupHandler.RunModalAsync(() => PopupHandler.ShowDialogAsync(dialog, shell.XamlRoot,
                () => before++, () => after++));
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await LoadedAsync(dialog); dialog.UpdateLayout();
            Require(dialog.RequestedTheme == theme && dialog.XamlRoot == shell.XamlRoot && before == 1 && after == 0,
                "Modal theme, root or resource ordering is incorrect.");
            var dialogButtons = Tree<Button>(dialog).Concat(Tree<Button>(shell.XamlRoot.Content))
                .Concat(VisualTreeHelper.GetOpenPopupsForXamlRoot(shell.XamlRoot).SelectMany(popup => Tree<Button>(popup.Child))).Distinct().ToArray();
            var primary = dialogButtons.SingleOrDefault(button => button.Name == "PrimaryButton" || Equals(button.Content, "Confirm"))
                ?? throw new InvalidOperationException("Dialog buttons: " + string.Join("; ", dialogButtons.Select(button => button.Name + "=" + button.Content)));
            Invoke(primary); await Task.Delay(80);
            Require(!showing.IsCompleted && primaryClicks == 1, "Cancelled validation closed the dialog.");
            Invoke(primary);
            Require(await showing == ContentDialogResult.Primary && after == 1 && primaryClicks == 2,
                "The shared modal changed its result or cleanup timing.");
        }
        Console.WriteLine("PASS: shared dialog/menu/tooltip frames; both themes, native validation, results and menu actions.");
    }

    /// <summary>Checks the actual info button and technical sections in regular, parent and child templates.</summary>
    private async Task CheckTechnicalDetailsAsync(Grid shell)
    {
        var templates = new SettingTemplates();
        Resources.MergedDictionaries.Add(templates);
        foreach (var language in new[] { "en", "it" })
        {
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(source)!;
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => strings.GetValueOrDefault(key, key));
            var application = new Mock<ISettingApplicationService>(MockBehavior.Strict);
            var regedit = new Mock<IRegeditLauncher>(MockBehavior.Strict);
            var paths = new List<string>();
            regedit.Setup(service => service.OpenAtPath(It.IsAny<string>())).Callback<string>(paths.Add);
            SettingItemViewModel Create(string id) => new(new SettingItemViewModelConfig
            {
                SettingDefinition = new() { Id = id, Name = id, Description = "", InputType = InputType.Toggle },
                SettingId = id, Name = strings["Setting_taskbar-widgets_Name"], Description = strings["Setting_taskbar-widgets_Description"], InputType = InputType.Toggle
            }, application.Object, Mock.Of<ILogService>(), new ExtensionTestDispatcher(), Mock.Of<IDialogService>(), localization.Object, regeditLauncher: regedit.Object);
            using var regular = Create("regular"); using var parent = Create("parent"); using var child = Create("child"); using var empty = Create("empty");
            var rows = new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" }.Select((hive, index) => new TechnicalDetailRow
            {
                RowType = DetailRowType.Registry, RegistryPath = hive + @"\Software\Policies\Microsoft\Dsh", ValueName = "AllowNewsAndInterests", ValueType = "DWord",
                CurrentValue = "1 (On)", RecommendedValue = "0 (Off)", DefaultValue = "1 (On)", PathLabel = strings["TechnicalDetails_Path"],
                CurrentLabel = strings["TechnicalDetails_Current"], RecommendedLabel = strings["TechnicalDetails_Recommended"], DefaultLabel = strings["TechnicalDetails_DefaultValue"],
                OpenRegeditCommand = regular.OpenRegeditCommand, CanOpenRegedit = index == 0
            }).ToArray();
            var sections = new[] { new TechnicalDetailSection(DetailRowType.Registry, strings["TechnicalDetails_Section_Registry"], true, rows) };
            regular.TechnicalDetailSections = sections; parent.TechnicalDetailSections = sections; child.TechnicalDetailSections = sections;
            parent.Children = new() { child }; parent.IsExpanderExpanded = true;
            var cards = new StackPanel { Margin = new Thickness(24), Spacing = 12, VerticalAlignment = VerticalAlignment.Top };
            foreach (var (vm, key) in new[] { (regular, "SettingItemTemplate"), (parent, "SettingExpanderItemTemplate"), (empty, "SettingItemTemplate") })
                cards.Children.Add(new ContentControl { Content = vm, ContentTemplate = (DataTemplate)templates[key], HorizontalContentAlignment = HorizontalAlignment.Stretch });
            shell.Children.Clear(); shell.Children.Add(cards); shell.UpdateLayout();
            await Task.Delay(100); shell.UpdateLayout();
            var controls = Descendants<TechnicalDetailsButton>(cards).ToArray();
            Require(controls.Length == 4 && controls.Count(control => control.Visibility == Visibility.Visible) == 3, "Info buttons are missing or empty details are visible.");
            foreach (var control in controls.Where(control => control.Visibility == Visibility.Visible))
            {
                var button = (Button)control.FindName("InfoButton"); await LoadedAsync(button);
                var cardDialog = await OpenDetailsAsync(control, () => Invoke(button));
                Require(control.Setting!.IsTechnicalDetailsExpanded && parent.IsExpanderExpanded, "An info click changed the group or opened the wrong setting.");
                await CloseDetailsAsync(cardDialog, () => Invoke(DialogButtons(cardDialog).Single(close => close.Name == "CloseButton")));
            }
            var info = controls.Single(control => ReferenceEquals(control.Setting, regular));
            var trigger = (Button)info.FindName("InfoButton");
            var content = DetailsContent(info);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var width in new[] { 1240, 760, 520 })
            {
                shell.RequestedTheme = theme; _window!.AppWindow.ResizeClient(new SizeInt32(width, 1000));
                await Task.Delay(100); shell.UpdateLayout(); await LoadedAsync(trigger);
                var height = cards.ActualHeight;
                var dialog = await OpenDetailsAsync(info, () => Invoke(trigger));
                content.UpdateLayout();
                Require(regular.IsTechnicalDetailsExpanded, "Info open state was not published.");
                Require(trigger.Flyout == null && ReferenceEquals(dialog.Style, Resources["Popup.Dialog"]), "Technical details must use the shared dialog builder.");
                Require(Equals(dialog.Title, regular.Name) && dialog.CloseButtonText == regular.CloseTechnicalDetailsLabel,
                    "The shared title and close action lost their localized text.");
                Require(((Grid)content).Background == null, "Technical content must use the shared dialog surface.");
                Require(Math.Abs(cards.ActualHeight - height) < 1, "The popup changed the page layout.");
                Require(content.ActualWidth > 0 && content.ActualWidth < shell.ActualWidth && content.ActualHeight < shell.ActualHeight, "The popup exceeds the viewport.");
                for (var ancestor = VisualTreeHelper.GetParent(content); ancestor is FrameworkElement viewport; ancestor = VisualTreeHelper.GetParent(viewport))
                    Require(viewport.ActualWidth <= 0 || content.ActualWidth <= viewport.ActualWidth + 1,
                        $"Technical cards are clipped by {viewport.GetType().Name}: {content.ActualWidth} > {viewport.ActualWidth}.");
                Require(content.ActualTheme == theme, "The popup did not inherit its anchor's theme.");
                var texts = Descendants<TextBlock>(content).Where(text => text.ActualHeight > 0).ToArray();
                Require(rows.All(row => texts.Any(text => text.Text == row.RegistryPath && text.IsTextSelectionEnabled)), "Registry paths must remain complete and selectable.");
                Require(texts.Any(text => text.Text == rows[0].RecommendedValue), "Registry state comparisons are missing.");
                Require(AutomationProperties.GetName(trigger) == strings["View_TechnicalDetails"], "The info button lost its localized accessible name.");
                var card = Descendants<Border>(content).First(border => border.Padding.Left == 14);
                var color = ((SolidColorBrush)card.Background).Color;
                var surface = ((AcrylicBrush)dialog.Background).TintColor;
                var renderedRed = (color.R * color.A + surface.R * (255 - color.A)) / 255;
                Require(theme == ElementTheme.Dark ? renderedRed < 120 : renderedRed > 180, "Card brushes use the wrong popup theme.");
                await CaptureAsync(dialog, $"technical-details-{language}-{theme}-{width}.png");
                await CloseDetailsAsync(dialog, () => Invoke(DialogButtons(dialog).Single(button => button.Name == "CloseButton")));
                Require(!regular.IsTechnicalDetailsExpanded, "Closing left stale popup state.");
            }
            var activeDialog = await OpenDetailsAsync(info, regular.ToggleTechnicalDetails);
            var registryButtons = Descendants<Button>(content).Where(button => button.CommandParameter is string).ToArray();
            Require(registryButtons.Length == 2 && registryButtons[0].IsEnabled && !registryButtons[1].IsEnabled, "Registry availability changed.");
            Invoke(registryButtons[0]); Require(paths.SequenceEqual(new[] { rows[0].RegistryPath }), "Registry navigation changed its path.");
            await CloseDetailsAsync(activeDialog, () => PopupHandler.Hide(content));
            Require(!regular.IsTechnicalDetailsExpanded, "Shared shortcut dismissal left stale state.");
            activeDialog = await OpenDetailsAsync(info, regular.ToggleTechnicalDetails);
            await CloseDetailsAsync(activeDialog, regular.ToggleTechnicalDetails);
            Require(!regular.IsTechnicalDetailsExpanded, "View-model dismissal left stale state.");
            regular.TechnicalDetailSections = AllSections(strings, rows[0]);
            activeDialog = await OpenDetailsAsync(info, regular.ToggleTechnicalDetails);
            Require(Descendants<ScrollViewer>(content).Single(scroll => scroll.Name == "DetailsScroll").ScrollableHeight > 0,
                "Long details do not scroll.");
            var allTexts = Descendants<TextBlock>(content).Select(text => text.Text).ToArray();
            foreach (var expected in new[] { "Write-Output 'technical preview'", "Windows Registry Editor Version 5.00", "Test dependency", @"\Test\ScheduledTask", "TEST_POWER_SETTING", "On Apply" })
                Require(allTexts.Contains(expected), "Missing technical content: " + expected);
            await CaptureAsync(activeDialog, $"technical-details-all-types-{language}.png");
            _window!.AppWindow.ResizeClient(new SizeInt32(380, 600)); await Task.Delay(100);
            content.UpdateLayout();
            Require(content.ActualWidth < shell.ActualWidth && content.ActualHeight < shell.ActualHeight,
                $"Resize: state={regular.IsTechnicalDetailsExpanded}, actual={content.ActualWidth}x{content.ActualHeight}, requested={content.Width}x{content.MaxHeight}, window={shell.ActualWidth}x{shell.ActualHeight}.");
            await CloseDetailsAsync(activeDialog, shell.Children.Clear);
            Require(!regular.IsTechnicalDetailsExpanded, "Unloading retained a popup.");
            shell.Children.Add(cards); await LoadedAsync(trigger);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = PopupHandler.RunModalAsync(async () => { await release.Task; return true; });
            regular.ToggleTechnicalDetails();
            shell.Children.Clear(); release.SetResult(); await blocker;
            await PopupHandler.RunModalAsync(() => Task.FromResult(true));
            Require(!regular.IsTechnicalDetailsExpanded && !content.IsLoaded, "A queued dialog opened after its card unloaded.");
            application.VerifyNoOtherCalls();
        }
        Console.WriteLine("PASS: technical popups in all three templates; English/Italian, both themes, three widths, all six detail types, registry action, dismissal, resize and unload.");
    }

    private static IReadOnlyList<TechnicalDetailSection> AllSections(Dictionary<string, string> strings, TechnicalDetailRow row) =>
    [
        new(DetailRowType.Registry, strings["TechnicalDetails_Section_Registry"], true, [row, new() { RowType = DetailRowType.Registry, IsActionRow = true, ValueName = "Action value", OnApplyLabel = "On Apply", OnApplyValue = "0", RegistryPath = row.RegistryPath }]),
        new(DetailRowType.ScheduledTask, strings["TechnicalDetails_Section_ScheduledTasks"], false, [new() { RowType = DetailRowType.ScheduledTask, TaskPath = @"\Test\ScheduledTask", CurrentState = "On", RecommendedState = "Off", DefaultState = "On" }]),
        new(DetailRowType.PowerConfig, strings["TechnicalDetails_Section_PowerSettings"], false, [new() { RowType = DetailRowType.PowerConfig, SubgroupAlias = "TEST_POWER_GROUP", SubgroupGuid = Guid.Empty.ToString(), SettingAlias = "TEST_POWER_SETTING", SettingGuid = Guid.Empty.ToString(), CurrentAC = "50", CurrentDC = "40", RecommendedAC = "80", RecommendedDC = "60", DefaultAC = "100", DefaultDC = "100" }]),
        new(DetailRowType.PowerShellScript, strings["TechnicalDetails_Section_Scripts"], false, [new() { RowType = DetailRowType.PowerShellScript, ScriptLabel = "On Enable", ScriptBody = "Write-Output 'technical preview'" }]),
        new(DetailRowType.RegContent, strings["TechnicalDetails_Section_RegContent"], false, [new() { RowType = DetailRowType.RegContent, ContentLabel = "On Enable", ContentBody = "Windows Registry Editor Version 5.00" }]),
        new(DetailRowType.Dependency, strings["TechnicalDetails_Section_Dependencies"], false, [new() { RowType = DetailRowType.Dependency, DependencyLabel = "Test dependency", DependencyRelation = "= On" }])
    ];

    /// <summary>Renders the production support dialog without fetching sponsors or opening donation links.</summary>
    private async Task CheckSponsorsAsync(Grid shell)
    {
        var builderType = typeof(PopupHandler).Assembly.GetType("WinOptimizer.UI.Features.Common.Dialogs.SponsorsDialogBuilder", throwOnError: true)!;
        Require(Equals(builderType.GetField("CoffeeUrl", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue(),
            "https://www.paypal.com/donate/?hosted_button_id=8G8VR4APG9JRU"), "The coffee donation destination changed.");
        _window!.AppWindow.ResizeClient(new SizeInt32(1000, 900));
        foreach (var language in new[] { "en", "it" })
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (var mode in new[] { SponsorsDialogMode.Normal, SponsorsDialogMode.Exit })
        {
            shell.RequestedTheme = theme;
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(language + ".json")!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(source)!;
            Require(strings["Sponsors_Title"] == (language == "en" ? "win-optimizer is free." : "win-optimizer è gratuito."),
                "The support title changed.");
            Require(strings["Sponsors_CoffeeButton"] == (language == "en" ? "Buy me a coffee" : "Offrimi un caffè"),
                "The coffee action lost its localized label.");
            var localization = new Mock<ILocalizationService>();
            localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => strings.GetValueOrDefault(key, key));
            var sponsors = new Mock<ISponsorsService>(MockBehavior.Strict);
            var builder = Activator.CreateInstance(builderType, localization.Object, sponsors.Object)!;
            var dialog = (ContentDialog)builderType.GetMethod("Build")!.Invoke(builder, [null, mode, shell.XamlRoot])!;
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Opened += (_, _) => opened.TrySetResult();
            var showing = PopupHandler.ShowDialogAsync(dialog, shell.XamlRoot);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await LoadedAsync(dialog); dialog.UpdateLayout();
            var body = (Grid)dialog.Content;
            Require(Descendants<TextBlock>(body).Any(text => text.Text == strings["Sponsors_Title"]), "The support title is missing.");
            var buttons = Descendants<Button>(body).ToArray();
            Require(buttons.Length == 2, "Both support actions must be present.");
            Require(Equals(buttons[0].Content, strings["Sponsors_SupportButton"]) && Equals(buttons[1].Content, strings["Sponsors_CoffeeButton"]),
                "The support actions lost their labels or order.");
            Require(buttons.All(button => button.ActualWidth > 0 && button.ActualHeight > 0) &&
                buttons.Sum(button => button.ActualWidth) + 10 <= body.ActualWidth, "The support actions are clipped.");
            Require(!Descendants<CheckBox>(body).Any(), "The support dialog must not contain checkboxes.");
            Require(body.RowDefinitions.Count == 6, "The support dialog must contain six content rows.");
            Require(dialog.CloseButtonText == strings[mode == SponsorsDialogMode.Exit ? "Sponsors_Exit" : "Sponsors_Close"], "The close action changed.");
            await CaptureAsync(dialog, $"sponsors-{language}-{theme}-{mode}.png");
            await CloseDetailsAsync(dialog, () => Invoke(DialogButtons(dialog).Single(button => button.Name == "CloseButton")));
            await showing;
            var result = ((bool SupportClicked, bool DontShowAgain))builderType.GetMethod("ExtractResult")!.Invoke(builder, null)!;
            Require(!result.SupportClicked && !result.DontShowAgain, "Closing the support dialog changed its result.");
            sponsors.VerifyNoOtherCalls();
        }
        Console.WriteLine("PASS: support dialog titles and coffee action; English/Italian, both themes and both modes.");
    }

    /// <summary>Exercises production shutdown while keeping the native test application alive.</summary>
    private static async Task CheckApplicationCloseAsync()
    {
        foreach (var (running, confirmed) in new[] { (false, true), (true, true), (true, false) })
        {
            var tasks = new Mock<ITaskProgressService>();
            tasks.Setup(service => service.IsTaskRunning).Returns(running);
            var dialogs = new Mock<IDialogService>(MockBehavior.Strict);
            if (running)
                dialogs.Setup(service => service.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
                    .ReturnsAsync(new ConfirmationResponse { Confirmed = confirmed });
            var cleanupCompleted = false;
            var shutdownCount = 0;
            var close = new ApplicationCloseService(Mock.Of<ILogService>(), tasks.Object, dialogs.Object)
            {
                BeforeShutdown = () => { cleanupCompleted = true; return Task.CompletedTask; },
                ShutdownAction = () =>
                {
                    Require(cleanupCompleted, "Shutdown ran before cleanup completed.");
                    shutdownCount++;
                }
            };

            var result = await close.CheckOperationsAndCloseAsync();

            Require(result.Success == confirmed && shutdownCount == (confirmed ? 1 : 0), "Close confirmation or shutdown count changed.");
            if (running)
                dialogs.Verify(service => service.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()), Times.Once);
            dialogs.VerifyNoOtherCalls();
            tasks.Verify(service => service.CancelCurrentTask(), running && confirmed ? Times.Once() : Times.Never());
        }
        Console.WriteLine("PASS: idle shutdown, confirmed operation cancellation and cancelled close.");
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }

    /// <summary>Opens through the actual trigger or view-model path and inspects the native dialog it created.</summary>
    private static async Task<ContentDialog> OpenDetailsAsync(TechnicalDetailsButton control, Action action)
    {
        var content = DetailsContent(control);
        // ContentDialog reparents its body; the native owner is not a visual ancestor.
        var field = typeof(TechnicalDetailsButton).GetField("_dialog", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The technical dialog owner field is missing.");
        action();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        // Automation Invoke posts the click; resource content can already report IsLoaded before that click runs.
        while (DateTime.UtcNow < deadline)
        {
            if (field.GetValue(control) is ContentDialog dialog &&
                VisualTreeHelper.GetOpenPopupsForXamlRoot(control.XamlRoot)
                    .Any(popup => Tree<FrameworkElement>(popup.Child).Contains(content)))
            {
                content.UpdateLayout();
                return dialog;
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException($"Technical dialog did not open: setting={control.Setting?.IsTechnicalDetailsExpanded}, cardLoaded={control.IsLoaded}, contentLoaded={content.IsLoaded}, dialog={field.GetValue(control)}.");
    }

    private static IEnumerable<Button> DialogButtons(ContentDialog dialog) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot).SelectMany(popup => Tree<Button>(popup.Child));

    private static FrameworkElement DetailsContent(TechnicalDetailsButton control) =>
        typeof(TechnicalDetailsButton).GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(control) as FrameworkElement
        ?? throw new InvalidOperationException("The technical dialog body is missing.");

    /// <summary>Waits for native dismissal and completion of the setting-state synchronization.</summary>
    private static async Task CloseDetailsAsync(ContentDialog dialog, Action action)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => closed.TrySetResult();
        dialog.Closed += OnClosed;
        try { action(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(3)); await Task.Yield(); }
        finally { dialog.Closed -= OnClosed; }
    }

    /// <summary>Waits for responsive header reparenting to finish before native interaction.</summary>
    private static async Task LoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded) return;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler handler = (_, _) => completed.TrySetResult(); element.Loaded += handler;
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(3)); } finally { element.Loaded -= handler; }
    }

    /// <summary>Observes native open/close events instead of assuming animation durations.</summary>
    private static async Task FlyoutAsync(FlyoutBase popup, bool opening, Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object> handler = (_, _) => completed.TrySetResult();
        if (opening) popup.Opened += handler; else popup.Closed += handler;
        try { action(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { if (opening) popup.Opened -= handler; else popup.Closed -= handler; }
    }

    private static IEnumerable<T> Tree<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (var child in Descendants<T>(root)) yield return child;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private async Task CaptureAsync(FrameworkElement content, string name)
    {
        if (content is ContentDialog { Content: FrameworkElement body })
        {
            content = body;
            while (VisualTreeHelper.GetParent(content) is FrameworkElement parent) content = parent;
        }
        await Task.Delay(100); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(content);
        Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "Empty preview: " + name);
        await using var output = File.Create(Path.Combine(_output, name));
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }

    private void Finish(string? error = null)
    {
        File.WriteAllText(Path.Combine(_output, "popup-result.log"), error ?? "All popup checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
