using System.ComponentModel;
using FluentIcons.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Events.Settings;
using WinOptimizer.Core.Features.Common.Events.UI;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.ViewModels;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Constructs section pages, owning their shared search, navigation, cards and presentation subscriptions.</summary>
public sealed partial class SectionPageView : UserControl
{
    private IConfigReviewService _review = null!;
    private IEventBus _events = null!;
    private ISubscriptionToken? _appliedSubscription;
    private ISubscriptionToken? _refreshedSubscription;
    private bool _active;
    private int _activation;
    private bool _refreshQueued;
    private bool _applySearchOnRefresh;
    private Action<SectionPageNavigation?> _navigate = null!;

    public ISectionPageViewModel ViewModel { get; private set; } = null!;
    public IReadOnlyList<SectionNavigationItem> Items { get; private set; } = [];
    public Icon Icon { get; private set; }

    public static readonly DependencyProperty OverviewFooterProperty = DependencyProperty.Register(
        nameof(OverviewFooter), typeof(UIElement), typeof(SectionPageView), new PropertyMetadata(null));

    /// <summary>Allows a section owner to add a tool destination below its settings categories.</summary>
    public UIElement? OverviewFooter
    {
        get => (UIElement?)GetValue(OverviewFooterProperty);
        set => SetValue(OverviewFooterProperty, value);
    }

    public SectionPageView()
    {
        InitializeComponent();
        PageScrollHelper.Attach(this, OverviewScrollView);
        SizeChanged += OnSizeChanged;
    }

    /// <summary>Receives the page's catalogue and dependencies explicitly before its first load.</summary>
    public void Configure(ISectionPageViewModel viewModel, IEnumerable<SectionPageRoute> routes, Icon icon,
        ILocalizationService localization, IConfigReviewService review, IEventBus events,
        Action<SectionPageNavigation?> navigate)
    {
        ViewModel = viewModel;
        Icon = icon;
        _review = review;
        _events = events;
        _navigate = navigate;
        Items = routes.Select(route => new SectionNavigationItem(route,
            viewModel.GetSectionViewModel(route.Section.Key)
                ?? throw new InvalidOperationException($"Missing feature for section {route.Section.Key}."),
            localization, review)).ToArray();
    }

    /// <summary>Initializes settings and scopes subscriptions to the cached page's active navigation lifetime.</summary>
    public async Task ActivateAsync(SectionPageNavigation? destination = null)
    {
        Deactivate();
        _active = true;
        int activation = ++_activation;
        ViewModel.PropertyChanged += OnViewModelChanged;
        _review.ReviewModeChanged += OnReviewModeChanged;
        _review.BadgeStateChanged += OnBadgeStateChanged;
        foreach (var item in Items) item.Feature.PropertyChanged += OnFeatureChanged;
        _appliedSubscription = _events.Subscribe<SettingAppliedEvent>(_ => QueueRefresh(false));
        _refreshedSubscription = _events.Subscribe<SettingsRefreshedEvent>(_ => QueueRefresh(true));
        ShowDestination(destination);
        await ViewModel.InitializeAsync();
        if (_active && _activation == activation) RefreshPresentation(true);
    }

