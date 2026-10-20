using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Shared compact, expandable results list with category and item selection.</summary>
public sealed partial class GroupedResultsList : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<SelectableResultGroup>), typeof(GroupedResultsList), new PropertyMetadata(null));

    public IEnumerable<SelectableResultGroup>? ItemsSource
    {
        get => (IEnumerable<SelectableResultGroup>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public GroupedResultsList() => InitializeComponent();

}
