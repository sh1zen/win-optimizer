using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Moq;
using Windows.Graphics;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Resources;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.UI.Features.Extensions.Preview;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using PreviewCompanion = WinOptimizer.UI.Features.Extensions.Preview.PreviewApplication;

namespace WinOptimizer.PopupTests;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass a test results directory.");
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
        var stage = "Popup checks";
        deadline.Interval = TimeSpan.FromSeconds(60);
        deadline.Tick += (_, _) => Finish($"{stage} timed out.");
        deadline.Start();
        shell.Loaded += async (_, _) =>
        {
            try
            {
                await CheckModalQueueAsync();
                await CheckConfigImportAsync(shell);
                await CheckFramesAsync(shell);
                await CheckTaskOutputAsync(shell);
                await CheckTechnicalDetailsAsync(shell);
                CheckFirstPreviewAfterMinimize();
                await CheckFilePreviewAsync(shell);
                await CheckExtensionSettingsAsync(shell);
                await CheckNotchCalendarLayoutAsync();
                await CheckNotchBatteriesLayoutAsync();
                await CheckNotchShortcutsAsync();
                await CheckNotchQuickSettingsAsync();
                await CheckNotchBatteriesAsync();
                await CheckCompactOrderAsync();
                await CheckNotchAsync(shell);
                deadline.Stop();
                stage = "Notch desktop checks";
                deadline.Interval = TimeSpan.FromSeconds(25);
                deadline.Start();
                await CheckNotchDesktopActionsAsync();
                deadline.Stop(); Finish();
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        };
        _window.Activate();
    }

    /// <summary>Verifies that Preview opens while the main window is minimized and stays topmost after it returns.</summary>
    private void CheckFirstPreviewAfterMinimize()
    {
        var main = _window!;
        var presenter = (OverlappedPresenter)main.AppWindow.Presenter;
        var preview = new Window { Content = new Grid() };
        preview.AppWindow.IsShownInSwitchers = false;
        try
        {
            presenter.Minimize();
            Require(presenter.State == OverlappedPresenterState.Minimized, "The main test window did not minimize.");
            Require(!preview.AppWindow.IsVisible, "The Preview window showed before a request.");
            PreviewCompanion.ShowPreviewWindow(preview);
            Require(preview.AppWindow.IsVisible, "The first Preview request did not show its window.");
            presenter.Restore();
            main.Activate();
            Require(preview.AppWindow.IsVisible && preview.AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true },
                "Preview did not stay visible and topmost after activating the main window.");
            Console.WriteLine("PASS: Preview opens while the main window is minimized and stays topmost after it returns.");
        }
        finally
        {
            preview.Close();
            presenter.Restore();
            main.Activate();
        }
    }

    /// <summary>Verifies that summaries fit their metadata and that readable content expands the popup.</summary>
    private async Task CheckFilePreviewAsync(Grid shell)
    {
        var layout = new Grid { Width = 660, Height = PreviewSurface.InitialHeight, VerticalAlignment = VerticalAlignment.Top };
        bool closed = false;
        var processExecutor = new Mock<IProcessExecutor>(MockBehavior.Strict);
        var surface = new PreviewSurface(ExtensionLocalization(), processExecutor.Object, () => closed = true,
            current => layout.Height = current.MeasureClientHeight(layout.Width));
        layout.Children.Add(surface);
        shell.Children.Add(layout);
        try
        {
            await LoadedAsync(surface);
            var folder = new QuickPreview(@"C:\sample", "Sample folder", PreviewKind.Folder, new DateTime(2026, 2, 5), 2048,
                Entries: [new("drafts", true, null), new("notes", true, null), new("example.txt", false, 17), new("summary.txt", false, 2031)],
                IsPartial: true)
            {
                FolderDetails = new(new DateTime(2025, 2, 4), FileAttributes.Hidden, 12, 5, 4096, true)
            };
            var open = (Button)surface.FindName("OpenButton");
            var folderLifetime = surface.Begin(folder.Name);
            Require(!open.IsEnabled, "Open was enabled before a selection was loaded.");
            await surface.PresentAsync(folder, folderLifetime);
            surface.UpdateLayout();
            var artwork = (FrameworkElement)surface.FindName("FileArtwork");
            var heading = (TextBlock)surface.FindName("NameLabel");
            var size = (TextBlock)surface.FindName("SizeValue");
            var disk = (TextBlock)surface.FindName("DiskValue");
            var diskMetric = (FrameworkElement)surface.FindName("DiskMetric");
            var exact = (TextBlock)surface.FindName("SizeExact");
            var files = (Microsoft.UI.Xaml.Documents.Run)surface.FindName("FilesValue");
            var subfolders = (Microsoft.UI.Xaml.Documents.Run)surface.FindName("SubfoldersValue");
            var modified = (TextBlock)surface.FindName("Modified");
            var created = (TextBlock)surface.FindName("Created");
            var folderPath = (TextBlock)surface.FindName("FolderPath");
            var metadata = (TextBlock)surface.FindName("Metadata");
            var reveal = (Button)surface.FindName("Reveal");
            var close = (Button)surface.FindName("CloseButton");
            var contents = (ToggleButton)surface.FindName("FolderContents");
            var body = (ScrollViewer)surface.FindName("BodyScroll");
            var excerpt = (TextBlock)surface.FindName("Excerpt");
            var message = (InfoBar)surface.FindName("Message");
            var appIcon = (Image)surface.FindName("AppIcon");
            var loading = (ProgressRing)surface.FindName("LoadingRing");
            Windows.Foundation.Point Position(FrameworkElement element) => element.TransformToVisual(surface).TransformPoint(default);
            bool Fits(FrameworkElement element) => element.ActualHeight > 0 &&
                Position(element).Y + element.ActualHeight <= surface.ActualHeight;

            double folderHeight = layout.Height;
            Require(body.Visibility == Visibility.Collapsed && contents.Visibility == Visibility.Visible,
                "A folder did not open as a compact summary with the contents action.");
            Require(Position(artwork).X + artwork.ActualWidth < Position(heading).X &&
                Position(open).X + open.ActualWidth <= Position(reveal).X &&
                Position(reveal).Y + reveal.ActualHeight <= Position(artwork).Y &&
                Position(close).Y + close.ActualHeight <= Position(heading).Y,
                "The actions, icon and name are out of order in the compact layout.");
            Require(size.Text.EndsWith(" KiB", StringComparison.Ordinal) && disk.Text.EndsWith(" KiB", StringComparison.Ordinal) &&
                exact.Text.EndsWith(" B", StringComparison.Ordinal) && files.Text == "12" && subfolders.Text == "5" &&
                size.FontWeight.Weight == 700 && disk.FontWeight.Weight == 700 &&
                files.FontWeight.Weight == 700 && subfolders.FontWeight.Weight == 700,
                "Sizes and recursive counts are missing or lack visual emphasis.");
            Require(Position(disk).X > Position(size).X && Position(disk).Y == Position(size).Y &&
                Position(modified).Y > Position(size).Y && Position(created).Y > Position(modified).Y &&
                Position(folderPath).Y > Position(created).Y && Position(folderPath).X == Position(modified).X,
                "Statistics, dates and path are out of order or misaligned.");
            Require(metadata.Text.Contains("Folder totals are incomplete.", StringComparison.Ordinal) && !message.IsOpen,
                "Partial totals were not identified in the summary.");
            Require(created.Text.Length > 0 && folderPath.Text == folder.Path && metadata.Text.Contains("Hidden", StringComparison.Ordinal),
                "Folder properties are missing from the summary.");
            Require(Fits(folderPath) && Fits(metadata), "The path or attributes did not fit in the visible folder summary.");

            var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(contents).GetPattern(PatternInterface.Toggle);
            toggle.Toggle();
            Require(body.Visibility == Visibility.Visible && layout.Height > folderHeight && excerpt.Text.Contains("example.txt", StringComparison.Ordinal) &&
                excerpt.Text.Contains("direct entries", StringComparison.Ordinal),
                "The folder contents action did not expand the popup and show the entries.");
            toggle.Toggle();
            Require(body.Visibility == Visibility.Collapsed && layout.Height == folderHeight, "The folder contents action did not collapse the popup.");

            var invokeOpen = (IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke);
            processExecutor.Setup(executor => executor.ShellExecuteAsync(folder.Path, null, false, folderLifetime))
                .ReturnsAsync((int?)null);
            Require(open.IsEnabled && AutomationProperties.GetName(open) == "Open", "Open was unavailable or lacked a label.");
            invokeOpen.Invoke();
            processExecutor.Verify(executor => executor.ShellExecuteAsync(folder.Path, null, false, folderLifetime), Times.Once);
            Require(closed, "Opening an existing folder window did not dismiss the preview.");
            closed = false;

            var text = new QuickPreview(@"C:\sample\example.txt", "example.txt", PreviewKind.Text, folder.Modified, 17, Text: "Example file text");
            var shortcut = text with { Path = @"C:\sample\example.lnk", Name = "example.lnk", ContentPath = text.Path };
            var shortcutLifetime = surface.Begin(shortcut.Name);
            await surface.PresentAsync(shortcut, shortcutLifetime);
            Require(body.Visibility == Visibility.Visible && contents.Visibility == Visibility.Collapsed && excerpt.Text == text.Text,
                "The text preview did not expand automatically or retained the folder controls.");
            processExecutor.Setup(executor => executor.ShellExecuteAsync(shortcut.Path, null, false, shortcutLifetime))
                .ThrowsAsync(new System.ComponentModel.Win32Exception("File unavailable"));
            invokeOpen.Invoke();
            surface.UpdateLayout();
            processExecutor.Verify(executor => executor.ShellExecuteAsync(shortcut.Path, null, false, shortcutLifetime), Times.Once);
            Require(!closed && message.IsOpen && message.Visibility == Visibility.Visible &&
                message.Severity == InfoBarSeverity.Error && message.Message == "File unavailable" && Fits(message),
                "A failed open did not keep the preview and show the error.");

            var archive = new QuickPreview(@"C:\sample\archive.zip", "archive.zip", PreviewKind.File, folder.Modified,
                718L * 1024 * 1024, IsPartial: true)
            {
                ContentError = "The archive exceeds 128 MiB."
            };
            var archiveLifetime = surface.Begin(archive.Name);
            Require(appIcon.Source is null && appIcon.Visibility == Visibility.Collapsed, "The previous icon survived a selection change.");
            await surface.PresentAsync(archive, archiveLifetime);
            surface.UpdateLayout();
            Require(appIcon.Source is SoftwareBitmapSource && appIcon.Visibility == Visibility.Visible &&
                !message.IsOpen && !loading.IsActive && body.Visibility == Visibility.Collapsed && Fits(modified),
                "The oversized archive did not stay in the compact summary with its icon.");
            Require(size.Text.EndsWith(" MiB", StringComparison.Ordinal) && diskMetric.Visibility == Visibility.Collapsed &&
                folderPath.Visibility == Visibility.Collapsed && files.Text.Length == 0 && subfolders.Text.Length == 0,
                "The file summary did not display its size or retained properties from the previous folder.");

            // At a narrow width, wrapped shortcut metadata must grow the summary itself.
            double archiveHeight = layout.Height;
            layout.Width = 420;
            var longTarget = @"C:\sample\" + string.Concat(Enumerable.Repeat(@"project documentation and release notes\", 10)) + "archive.zip";
            var link = archive with { Path = @"C:\sample\archive.lnk", Name = "archive.lnk", Kind = PreviewKind.Link, Details = [new("Target", longTarget)] };
            await surface.PresentAsync(link, surface.Begin(link.Name));
            surface.UpdateLayout();
            Require(layout.Height > archiveHeight && Fits(modified) && Fits(metadata) &&
                metadata.Text.Contains(longTarget, StringComparison.Ordinal) && body.Visibility == Visibility.Collapsed,
                "Wrapped metadata did not fit in the taller summary.");

            layout.Width = 660;
            var pdf = archive with { Path = @"C:\sample\large.pdf", Name = "large.pdf", Kind = PreviewKind.Pdf, IsPartial = false, ContentError = null };
            await surface.PresentAsync(pdf, surface.Begin(pdf.Name));
            Require(!message.IsOpen && !loading.IsActive && body.Visibility == Visibility.Collapsed && layout.Height == archiveHeight,
                "The oversized PDF did not stay in the compact summary.");

            ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
            Require(closed, "The toolbar close button did not dismiss the preview.");
            Console.WriteLine("PASS: Preview summaries fit archive and wrapped metadata, and readable content expands independently.");
        }
        finally
        {
            surface.Clear();
            shell.Children.Remove(layout);
        }
    }

    private static ILocalizationService ExtensionLocalization()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("en.json")!;
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(source)!;
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
        localization.Setup(service => service.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] values) => string.Format(strings[key], values));
        return localization.Object;
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
            var close = dialogButtons.Single(button => button.Name == "CloseButton");
            Require(primary.CornerRadius.TopLeft > 0 && primary.CornerRadius.Equals(close.CornerRadius) &&
                Math.Abs(primary.ActualWidth - close.ActualWidth) < 1 && Math.Abs(primary.ActualHeight - close.ActualHeight) < 1,
                $"Dialog actions have different shapes: primary {primary.CornerRadius} ({primary.ActualWidth}x{primary.ActualHeight}), close {close.CornerRadius} ({close.ActualWidth}x{close.ActualHeight}).");
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
            if (language == "en")
            {
                using var first = Create("spacing-first");
                using var middle = Create("spacing-middle");
                using var last = Create("spacing-last");
                await CheckFilteredSettingsSpacingAsync(shell, first, middle, last);
            }
            application.VerifyNoOtherCalls();
        }
        Console.WriteLine("PASS: technical popups in all three templates; English/Italian, both themes, three widths, all six detail types, registry action, dismissal, resize and unload.");
    }

    /// <summary>Measures the actual grouped settings cards before and after filtering a middle row.</summary>
    private static async Task CheckFilteredSettingsSpacingAsync(Grid shell, SettingItemViewModel first,
        SettingItemViewModel middle, SettingItemViewModel last)
    {
        var group = new SettingsGroup("Settings", [first, middle, last]);
        var source = new CollectionViewSource { IsSourceGrouped = true, Source = new[] { group } };
        var view = new SettingsListView { GroupedSettingsSource = source.View };
        shell.Children.Add(view);
        try
        {
            await LoadedAsync(view);
            view.UpdateLayout();
            var list = (ListView)view.FindName("SettingsListViewControl");
            FrameworkElement Card(int index) => Descendants<CommunityToolkit.WinUI.Controls.SettingsCard>(list.ContainerFromIndex(index)).First();
            CheckCardGap(Card(0), Card(1));
            CheckCardGap(Card(1), Card(2));

            middle.IsVisible = false;
            view.UpdateLayout();
            CheckCardGap(Card(0), Card(2));
            Console.WriteLine("PASS: filtered settings cards keep the shared gap.");
        }
        finally
        {
            shell.Children.Remove(view);
            group.Clear();
        }
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

    private void Finish(string? error = null)
    {
        File.WriteAllText(Path.Combine(_output, "popup-result.log"), error ?? "All popup checks passed.");
        if (error != null) Console.Error.WriteLine(error);
        Environment.Exit(error == null ? 0 : 1);
    }
}
