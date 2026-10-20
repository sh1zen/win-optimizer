using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.Restore.ViewModels;

namespace WinOptimizer.UI.Features.Restore;

/// <summary>Loads the restore inventory only when its page is explicitly opened.</summary>
public sealed partial class RestorePage : Page
{
    public RestoreViewModel ViewModel { get; } = App.Services.GetRequiredService<RestoreViewModel>();
    public RestorePage() => InitializeComponent();
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshRestorePointsCommand.ExecuteAsync(null);
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Dispose();
        base.OnNavigatedFrom(e);
    }
}
