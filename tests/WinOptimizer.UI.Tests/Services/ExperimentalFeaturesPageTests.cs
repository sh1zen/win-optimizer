using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Moq;
using Windows.Graphics;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.UI.Features.AdvancedTools;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Exercises the Advanced Tools route and feature-scoped controls using fake discovery and writes.</summary>
public sealed class ExperimentalFeaturesPageTests
{
    [Fact]
    public async Task RightAlignedToolbarAndDetailsPopupFitBothWidthsAndKeepActionsWithTheirFeature()
    {
        var service = App.Services.GetRequiredService<Mock<IExperimentalFeatures>>();
        var snapshot = CreateSnapshot();
        service.Setup(features => features.DiscoverAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var request = new TaskCompletionSource<(string Key, ExperimentalAction Action)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ExperimentalChangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Setup(features => features.ChangeAsync(It.IsAny<string>(), It.IsAny<ExperimentalAction>(), It.IsAny<CancellationToken>()))
            .Returns((string key, ExperimentalAction action, CancellationToken _) =>
            {
                request.TrySetResult((key, action));
                return completion.Task;
            });

        var frame = new Frame();
        var window = new Window { Content = frame };
        try
        {
            frame.Navigate(typeof(AdvancedToolsPage));
            window.Activate();
            await RenderAsync(frame);
            var scale = frame.XamlRoot.RasterizationScale;
            window.AppWindow.ResizeClient(new SizeInt32((int)(1200 * scale), (int)(800 * scale)));
            var overview = Assert.IsType<AdvancedToolsPage>(frame.Content);
            Assert.Equal("Nav_AdvancedTools", VisibleHeadings(overview).Single().Title);
            var cards = Descendants<NavigationCard>(overview).ToArray();
            Assert.NotEmpty(cards);
            Assert.All(cards, card =>
            {
                var icon = Assert.IsType<PathIcon>(card.Icon);
                var outline = Assert.IsType<PathGeometry>(icon.Data);
                Assert.Equal(FillRule.Nonzero, outline.FillRule);
            });

            overview.NavigateToSection("ExperimentalFeatures");
            await RenderAsync(frame);
            var section = Assert.IsType<AdvancedToolsPage>(frame.Content);
            Assert.Equal("Experimental_Title", VisibleHeadings(section).Single().Title);
            var page = Descendants<ExperimentalFeaturesPage>(section).Single();
            var catalog = Assert.IsType<ItemsControl>(page.FindName("Catalog"));
            Assert.Equal(2, catalog.Items.Count);

            var toolbar = Assert.IsAssignableFrom<Panel>(page.FindName("Toolbar"));
            Assert.Equal(HorizontalAlignment.Right, toolbar.HorizontalAlignment);
            var search = Assert.IsType<TextBox>(page.FindName("SearchBox"));
            Assert.Same(search, toolbar.Children[0]);
            Assert.Same(page.FindName("RefreshButton"), toolbar.Children[1]);
            Assert.Same(page.FindName("ImportProfilesButton"), toolbar.Children[2]);
            var details = DetailsButtons(catalog).ToArray();
            Assert.Equal(2, details.Length);
            Assert.All(details, button => Assert.Equal(1, Grid.GetColumn(Assert.IsAssignableFrom<FrameworkElement>(button.Parent))));
            Assert.Single(Descendants<TextBlock>(catalog), title => title.Text == "Experimental_Pointer_Name");
            var catalogHeight = catalog.ActualHeight;
            Invoke(details[1]);
            var detailScroll = await WaitForPopupElementAsync<ScrollViewer>(frame, scroll => scroll.Name == "DetailsScroll");
            Assert.InRange(Math.Abs(catalogHeight - catalog.ActualHeight), 0, 1);
            Assert.Equal(new Uri("https://example.com/Pointer"), Descendants<HyperlinkButton>(detailScroll).Single().NavigateUri);
            var idCards = Descendants<Border>(detailScroll).Where(card => card.Name == "FeatureIdCard").ToArray();
            Assert.Equal(6, idCards.Length);
            var firstId = Descendants<TextBlock>(idCards[0]).ToArray();
            Assert.Contains(firstId, text => text.Text == "2" && text.IsTextSelectionEnabled);
            Assert.Contains(firstId, text => text.Text == "Experimental_Configured");
            Assert.Contains(firstId, text => text.Text == "Experimental_Runtime");
            Assert.Contains(firstId, text => text.Text == "Experimental_Startup");

            window.AppWindow.ResizeClient(new SizeInt32((int)(540 * scale), (int)(630 * scale)));
            await RenderAsync(frame);
            Assert.All(details, button => Assert.Equal(1, Grid.GetRow(Assert.IsAssignableFrom<FrameworkElement>(button.Parent))));
            Assert.True(((ScrollViewer)page.Content).ScrollableWidth <= 1);
            Assert.InRange(detailScroll.ActualWidth, 1, frame.ActualWidth - 1);
            Assert.InRange(detailScroll.ActualHeight, 1, frame.ActualHeight - 1);
            Assert.True(detailScroll.ScrollableHeight > 0);
            Assert.True(detailScroll.ScrollableWidth <= 1);

            Invoke(Assert.Single(PopupElements<Button>(frame), button => button.Name == "CloseButton"));
            await RenderAsync(frame);
            Assert.DoesNotContain(PopupElements<ScrollViewer>(frame), scroll => scroll.Name == "DetailsScroll");

            search.Text = "Pointer";
            await RenderAsync(frame);
            Assert.Single(catalog.Items);
            Invoke(Assert.Single(DetailsButtons(catalog)));
            var restore = await WaitForPopupElementAsync<Button>(frame, button =>
                button.CommandParameter is ExperimentalFeatureAction { Action: ExperimentalAction.RestoreDefault });
            var enable = Assert.Single(Descendants<Button>(catalog), button =>
                button.CommandParameter is ExperimentalFeatureAction { Action: ExperimentalAction.Enable });
            Assert.True(enable.IsEnabled);
            Invoke(restore);
            Assert.Equal(("Pointer", ExperimentalAction.RestoreDefault), await request.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(enable.IsEnabled);
            Assert.DoesNotContain(PopupElements<ScrollViewer>(frame), scroll => scroll.Name == "DetailsScroll");
            var change = Assert.IsType<ExperimentalFeatureCard>(Assert.Single(catalog.Items)).Change;
            service.Setup(features => features.DiscoverAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot with
            {
                UndoFeature = "Pointer", RecoveryRequired = true
            });
            completion.SetResult(new([], false, true));
            await change.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));
            var undo = await WaitForPopupElementAsync<Button>(frame, button =>
                button.CommandParameter is ExperimentalFeatureAction { Action: ExperimentalAction.Undo, FeatureKey: "Pointer" });
            Assert.True(undo.IsEnabled);
            Assert.Contains(PopupElements<InfoBar>(frame), notice => notice.IsOpen && notice.Message == "Experimental_RecoveryRequired");

            frame.GoBack();
            await RenderAsync(frame);
            Assert.Equal("Nav_AdvancedTools", VisibleHeadings(frame).Single().Title);
            Assert.DoesNotContain(PopupElements<ScrollViewer>(frame), scroll => scroll.Name == "DetailsScroll");
        }
        finally
        {
            completion.TrySetCanceled();
            window.Close();
        }
    }

