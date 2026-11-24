using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Drivers.ViewModels;

namespace WinOptimizer.UI.Features.Drivers;

/// <summary>Arranges the result table and actions for the current category and available width.</summary>
public sealed partial class DriverResultsView : UserControl
{
    public DriversViewModel ViewModel { get; }

    internal DriverResultsView(DriversViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnResultsChanged;
            UpdateResultLayout();
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnResultsChanged;
    }

    private void ResultsLayout_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResultLayout();

    private void OnResultsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DriversViewModel.SelectedGroup))
            UpdateResultLayout();
    }

    private void UpdateResultLayout()
    {
        var width = ResultsLayout.ActualWidth;
        var useTabs = width >= 860;
        CategoryTabs.Visibility = useTabs ? Visibility.Visible : Visibility.Collapsed;
        CategoryPicker.Visibility = useTabs ? Visibility.Collapsed : Visibility.Visible;
        var useComparisonColumns = width >= 800 && ViewModel.IsCandidateCategory;
        CandidateColumns.Visibility = useComparisonColumns ? Visibility.Visible : Visibility.Collapsed;
        InventoryColumns.Visibility = useComparisonColumns ? Visibility.Collapsed : Visibility.Visible;
        var template = (DataTemplate)Resources[useComparisonColumns ? "CandidateRowTemplate" : "InventoryRowTemplate"];
        if (!ReferenceEquals(DriverRows.ItemTemplate, template)) DriverRows.ItemTemplate = template;

        InventoryNameColumn.Text = ViewModel.HasDeviceSearch ? ViewModel.DeviceLabel :
            ViewModel.IsCandidateCategory ? ViewModel.DriverLabel : ViewModel.NameLabel;
        InventoryDateColumn.Text = ViewModel.HasDeviceSearch ? ViewModel.DriverDateLabel :
            ViewModel.IsCandidateCategory ? ViewModel.ReleasedLabel : ViewModel.DateLabel;
        InventorySizeColumn.Text = ViewModel.HasDeviceSearch ? ViewModel.PackageSizeLabel : ViewModel.SizeLabel;
        var hasInventory = ViewModel.SelectedGroup?.LabelKey != "Drivers_Sources";
        InventoryDateColumn.Visibility = InventorySizeColumn.Visibility = hasInventory ? Visibility.Visible : Visibility.Collapsed;

        var inlineActions = width >= 670;
        PlaceActionRow(CategoryHeading, CategoryTools, inlineActions);
        PlaceActionRow(SelectionLayout, SelectionActions, inlineActions);
    }

    /// <summary>Keeps actions alongside their labels where space permits.</summary>
    private static void PlaceActionRow(Grid layout, FrameworkElement actions, bool inline)
    {
        layout.RowSpacing = inline ? 0 : 8;
        Grid.SetRow(actions, inline ? 0 : 1);
        Grid.SetColumn(actions, inline ? 1 : 0);
        Grid.SetColumnSpan(actions, inline ? 1 : 2);
        actions.HorizontalAlignment = inline ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    }
}
