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
using WinOptimizer.UI.Features.Customize.Pages;
using WinOptimizer.UI.Features.Customize.ViewModels;

namespace WinOptimizer.UI.Features.Customize;

/// <summary>Supplies the customize catalogue to the common section-page constructor.</summary>
public sealed partial class CustomizePage : Page, ISectionNavigationPage
{
    public CustomizeViewModel ViewModel { get; }
    public ContextMenuViewModel ContextMenus { get; } = App.Services.GetRequiredService<ContextMenuViewModel>();
    public bool IsInDetailPage => ViewModel.IsInDetailPage;

    public CustomizePage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<CustomizeViewModel>();
        var routes = CustomizeViewModel.Sections.Reverse().Select(section => new SectionPageRoute(section, section.Key switch
        {
            "Explorer" => typeof(ExplorerCustomizePage),
            "StartMenu" => typeof(StartMenuCustomizePage),
            "Taskbar" => typeof(TaskbarCustomizePage),
            "WindowsTheme" => typeof(WindowsThemeCustomizePage),
            _ => throw new InvalidOperationException($"Missing detail page for {section.Key}.")
        }));
        SectionView.Configure(ViewModel, routes, Icon.DesignIdeas,
            App.Services.GetRequiredService<ILocalizationService>(),
            App.Services.GetRequiredService<IConfigReviewService>(),
            App.Services.GetRequiredService<IEventBus>(), Navigate);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try { await SectionView.ActivateAsync(e.Parameter as SectionPageNavigation); }
        catch (Exception ex) { StartupLogger.Log("CustomizePage", $"Initialization failed: {ex}"); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SectionView.Deactivate();
    }

    public void NavigateToSection(string sectionKey, string? searchText = null) => SectionView.NavigateToSection(sectionKey, searchText);
    public void NavigateToOverview() => SectionView.NavigateToOverview();

    /// <summary>Uses the shell journal for the on-demand registered-program editor.</summary>
    private void ContextMenus_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        Frame.Navigate(typeof(ContextMenuCustomizePage));

    /// <summary>Records overview, card and search destinations in the shell's Back/Forward journal.</summary>
    private void Navigate(SectionPageNavigation? destination)
    {
        NavigationRouter.NavigateWithinSection(Frame, destination);
    }
}