    private static ExperimentalDiscovery CreateSnapshot()
    {
        var target = new ExperimentalBuild(26300, 10075, [Architecture.X64]);
        var features = new[] { Feature("Search", 1), Feature("Pointer", 2) };
        return new(new(10, 0, target.Build, target.Revision, Architecture.X64, "26H2", true, 0, true, true),
            features.Select(feature => new ExperimentalFeatureStatus(feature,
                new(ExperimentalCompatibility.Supported, "Experimental_Supported"), [], null, null, null)).ToArray(),
            features.SelectMany(feature => feature.Requirements).ToDictionary(requirement => requirement.Id,
                requirement => new ExperimentalObservation(requirement.Id, null, null, null, 0)),
            new Dictionary<uint, ExperimentalRestart>(), null, null, false);

        ExperimentalFeature Feature(string key, uint id) => new(key, "Experimental_Shell",
            [.. Enumerable.Range((int)id, key == "Pointer" ? 6 : 1).Select(featureId => new ExperimentalRequirement(
                (uint)featureId, new(FeatureOverrideState.Enabled), new(FeatureOverrideState.Disabled)))],
            [target], [], [new Uri($"https://example.com/{key}")], true);
    }

    private static IEnumerable<Button> DetailsButtons(ItemsControl catalog) =>
        Descendants<Button>(catalog).Where(button => button.Name == "DetailsButton");

    private static IEnumerable<T> PopupElements<T>(FrameworkElement root) where T : DependencyObject =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(popup => Descendants<T>(popup.Child));

    /// <summary>Waits for native modal transitions and automation clicks to reach the visual tree.</summary>
    private static async Task<T> WaitForPopupElementAsync<T>(FrameworkElement root, Predicate<T> matches) where T : FrameworkElement
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await RenderAsync(root);
            if (PopupElements<T>(root).SingleOrDefault(element => matches(element)) is { } match) return match;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static Task RenderAsync(FrameworkElement root) => StartupUiCoordinator.RunAfterFirstRenderAsync(root, () =>
    {
        root.UpdateLayout();
        return Task.CompletedTask;
    });

    private static IEnumerable<PageHeader> VisibleHeadings(DependencyObject root) =>
        Descendants<PageHeader>(root).Where(heading => heading.IsLoaded && heading.Visibility == Visibility.Visible);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
