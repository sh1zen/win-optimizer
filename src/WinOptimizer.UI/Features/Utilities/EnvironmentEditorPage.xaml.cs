using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Utilities;

/// <summary>Loads the environment editor explicitly and preserves its draft through page navigation.</summary>
public sealed partial class EnvironmentEditorPage : Page
{
    public EnvironmentEditorViewModel ViewModel { get; } = App.Services.GetRequiredService<EnvironmentEditorViewModel>();
    public EnvironmentEditorPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.LoadAsync();
        // Use the page's available width, including when the shell sidebar takes part of the window.
        SizeChanged += (_, args) => VisualStateManager.GoToState(this, args.NewSize.Width >= 1000 ? "WideEditor" : "NarrowEditor", false);
    }

    private void Variables_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Filtering the list must not turn an existing variable's draft into a new variable.
        if (e.AddedItems.FirstOrDefault() is EnvironmentVariableEntry variable) ViewModel.SelectedVariable = variable;
    }

    private void PathEntry_GettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (sender is TextBox { DataContext: EnvironmentPathEntryViewModel entry }) ViewModel.SelectedPathEntry = entry;
    }

    private void AddPathEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.AddPathEntryCommand.CanExecute(null)) return;
        ViewModel.AddPathEntryCommand.Execute(null);
        FocusSelectedPathEntry();
    }

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.AddPathEntryCommand.CanExecute(null)) return;
        var path = App.Services.GetRequiredService<IFilePickerService>().PickFolder(ViewModel.BrowsePathLabel);
        if (path == null) return;
        ViewModel.AddPathEntryCommand.Execute(path);
        FocusSelectedPathEntry();
    }

    /// <summary>Brings a newly added row into view and places the caret in its native text editor.</summary>
    private void FocusSelectedPathEntry()
    {
        PathEntriesList.ScrollIntoView(ViewModel.SelectedPathEntry);
        PathEntriesList.UpdateLayout();
        if (PathEntriesList.ContainerFromItem(ViewModel.SelectedPathEntry) is not DependencyObject container) return;
        FindEditor(container)?.Focus(FocusState.Programmatic);
    }

    private static TextBox? FindEditor(DependencyObject parent)
    {
        if (parent is TextBox editor) return editor;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindEditor(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
        return null;
    }
}
