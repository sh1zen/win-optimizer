using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Helpers;
using WinOptimizer.UI.Features.Optimize.Pages;
using WinOptimizer.UI.Features.Optimize.ViewModels;

namespace WinOptimizer.UI.Features.Optimize;

/// <summary>Supplies the optimize catalogue to the common section-page constructor.</summary>
public sealed partial class OptimizePage : Page, ISectionNavigationPage
{
    public OptimizeViewModel ViewModel { get; }
    public NetworkOptimizationViewModel Network { get; } = App.Services.GetRequiredService<NetworkOptimizationViewModel>();
    public FeatureOptimizationViewModel Features { get; } = App.Services.GetRequiredService<FeatureOptimizationViewModel>();
    public bool IsInDetailPage => ViewModel.IsInDetailPage;

    public OptimizePage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<OptimizeViewModel>();
        var routes = OptimizeViewModel.Sections.Select(section => new SectionPageRoute(section, section.Key switch
        {
            "Privacy" => typeof(PrivacyOptimizePage),
            "Power" => typeof(PowerOptimizePage),
            "Gaming" => typeof(GamingOptimizePage),
            "Update" => typeof(UpdateOptimizePage),
            "Notification" => typeof(NotificationOptimizePage),
            "Sound" => typeof(SoundOptimizePage),
            _ => throw new InvalidOperationException($"Missing detail page for {section.Key}.")
        }));
        SectionView.Configure(ViewModel, routes, Icon.Rocket,
            App.Services.GetRequiredService<ILocalizationService>(),
            App.Services.GetRequiredService<IConfigReviewService>(),
            App.Services.GetRequiredService<IEventBus>(), Navigate);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try { await SectionView.ActivateAsync(e.Parameter as SectionPageNavigation); }
        catch (Exception ex) { StartupLogger.Log("OptimizePage", $"Initialization failed: {ex}"); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SectionView.Deactivate();
    }

    public void NavigateToSection(string sectionKey, string? searchText = null) => SectionView.NavigateToSection(sectionKey, searchText);
    public void NavigateToOverview() => SectionView.NavigateToOverview();

    private void Network_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Frame.Navigate(typeof(NetworkOptimizePage));
    private void Features_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Frame.Navigate(typeof(FeaturesOptimizePage));

    /// <summary>Records overview, card and search destinations in the shell's Back/Forward journal.</summary>
    private void Navigate(SectionPageNavigation? destination)
    {
        NavigationRouter.NavigateWithinSection(Frame, destination);
    }
}
