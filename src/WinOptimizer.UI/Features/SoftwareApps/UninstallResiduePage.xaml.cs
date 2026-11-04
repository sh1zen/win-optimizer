using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;

namespace WinOptimizer.UI.Features.SoftwareApps;

/// <summary>Shows the post-uninstall scan while its workflow retains the inspection session.</summary>
public sealed partial class UninstallResiduePage : Page
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private UninstallResidueViewModel? _viewModel;
    internal Task Closed => _closed.Task;

    public UninstallResiduePage() => InitializeComponent();

    /// <summary>Uses the workflow's current scan without rediscovering installed applications.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _viewModel = e.Parameter as UninstallResidueViewModel
            ?? throw new ArgumentException("An uninstall scan is required.", nameof(e));
        Header.Title = _viewModel.Title;
        FinishButton.Content = _viewModel.FinishLabel;
        FinishButton.IsEnabled = _viewModel.CanClose;
        ReviewHost.Content = new UninstallResidueReviewControl(_viewModel);
        _viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>Retains the session until a cleanup, recovery or rescan has finished.</summary>
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (_viewModel?.CanClose == false) e.Cancel = true;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelChanged;
        ReviewHost.Content = null;
        _closed.TrySetResult();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UninstallResidueViewModel.CanClose))
            FinishButton.IsEnabled = _viewModel!.CanClose;
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.CanClose != true) return;
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(SoftwareAppsPage));
    }
}
