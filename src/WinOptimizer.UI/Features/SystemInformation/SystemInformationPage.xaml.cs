using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.SystemInformation.ViewModels;

namespace WinOptimizer.UI.Features.SystemInformation;

/// <summary>Collects details when displayed and cancels publication when the user leaves.</summary>
public sealed partial class SystemInformationPage : Page
{
    public SystemInformationViewModel ViewModel { get; } = App.Services.GetRequiredService<SystemInformationViewModel>();
    public SystemInformationPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
    }

    /// <summary>Uses a compact category picker when two reading panes would be too narrow.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WideLayoutTrigger.IsActive = e.NewSize.Width >= 1000;
        CompactLayoutTrigger.IsActive = !WideLayoutTrigger.IsActive;
    }

    /// <summary>Opens the same category shown in the navigation from its summary row.</summary>
    private void OverviewList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SystemInformationCategoryViewModel category)
            ViewModel.SelectedCategory = category;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) { ViewModel.Dispose(); base.OnNavigatedFrom(e); }
}
