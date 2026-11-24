using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.UI.Features.Cleaner.ViewModels;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Cleaner;

/// <summary>Hosts scan review, confirmed cleanup and the category reports.</summary>
public sealed partial class CleanerResultsPage : Page
{
    private ContentDialog? _categoryDialog;
    private bool _categoryDetailsQueued;
    public CleanerViewModel ViewModel { get; }

    public CleanerResultsPage() : this(App.Services.GetRequiredService<CleanerViewModel>()) { }

    public CleanerResultsPage(CleanerViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Unloaded += (_, _) => _categoryDialog?.Hide();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A results route is only valid while a completed scan is available.
        if (!ViewModel.HasScanned) ReturnToScan();
    }

    /// <summary>Sizes category navigation and review actions using the available page width.</summary>
    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 720;
        WideLayoutTrigger.IsActive = wide;
        CompactLayoutTrigger.IsActive = !wide;
    }

    /// <summary>Opens the selected category within the shared modal frame and follows window resizing.</summary>
    private async void ShowCategoryDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_categoryDetailsQueued || _categoryDialog is not null
            || sender is not Button { CommandParameter: CleanerCategoryReport category }) return;

        _categoryDetailsQueued = true;
        await PopupHandler.RunModalAsync(async () =>
        {
            _categoryDetailsQueued = false;
            if (!IsLoaded || XamlRoot is not { } root || !ViewModel.HasCleanupReport || !ViewModel.IsIdle
                || !ViewModel.CategoryReports.Contains(category)) return ContentDialogResult.None;

            var content = new ContentControl
            {
                Content = category,
                ContentTemplate = (DataTemplate)Resources["CategoryDetailsTemplate"],
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsTabStop = false
            };
            var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
            {
                Title = category.Title,
                Content = content,
                CloseButtonText = ViewModel.CloseLabel,
                DefaultButton = ContentDialogButton.Close
            });
            dialog.Resources["ContentDialogMaxWidth"] = 560d;

            void Resize(XamlRoot changedRoot, XamlRootChangedEventArgs? _)
            {
                content.Width = Math.Clamp(changedRoot.Size.Width - 96, 0, 488);
                content.MaxHeight = Math.Max(0, changedRoot.Size.Height - 180);
            }

            Resize(root, null);
            root.Changed += Resize;
            _categoryDialog = dialog;
            // The next card can queue as soon as closing starts. The shared queue waits for the transition.
            dialog.Closing += (_, _) => _categoryDialog = null;
            try { return await PopupHandler.ShowDialogAsync(dialog, root); }
            finally
            {
                root.Changed -= Resize;
                dialog.Content = null;
                _categoryDialog = null;
            }
        });
    }

    private void ReturnToScan()
    {
        if (Frame.BackStack.LastOrDefault()?.SourcePageType == typeof(CleanerPage))
            Frame.GoBack();
        else
            Frame.Navigate(typeof(CleanerPage));
    }
}
