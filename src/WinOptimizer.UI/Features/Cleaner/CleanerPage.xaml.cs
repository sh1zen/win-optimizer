using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using WinOptimizer.UI.Features.Cleaner.ViewModels;
using WinOptimizer.UI.Features.Management;

namespace WinOptimizer.UI.Features.Cleaner;

/// <summary>Hosts Cleaner and explicitly initializes its saved choices when displayed.</summary>
public sealed partial class CleanerPage : Page
{
    public CleanerViewModel ViewModel { get; } = App.Services.GetRequiredService<CleanerViewModel>();
    public CleanerPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnNavigatedFrom(e);
    }

    /// <summary>Opens review only when this visible page's scan has finished successfully.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.HasScanned) && ViewModel.HasScanned)
            OpenResults();
    }

    private void ReviewResults_Click(object sender, RoutedEventArgs e) => OpenResults();

    /// <summary>Opens event-log maintenance in Cleaner's native Back/Forward journal.</summary>
    private void EventsCleanupCard_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CurrentSourcePageType != typeof(EventsCleanupPage)) Frame.Navigate(typeof(EventsCleanupPage));
    }

    private void OpenResults()
    {
        if (ViewModel.HasScanned && Frame is not null && ReferenceEquals(Frame.Content, this))
            Frame.Navigate(typeof(CleanerResultsPage), null, new DrillInNavigationTransitionInfo());
    }

    /// <summary>Positions the scan action using the page's available space, including sidebar and app zoom.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 760;
        WideLayoutTrigger.IsActive = wide;
        CompactLayoutTrigger.IsActive = !wide;
    }
}
