using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Cleaner;
using WinOptimizer.UI.Features.Drivers;
using WinOptimizer.UI.Features.Settings;
using WinOptimizer.UI.Features.Settings.ViewModels;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Loads the actual Settings XAML with synthetic feature services on a native dispatcher.</summary>
public sealed class SettingsPageTests
{
    [Fact]
    public async Task SettingsRendersItsCategoriesAndSharedPreferenceControls()
    {
        var frame = new Frame
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
        };
        var window = new Window { Content = frame };
        try
        {
            var frameLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            frame.Loaded += (_, _) => frameLoaded.TrySetResult();
            frame.Navigate(typeof(NavigationOtherPage));
            window.Activate();
            await frameLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask);
            var router = new NavigationRouter(null, null, new ExtensionTestDispatcher());
            router.NavigateToPage(frame, "Settings");
            var page = await RenderCurrentSettingsAsync(frame);
            var settingsPage = page;
            var viewModel = page.ViewModel;
            var initialization = viewModel.InitializeAsync();
            var reloads = 0;
            var unloads = 0;
            page.Loaded += (_, _) => reloads++;
            page.Unloaded += (_, _) => unloads++;
            Assert.Single(frame.BackStack);
            Assert.Null(viewModel.SelectedCategory);
            var categories = Assert.IsType<ListView>(page.FindName("CategoryList"));
            Assert.Null(categories.SelectedItem);
            Assert.NotEmpty(categories.Items);
            Assert.Equal(Visibility.Visible, categories.Visibility);
            Assert.True(categories.ActualHeight > 100, $"Category list height: {categories.ActualHeight}");
            Assert.True(Descendants<PageHeader>(page).Single().ActualHeight > 0);
            // Allow the native navigation entrance animation to finish before exporting pixels.
            await Task.Delay(350);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(frame);
            var pixels = await bitmap.GetPixelsAsync();
            await using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "settings-preview.png")))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
                await encoder.FlushAsync();
            }
            Assert.Contains(Descendants<TextBlock>(categories), text => text.Text == viewModel.Categories[0].Title);

            viewModel.Query = "Backups";
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            Assert.Same(page, frame.Content);
            Assert.Null(viewModel.SelectedCategory);
            viewModel.Query = "";
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            Assert.Same(page, frame.Content);
            Assert.Null(categories.SelectedItem);

            categories.SelectedItem = viewModel.Categories.Single(item => item.Category == SettingsCategory.Backups);
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Equal(SettingsCategory.Backups, page.ViewModel.SelectedCategory?.Category);
            Assert.Same(page.ViewModel.Cleaner, Descendants<CleanerSettingsControl>(page).Single().ViewModel);
            categories = Assert.IsType<ListView>(page.FindName("CategoryList"));
            categories.SelectedItem = page.ViewModel.Categories.Single(item => item.Category == SettingsCategory.Drivers);
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Same(viewModel, page.ViewModel);
            Assert.Same(initialization, page.ViewModel.InitializeAsync());
            Assert.IsType<SuppressNavigationTransitionInfo>(Assert.IsType<NavigationThemeTransition>(Assert.Single(page.Transitions)).DefaultNavigationTransitionInfo);
            Assert.Equal(SettingsCategory.Drivers, page.ViewModel.SelectedCategory?.Category);
            Assert.Same(page.ViewModel.Drivers, Descendants<DriverSettingsControl>(page).Single().ViewModel);

            frame.GoBack();
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Equal(SettingsCategory.Backups, page.ViewModel.SelectedCategory?.Category);
            frame.GoForward();
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Equal(SettingsCategory.Drivers, page.ViewModel.SelectedCategory?.Category);
            router.NavigateToPage(frame, "Settings");
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Same(categories, page.FindName("CategoryList"));
            Assert.Equal(0, reloads);
            Assert.Equal(0, unloads);
            Assert.Null(page.ViewModel.SelectedCategory);
            Assert.True(Assert.IsType<ListView>(page.FindName("CategoryList")).ActualHeight > 100);

            // The full reset stays beside its description at both desktop and compact widths.
            foreach (var width in new[] { 1200d, 600d })
            {
                page.Width = width;
                page.ViewModel.SelectedCategory = page.ViewModel.Categories.Single(item => item.Category == SettingsCategory.ResetAll);
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
                page.UpdateLayout();
                var card = Assert.IsType<Border>(page.FindName("FullResetCard"));
                var reset = Assert.Single(Descendants<Button>(card));
                Assert.Equal(Visibility.Visible, card.Visibility);
                Assert.True(card.ActualHeight > 0);
                Assert.Same(page.ViewModel.ResetCommand, reset.Command);
                Assert.True(reset.IsEnabled);
                Assert.False(page.ViewModel.ShowCategoryReset);
                var offset = reset.TransformToVisual(card).TransformPoint(new Windows.Foundation.Point());
                Assert.InRange(offset.Y, 0, card.ActualHeight - reset.ActualHeight);
                Assert.InRange(offset.X, 0, card.ActualWidth - reset.ActualWidth);
                Assert.InRange(card.TransformToVisual(page).TransformPoint(new Windows.Foundation.Point()).Y, 0, page.ActualHeight / 2);
                Assert.All(Descendants<Button>(page).Where(button => button.Command == page.ViewModel.ResetCommand && button != reset),
                    button => Assert.Equal(Visibility.Collapsed, button.Visibility));

                page.ViewModel.SelectedCategory = page.ViewModel.Categories.Single(item => item.Category == SettingsCategory.Drivers);
                await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
                Assert.Equal(Visibility.Collapsed, card.Visibility);
                Assert.True(page.ViewModel.ShowCategoryReset);
                Assert.Single(Descendants<Button>(page), button => button.Command == page.ViewModel.ResetCommand && button != reset && button.Visibility == Visibility.Visible);
            }

            // Leaving Settings ends its lifetime; history reentry gets an active, fresh view model.
            frame.Navigate(typeof(NavigationOtherPage));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask);
            frame.GoBack();
            page = await RenderCurrentSettingsAsync(frame);
            Assert.NotSame(settingsPage, page);
            Assert.NotSame(viewModel, page.ViewModel);
            Assert.Null(page.ViewModel.SelectedCategory);
        }
        finally { window.Close(); }
    }

    /// <summary>Waits for the real page lifecycle and verifies navigation did not replace it during layout.</summary>
    private static async Task<SettingsPage> RenderCurrentSettingsAsync(Frame frame)
    {
        var page = Assert.IsType<SettingsPage>(frame.Content);
        if (!page.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await page.ViewModel.InitializeAsync();
        await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
        page.UpdateLayout();
        Assert.Same(page, frame.Content);
        Assert.True(page.ActualWidth > 0);
        Assert.True(Assert.IsType<Grid>(page.Content).ActualHeight > 100);
        return page;
    }

    /// <summary>Inspects controls produced by the production page and templates.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
