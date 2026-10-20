using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Utilities;

/// <summary>Scopes saved Wi-Fi credentials to a separate page and its navigation lifetime.</summary>
public sealed partial class SavedWifiPage : Page
{
    private CancellationTokenSource? _session;
    public SavedWifiViewModel ViewModel { get; }

    public SavedWifiPage() : this(App.Services.GetRequiredService<SavedWifiViewModel>()) { }

    public SavedWifiPage(SavedWifiViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_session != null) return;
        _session = new CancellationTokenSource();
        await ViewModel.LoadAsync(_session.Token);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _session?.Cancel();
        _session?.Dispose();
        _session = null;
        ViewModel.Clear();
        ViewModel.Dispose();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ConnectionPage));
    }
}
