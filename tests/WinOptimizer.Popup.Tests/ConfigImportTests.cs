using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Moq;
using Windows.Graphics;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Dialogs;
using WinOptimizer.UI.Features.Common.Services;

namespace WinOptimizer.PopupTests;

public sealed partial class PreviewApplication
{
    /// <summary>Checks the production import dialog, its themed layout and its native selection controls.</summary>
    private async Task CheckConfigImportAsync(Grid shell)
    {
        var root = shell.XamlRoot;
        var window = _window!.AppWindow;
        var originalSize = window.Size;
        var originalTheme = shell.RequestedTheme;
        try
        {
            foreach (var (theme, language) in new[] { (ElementTheme.Dark, "en"), (ElementTheme.Light, "it") })
            {
                using var translations = typeof(PreviewApplication).Assembly.GetManifestResourceStream(language + ".json")!;
                var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(translations)!;
                var localization = new Mock<ILocalizationService>(MockBehavior.Strict);
                localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => strings[key]);
                var service = new DialogService(localization.Object, Mock.Of<ILogService>(), Mock.Of<ITaskProgressService>())
                    { XamlRoot = root };
                shell.RequestedTheme = theme;

                var pending = service.ShowConfigImportOptionsDialogAsync();
                ConfigImportDialogContent? body = null;
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (body is null && !pending.IsCompleted && DateTime.UtcNow < deadline)
                {
                    body = VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
                        .SelectMany(popup => Tree<ConfigImportDialogContent>(popup.Child)).SingleOrDefault();
                    if (body is null) await Task.Delay(10);
                }
                if (pending.IsCompleted) await pending;
                if (body is null) throw new InvalidOperationException("The configuration import dialog did not open.");
                await LoadedAsync(body);

                var frame = VisualTreeHelper.GetOpenPopupsForXamlRoot(root).SelectMany(popup => Tree<Border>(popup.Child))
                    .Single(border => border.Name == "BackgroundElement");
                var buttons = Descendants<Button>(frame).ToArray();
                var apply = buttons.Single(button => button.Name == "PrimaryButton");
                var cancel = buttons.Single(button => button.Name == "CloseButton");
                var sources = Descendants<RadioButton>(body).Where(radio => radio.GroupName == "ImportSource").ToArray();
                var scroll = Descendants<ScrollViewer>(body).First();
                frame.UpdateLayout();
                Require(body.ActualTheme == theme && sources.Length == 4 && sources.All(card => card.IsChecked != true) &&
                    !apply.IsEnabled && apply.CornerRadius.TopLeft > 0 && apply.CornerRadius.Equals(cancel.CornerRadius) &&
                    scroll.ScrollableHeight < 1,
                    "The import dialog did not use the host theme, source defaults or rounded actions, or required scrolling at full size.");
                var defaults = body.GetOptions();
                Require(defaults.ProcessWindowsAppsRemoval && !defaults.ProcessExternalAppsRemoval &&
                    defaults.ApplyThemeWallpaper && defaults.ApplyCleanTaskbar && defaults.ApplyCleanStartMenu,
                    "The initial import options changed.");

                Select(sources[1]);
                Select(sources[2]);
                sources[2].Focus(FocusState.Pointer);
                frame.UpdateLayout();
                var mark = Descendants<FrameworkElement>(sources[2]).Single(element => element.Name == "SelectionMark");
                Require(sources[2].IsChecked == true && sources.Count(card => card.IsChecked == true) == 1 &&
                    mark.Opacity == 1 && apply.IsEnabled, "Source selection did not update the card and Apply action.");

                if (theme == ElementTheme.Light)
                {
                    var resized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnResized(object sender, SizeChangedEventArgs args) => resized.TrySetResult();
                    shell.SizeChanged += OnResized;
                    try
                    {
                        window.ResizeClient(new SizeInt32(420, 380));
                        await resized.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        frame.UpdateLayout();
                        Require(frame.ActualWidth <= root.Size.Width + 1 && frame.ActualHeight <= root.Size.Height + 1 &&
                            scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 0 && scroll.ScrollableWidth < 1,
                            "The configuration import body did not scroll inside the smaller window.");
                    }
                    finally { shell.SizeChanged -= OnResized; }
                    Invoke(cancel);
                    var (option, _) = await pending.WaitAsync(TimeSpan.FromSeconds(3));
                    Require(option is null, "Cancelling import retained the selected source.");
                }
                else
                {
                    var actions = Descendants<RadioButton>(body).Single(radio => radio.Name == "ExternalAppsUninstall");
                    Select(actions);
                    var wallpaper = Descendants<CheckBox>(body).Single(checkBox => checkBox.Name == "ThemeWallpaper");
                    wallpaper.IsChecked = false;
                    Invoke(apply);
                    var (option, options) = await pending.WaitAsync(TimeSpan.FromSeconds(3));
                    Require(option == ImportOption.ImportBackup && options.ProcessWindowsAppsRemoval &&
                        options.ProcessExternalAppsRemoval && !options.ApplyThemeWallpaper && options.ApplyCleanTaskbar &&
                        options.ApplyCleanStartMenu, "The dialog returned incorrect import settings.");
                }
            }
        }
        finally
        {
            shell.RequestedTheme = originalTheme;
            window.Resize(originalSize);
        }

        Console.WriteLine("PASS: configuration import themes, responsive layout and native selection.");
    }

    private static void Select(RadioButton radio) =>
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)).Select();
}
