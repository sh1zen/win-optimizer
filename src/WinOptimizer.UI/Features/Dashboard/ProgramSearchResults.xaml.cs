using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WinOptimizer.UI.Features.Dashboard;

/// <summary>Presents search results over the current shell page without changing its navigation state.</summary>
public sealed partial class ProgramSearchResults : UserControl
{
    internal event Action? DismissRequested;
    internal event Action<ProgramSearchResult>? ResultInvoked;

    public ProgramSearchResults() => InitializeComponent();

    internal void SetLabels(string heading, string close)
    {
        Heading.Text = heading;
        AutomationProperties.SetName(CloseButton, close);
        ToolTipService.SetToolTip(CloseButton, close);
    }

    /// <summary>Shows progress, results, or feedback in the same page area.</summary>
    internal void Present(IReadOnlyList<ProgramSearchResult>? results, string status = "", bool searching = false)
    {
        ResultsList.ItemsSource = results;
        SearchStatus.Text = status;
        SearchProgress.IsActive = searching;
        SearchProgress.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    internal void Hide()
    {
        Visibility = Visibility.Collapsed;
        ResultsList.ItemsSource = null;
        SearchProgress.IsActive = false;
    }

    /// <summary>Moves keyboard focus from the title-bar field into the list.</summary>
    internal bool FocusFirstResult() => ResultsList.ContainerFromIndex(0) is ListViewItem { ContentTemplateRoot: Button first } &&
        first.Focus(FocusState.Keyboard);

    private void Result_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProgramSearchResult result }) ResultInvoked?.Invoke(result);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke();
}
