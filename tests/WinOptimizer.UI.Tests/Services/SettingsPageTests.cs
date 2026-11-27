using FluentIcons.Common;
using FluentIcons.WinUI;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
            Assert.IsType<SettingsPage>(frame.Content).Width = 1200;
            var page = await RenderCurrentSettingsAsync(frame);
            var settingsPage = page;
            var viewModel = page.ViewModel;
            var initialization = viewModel.InitializeAsync();
            var reloads = 0;
            var unloads = 0;
            page.Loaded += (_, _) => reloads++;
            page.Unloaded += (_, _) => unloads++;
            Assert.Single(frame.BackStack);
            var defaultCategory = viewModel.Categories[0];
            Assert.Equal(SettingsCategory.General, defaultCategory.Category);
            Assert.Same(defaultCategory, viewModel.SelectedCategory);
            Assert.True(viewModel.ShowGeneral);
            Assert.False(viewModel.ShowPlaceholder);
            var general = Assert.IsType<StackPanel>(page.FindName("GeneralSettings"));
            Assert.Equal(Visibility.Visible, general.Visibility);
            var generalCards = Descendants<CommunityToolkit.WinUI.Controls.SettingsCard>(general).ToArray();
            var appearanceCards = general.Children.OfType<CommunityToolkit.WinUI.Controls.SettingsCard>().ToArray();
            Assert.Equal(new[] { viewModel.LanguageHeader, viewModel.ThemeHeader, viewModel.WindowBackgroundPreferences.Label,
                    viewModel.SidebarPreferences.ExpandedLabel, viewModel.AppColorPreferences.Label },
                appearanceCards.Select(card => Assert.IsType<string>(card.Header)));
            foreach (var (previous, next) in appearanceCards.Zip(appearanceCards.Skip(1)))
            {
                var previousBottom = previous.TransformToVisual(general).TransformPoint(default).Y + previous.ActualHeight;
                var nextTop = next.TransformToVisual(general).TransformPoint(default).Y;
                Assert.InRange(nextTop - previousBottom, general.Spacing - 1, general.Spacing + 1);
            }
            Assert.Equal(new[] { viewModel.AppearanceHeading, viewModel.UpdatesHeading, viewModel.InfoHeading },
                Descendants<TextBlock>(general).Where(text => AutomationProperties.GetHeadingLevel(text) == AutomationHeadingLevel.Level3)
                    .Select(text => text.Text));
            Assert.Contains(generalCards, card => Equals(card.Header, viewModel.UpdatePreferences.ScriptUpdatesLabel));
            var info = viewModel.Info;
            var updates = info.Updates;
            var versionCard = Assert.Single(generalCards, card => Equals(card.Header, info.VersionLabel));
            Assert.Same(versionCard, Assert.Single(generalCards, card => card.Command == updates.CheckForUpdatesCommand));
            Assert.True(versionCard.IsClickEnabled);
            Assert.Equal(info.VersionInfo, versionCard.Description);
            Assert.Equal(new[] { info.VersionLabel, info.SupportLabel, info.DocumentationLabel, info.ReportBugLabel },
                generalCards.TakeLast(4).Select(card => Assert.IsType<string>(card.Header)));
            Assert.Equal(updates.CheckForUpdatesLabel, AutomationProperties.GetHelpText(versionCard));
            Assert.False(Assert.IsType<ProgressRing>(versionCard.Content).IsActive);
            Assert.False(updates.IsUpdateToastOpen);
            var checkForUpdates = Assert.IsAssignableFrom<IAsyncRelayCommand>(versionCard.Command);
            Assert.True(checkForUpdates.CanExecute(null));
            await checkForUpdates.ExecuteAsync(null);
            Assert.True(updates.IsUpdateToastOpen);
            Assert.Equal(InfoBarSeverity.Success, updates.UpdateToastSeverity);
            var categories = Assert.IsType<ListView>(page.FindName("CategoryList"));
            Assert.Same(defaultCategory, categories.SelectedItem);
            Assert.NotEmpty(categories.Items);
            Assert.Equal(Visibility.Visible, categories.Visibility);
            Assert.True(categories.ActualHeight > 100, $"Category list height: {categories.ActualHeight}");
            Assert.True(Descendants<PageHeader>(page).Single().ActualHeight > 0);
            var showCategories = Assert.IsType<Button>(page.FindName("ShowCategoryListButton"));
            Assert.Equal(Visibility.Collapsed, showCategories.Visibility);
            router.NavigateToPage(frame, "Settings");
            Assert.Single(frame.BackStack);
            Assert.Contains(Descendants<TextBlock>(categories), text => text.Text == viewModel.Categories[0].Title);
            var firstCategory = Assert.IsType<ListViewItem>(categories.ContainerFromItem(viewModel.Categories[0]));
            var categoryCard = Assert.IsType<Grid>(VisualTreeHelper.GetChild(firstCategory, 0));
            Assert.NotNull(categoryCard.Background);
            Assert.Equal(new CornerRadius(10), categoryCard.CornerRadius);

            viewModel.Query = "Registry";
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            Assert.Same(page, frame.Content);
            Assert.Null(viewModel.SelectedCategory);
            Assert.Null(categories.SelectedItem);

            categories.SelectedItem = viewModel.Categories.Single(item => item.Category == SettingsCategory.Registry);
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Equal(SettingsCategory.Registry, page.ViewModel.SelectedCategory?.Category);
            Assert.Same(page.ViewModel.Cleaner, Descendants<CleanerSettingsControl>(page).Single().ViewModel);
            viewModel.Query = "";
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
            Assert.Equal(SettingsCategory.Registry, page.ViewModel.SelectedCategory?.Category);
            Assert.Equal("Registry", viewModel.Query);
            frame.GoForward();
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Equal(SettingsCategory.Drivers, page.ViewModel.SelectedCategory?.Category);
            Assert.Empty(viewModel.Query);

            viewModel.Query = "ApplicationStartup";
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            categories.SelectedItem = Assert.Single(Assert.Single(viewModel.VisibleGroups).Items);
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(defaultCategory, viewModel.SelectedCategory);
            var startupCard = Assert.IsType<CommunityToolkit.WinUI.Controls.SettingsCard>(page.FindName("ApplicationStartupCard"));
            Assert.Contains(startupCard, generalCards);
            var infoSection = Assert.IsType<StackPanel>(VisualTreeHelper.GetParent(startupCard));
            Assert.Contains(infoSection.Children.OfType<TextBlock>(), text => text.Text == viewModel.InfoHeading);
            Assert.Equal(viewModel.ApplicationStartup.Title, startupCard.Header);
            Assert.Equal(viewModel.ApplicationStartup.Description, startupCard.Description);
            var startupToggle = Assert.IsType<ToggleSwitch>(startupCard.Content);
            Assert.True(startupToggle.ActualWidth > 0);
            Assert.True(startupToggle.IsEnabled);
            Assert.False(startupToggle.IsOn);
            Assert.Equal(viewModel.ApplicationStartup.Title, AutomationProperties.GetName(startupToggle));
            var startupTogglePeer = new ToggleSwitchAutomationPeer(startupToggle);
            startupTogglePeer.Toggle();
            await viewModel.ApplicationStartup.WaitForWriteAsync();
            Assert.True(startupToggle.IsOn);
            Assert.True(viewModel.ApplicationStartup.IsEnabled);
            startupTogglePeer.Toggle();
            await viewModel.ApplicationStartup.WaitForWriteAsync();
            Assert.False(startupToggle.IsOn);
            Assert.False(viewModel.ApplicationStartup.IsEnabled);

            router.NavigateToPage(frame, "Settings");
            page = await RenderCurrentSettingsAsync(frame);
            Assert.Same(settingsPage, page);
            Assert.Same(categories, page.FindName("CategoryList"));
            Assert.Equal(0, reloads);
            Assert.Equal(0, unloads);
            Assert.Same(defaultCategory, page.ViewModel.SelectedCategory);
            Assert.True(Assert.IsType<ListView>(page.FindName("CategoryList")).ActualHeight > 100);

            var categoryReset = Assert.IsType<Button>(page.FindName("CategoryResetButton"));
            Assert.Equal(Visibility.Visible, categoryReset.Visibility);
            Assert.True(categoryReset.IsEnabled);
            Assert.Same(viewModel.ResetCommand, categoryReset.Command);
            Assert.Equal(Icon.ArrowReset, Assert.IsType<FluentIcon>(categoryReset.Content).Icon);
            Assert.Equal(viewModel.ResetLabel, AutomationProperties.GetName(categoryReset));
            Assert.Equal(viewModel.ResetLabel, Assert.IsType<string>(ToolTipService.GetToolTip(categoryReset)));
            var language = Assert.IsType<ComboBox>(page.FindName("LanguagePicker"));
            Assert.True(language.ActualWidth > 0);
            Assert.Same(viewModel.Languages, language.ItemsSource);
            Assert.Equal("en", language.SelectedValue);
            language.IsDropDownOpen = true;
            Assert.True(language.IsDropDownOpen);
            language.SelectedValue = "fr";
            language.IsDropDownOpen = false;
            Assert.Equal("fr", viewModel.SelectedLanguage);
            viewModel.SelectedLanguage = "en";
            Assert.Equal("en", language.SelectedValue);
            Assert.Same(viewModel.SelectedThemeOption, Assert.IsType<ComboBox>(page.FindName("ThemePicker")).SelectedItem);
            Assert.Same(viewModel.AppColorPreferences.SelectedOption, Assert.IsType<ComboBox>(page.FindName("AppColorPicker")).SelectedItem);
            var backgroundToggle = Assert.IsType<ToggleSwitch>(page.FindName("WindowBackgroundToggle"));
            Assert.True(backgroundToggle.IsEnabled);
            Assert.False(backgroundToggle.IsOn);
            backgroundToggle.IsOn = true;
            Assert.True(viewModel.WindowBackgroundPreferences.IsBlurEnabled);
            Assert.True(await viewModel.WindowBackgroundPreferences.FlushAsync());

            page.Width = 600;
            await RenderCurrentSettingsAsync(frame);
            var categoryNavigation = Assert.IsType<Grid>(page.FindName("CategoryNavigation"));
            Assert.Same(defaultCategory, viewModel.SelectedCategory);
            Assert.Equal(Visibility.Visible, showCategories.Visibility);
            Assert.Equal(Visibility.Collapsed, categoryNavigation.Visibility);
            ((IInvokeProvider)new ButtonAutomationPeer(showCategories).GetPattern(PatternInterface.Invoke)).Invoke();
            await RenderCurrentSettingsAsync(frame);
            Assert.Null(viewModel.SelectedCategory);
            Assert.Equal(Visibility.Visible, categoryNavigation.Visibility);
            frame.GoBack();
            await RenderCurrentSettingsAsync(frame);
            Assert.Same(defaultCategory, viewModel.SelectedCategory);
            frame.GoForward();
            await RenderCurrentSettingsAsync(frame);
            Assert.Null(viewModel.SelectedCategory);
            categories.SelectedItem = defaultCategory;
            await RenderCurrentSettingsAsync(frame);
            Assert.Same(defaultCategory, viewModel.SelectedCategory);
            Assert.Equal(Visibility.Collapsed, categoryNavigation.Visibility);

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
                page.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, card.Visibility);
                Assert.True(page.ViewModel.ShowCategoryReset);
                Assert.Same(categoryReset, Assert.Single(Descendants<Button>(page),
                    button => button.Command == page.ViewModel.ResetCommand && button != reset && button.Visibility == Visibility.Visible));
                var header = Assert.IsType<Grid>(VisualTreeHelper.GetParent(categoryReset));
                Assert.Contains(Descendants<TextBlock>(header), text => text.Text == viewModel.SelectedTitle);
                var categoryResetOffset = categoryReset.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point());
                Assert.Equal(header.RowDefinitions[0].ActualHeight, categoryResetOffset.Y, 1);
                Assert.Equal(header.ActualWidth, categoryResetOffset.X + categoryReset.ActualWidth, 1);
            }

            Assert.Equal(Visibility.Collapsed, general.Visibility);
            categories.SelectedItem = defaultCategory;
            await RenderCurrentSettingsAsync(frame);
            Assert.Equal(Visibility.Visible, general.Visibility);
            var support = Assert.Single(generalCards, card => card.Command == viewModel.Info.SupportWinOptimizerCommand);
            support.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            Assert.True(VisualStateManager.GoToState(support, "PointerOver", true));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(page, () => Task.CompletedTask);
            var supportSurface = Assert.IsType<Grid>(VisualTreeHelper.GetChild(support, 0));
            Assert.Equal(byte.MaxValue, Assert.IsType<SolidColorBrush>(supportSurface.Background).Color.A);

            // Leaving Settings ends its lifetime; history reentry gets an active, fresh view model.
            frame.Navigate(typeof(NavigationOtherPage));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(frame, () => Task.CompletedTask);
            frame.GoBack();
            page = await RenderCurrentSettingsAsync(frame);
            Assert.NotSame(settingsPage, page);
            Assert.NotSame(viewModel, page.ViewModel);
            Assert.Same(page.ViewModel.Categories[0], page.ViewModel.SelectedCategory);
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