    /// <summary>Releases subscriptions without disposing singleton feature view models or cached settings.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        ++_activation;
        _refreshQueued = false;
        _applySearchOnRefresh = false;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        _review.ReviewModeChanged -= OnReviewModeChanged;
        _review.BadgeStateChanged -= OnBadgeStateChanged;
        foreach (var item in Items) item.Feature.PropertyChanged -= OnFeatureChanged;
        _appliedSubscription?.Dispose();
        _appliedSubscription = null;
        _refreshedSubscription?.Dispose();
        _refreshedSubscription = null;
        ViewModel.OnNavigatedFrom();
    }

    /// <summary>Routes suggestions and category clicks through the same detail-navigation path.</summary>
    public void NavigateToSection(string sectionKey, string? searchText = null)
    {
        _navigate(new SectionPageNavigation(sectionKey, searchText));
    }

    /// <summary>Renders the shell journal's destination without creating a second navigation history.</summary>
    private void ShowDestination(SectionPageNavigation? destination)
    {
        var item = Items.FirstOrDefault(item => item.Key == destination?.SectionKey);
        if (item == null)
        {
            ViewModel.CurrentSectionKey = "Overview";
            InnerContentFrame.Content = null;
            UpdateContentVisibility();
            RefreshPresentation(false);
            return;
        }

        ViewModel.CurrentSectionKey = item.Key;
        var searchText = destination?.SearchText;
        if (!string.IsNullOrWhiteSpace(searchText)) item.Feature.ApplySearchFilter(searchText);
        if (!InnerContentFrame.Navigate(item.Route.PageType, searchText))
            throw new InvalidOperationException($"Cannot navigate to section {item.Key}.");
        if (_review.IsInReviewMode) _review.MarkFeatureVisited(item.Route.Section.ModuleId);
    }

    /// <summary>Returns to the overview and recomputes badges from the current setting state.</summary>
    public void NavigateToOverview() => _navigate(null);

    private void InnerContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        ViewModel.CurrentSectionKey = Items.Single(item => item.Route.PageType == e.SourcePageType).Key;
        UpdateContentVisibility();
        RefreshPresentation(true);
    }

    /// <summary>Shows the selected detail page or the category overview.</summary>
    private void UpdateContentVisibility()
    {
        bool detail = ViewModel.IsInDetailPage;
        OverviewContent.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        InnerContentFrame.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPresentation(bool applySearch)
    {
        foreach (var item in Items)
        {
            if (applySearch) item.Feature.ApplySearchFilter(ViewModel.SearchText);
            item.Refresh();
        }
    }

    /// <summary>Ignores queued updates after leaving the page, including a later cached activation.</summary>
    private void QueueRefresh(bool applySearch)
    {
        if (!_active) return;
        _applySearchOnRefresh |= applySearch;
        if (_refreshQueued) return;
        _refreshQueued = true;
        int activation = _activation;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_active || _activation != activation) return;
            bool filter = _applySearchOnRefresh;
            _applySearchOnRefresh = false;
            _refreshQueued = false;
            RefreshPresentation(filter);
        });
    }

    private void OnReviewModeChanged(object? sender, EventArgs e) => QueueRefresh(true);
    private void OnBadgeStateChanged(object? sender, EventArgs e) => QueueRefresh(false);
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.PageTitle)) QueueRefresh(false);
    }

    private void OnFeatureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ISettingsFeatureViewModel.DisplayName)
            or nameof(ISettingsFeatureViewModel.GroupDescriptionText) or nameof(ISettingsFeatureViewModel.Settings))
            QueueRefresh(e.PropertyName == nameof(ISettingsFeatureViewModel.Settings));
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SectionNavigationItem item })
        {
            NavigateToSection(item.Key);
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchSuggestionItem suggestion) NavigateToSection(suggestion.SectionKey, suggestion.SettingName);
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is SearchSuggestionItem suggestion) NavigateToSection(suggestion.SectionKey, suggestion.SettingName);
    }

    /// <summary>Places search below the heading when the page's own width cannot fit both.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= 800;
        HeaderLayout.RowSpacing = wide ? 0 : 16;
        Grid.SetRow(SearchBox, wide ? 0 : 1);
        Grid.SetColumn(SearchBox, wide ? 1 : 0);
        Grid.SetColumnSpan(SearchBox, wide ? 1 : 2);
        SearchBox.Width = wide ? 260 : double.NaN;
        SearchBox.Margin = wide ? new Thickness(24, 0, 0, 0) : new Thickness(0);
        SearchBox.VerticalAlignment = VerticalAlignment.Center;
    }
}
