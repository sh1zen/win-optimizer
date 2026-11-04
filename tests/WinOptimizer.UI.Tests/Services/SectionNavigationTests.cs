using System.Collections.ObjectModel;
using FluentIcons.Common;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Moq;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using WinOptimizer.UI.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Exercises section history and shell routing on a real WinUI Frame with fake settings.</summary>
public sealed class SectionNavigationTests
{
    [Fact]
    public void BackReturnsToSectionOverviewAndForwardRestoresTheDetail()
    {
        var frame = new Frame();
        frame.Navigate(typeof(NavigationOtherPage));
        frame.Navigate(typeof(NavigationSectionPage));
        var page = Assert.IsType<NavigationSectionPage>(frame.Content);
        page.Sections.NavigateToSection("First");
        Assert.True(page.IsInDetailPage);
        Assert.Equal(2, frame.BackStack.Count);

        frame.GoBack();
        Assert.Same(page, frame.Content);
        Assert.False(page.IsInDetailPage);
        Assert.True(frame.CanGoForward);
        NavigationRouter.NavigateWithinSection(frame);
        Assert.True(frame.CanGoForward);
        frame.GoForward();
        Assert.True(page.IsInDetailPage);
        Assert.Equal("First", page.ViewModel.CurrentSectionKey);
        frame.GoBack();
        frame.GoBack();
        Assert.IsType<NavigationOtherPage>(frame.Content);
    }

    [Fact]
    public void ReturningHomeRecordsTheOverviewAndRepeatedHomeClicksPreserveHistory()
    {
        var frame = new Frame();
        frame.Navigate(typeof(NavigationSectionPage));
        var page = Assert.IsType<NavigationSectionPage>(frame.Content);
        NavigationRouter.NavigateWithinSection(frame);
        Assert.Empty(frame.BackStack);
        page.Sections.NavigateToSection("Second");
        page.Sections.NavigateToOverview();
        Assert.False(page.IsInDetailPage);
        Assert.Equal(2, frame.BackStack.Count);
        NavigationRouter.NavigateWithinSection(frame);
        Assert.Equal(2, frame.BackStack.Count);
        frame.GoBack();
        Assert.Equal("Second", page.ViewModel.CurrentSectionKey);
        frame.GoForward();
        Assert.False(page.IsInDetailPage);
    }

    [Fact]
    public void SearchDestinationsSurviveForwardAndCachedReentryFromAnotherSection()
    {
        var frame = new Frame();
        frame.Navigate(typeof(NavigationSectionPage));
        var page = Assert.IsType<NavigationSectionPage>(frame.Content);
        page.Sections.NavigateToSection("First", "taskbar setting");
        Assert.Equal("taskbar setting", NavigationDetailPage.LastParameter);
        frame.Navigate(typeof(NavigationOtherPage));
        frame.GoBack();
        Assert.Same(page, frame.Content);
        Assert.Equal("First", page.ViewModel.CurrentSectionKey);
        Assert.Equal("taskbar setting", NavigationDetailPage.LastParameter);
        frame.GoBack();
        Assert.False(page.IsInDetailPage);
        page.Sections.NavigateToSection("Second");
        Assert.Equal("Second", page.ViewModel.CurrentSectionKey);
        Assert.False(frame.CanGoForward);
    }
}

/// <summary>Hosts the production section control and navigation owner with synthetic feature boundaries.</summary>
public sealed partial class NavigationSectionPage : Page, ISectionNavigationPage
{
    public SectionPageView Sections { get; } = new();
    public ISectionPageViewModel ViewModel { get; }
    public bool IsInDetailPage => ViewModel.IsInDetailPage;

    public NavigationSectionPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        var model = new Mock<ISectionPageViewModel>();
        model.SetupProperty(vm => vm.CurrentSectionKey, "Overview");
        model.SetupGet(vm => vm.IsInDetailPage).Returns(() => model.Object.CurrentSectionKey != "Overview");
        model.SetupGet(vm => vm.SearchText).Returns("");
        model.Setup(vm => vm.InitializeAsync()).Returns(Task.CompletedTask);
        var feature = new Mock<ISettingsFeatureViewModel>();
        feature.SetupGet(vm => vm.Settings).Returns(new ObservableCollection<SettingItemViewModel>());
        model.Setup(vm => vm.GetSectionViewModel(It.IsAny<string>())).Returns(feature.Object);
        ViewModel = model.Object;
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        var routes = new[] { "First", "Second" }.Select((key, index) =>
        {
            var section = new Mock<ISectionInfo>();
            section.SetupGet(item => item.Key).Returns(key);
            section.SetupGet(item => item.ModuleId).Returns(key);
            return new SectionPageRoute(section.Object,
                index == 0 ? typeof(NavigationDetailPage) : typeof(NavigationSecondDetailPage));
        });
        Sections.Configure(ViewModel, routes, Icon.Settings, localization.Object,
            new Mock<IConfigReviewService>().Object, new Mock<IEventBus>().Object,
            destination => NavigationRouter.NavigateWithinSection(Frame, destination));
        Content = Sections;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e) =>
        await Sections.ActivateAsync(e.Parameter as SectionPageNavigation);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Sections.Deactivate();
}

/// <summary>Represents another sidebar section without OS calls.</summary>
public sealed partial class NavigationOtherPage : Page
{
    public NavigationOtherPage() => InitializeComponent();
}

/// <summary>Captures the search parameter passed to a detail by the production section control.</summary>
public sealed partial class NavigationDetailPage : Page
{
    public NavigationDetailPage() => InitializeComponent();
    public static object? LastParameter { get; private set; }
    protected override void OnNavigatedTo(NavigationEventArgs e) => LastParameter = e.Parameter;
}

/// <summary>Provides an independent second category for history branching checks.</summary>
public sealed partial class NavigationSecondDetailPage : Page
{
    public NavigationSecondDetailPage() => InitializeComponent();
}
