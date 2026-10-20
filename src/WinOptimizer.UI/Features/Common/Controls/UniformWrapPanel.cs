using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinOptimizer.UI.Features.Common.Helpers;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>
/// Non-virtualising panel that lays children out in a wrapping grid: every
/// cell uses ItemWidth and a minimum ItemHeight, gaps are fixed at ColumnSpacing /
/// RowSpacing, items are left-aligned within the available width. The
/// SoftwareAppsPage centres its containing StackPanel (with a MaxWidth
/// computed from card column count) so that headers, select-all checkboxes,
/// and the card grid all share the same horizontal extents — matches the
/// Microsoft Store / Windows Settings card-grid pattern.
/// StretchItems opts into filling the available width: ItemWidth becomes the
/// minimum card width and MaxColumns limits the row, while ItemHeight remains a floor.
///
/// Replaces ItemsRepeater + UniformGridLayout, which produced a measure
/// cycle that snapped the outer ScrollViewer back to the top.
/// </summary>
public sealed partial class UniformWrapPanel : Panel
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(UniformWrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(UniformWrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(ColumnSpacing),
            typeof(double),
            typeof(UniformWrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(
            nameof(RowSpacing),
            typeof(double),
            typeof(UniformWrapPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((UniformWrapPanel)d).InvalidateMeasure();
    }

    public static readonly DependencyProperty StretchItemsProperty = DependencyProperty.Register(
        nameof(StretchItems), typeof(bool), typeof(UniformWrapPanel), new PropertyMetadata(false, OnLayoutPropertyChanged));

    public bool StretchItems
    {
        get => (bool)GetValue(StretchItemsProperty);
        set => SetValue(StretchItemsProperty, value);
    }

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(UniformWrapPanel), new PropertyMetadata(int.MaxValue, OnLayoutPropertyChanged));

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0)
            return new Size(0, 0);

        double availableWidth = availableSize.Width;
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth) || availableWidth <= 0)
            availableWidth = ItemWidth > 0 ? ItemWidth * count : 0;

        var (columns, cellWidth) = UniformWrapLayout.Calculate(availableWidth, ItemWidth, ColumnSpacing, count, StretchItems, MaxColumns);

        // Always measure children with unbounded vertical room so we learn each
        // child's true desired height; the cell height then becomes
        // max(ItemHeight, biggest child desired). ItemHeight acts as a FLOOR,
        // not a hard cap, which is what lets the card grid tolerate Windows'
        // "Make text bigger" slider — at 100% scale every card fits inside the
        // ItemHeight floor and behaviour is unchanged, at higher scale the
        // descriptions push past the floor and rows grow to fit instead of
        // clipping (issue #668).
        var childAvailable = new Size(cellWidth, double.PositiveInfinity);
        foreach (var child in Children)
            child.Measure(childAvailable);

        double cellHeight = ComputeCellHeight();

        int rows = (int)Math.Ceiling((double)count / columns);
        double totalHeight = rows * cellHeight + Math.Max(0, rows - 1) * RowSpacing;
        return new Size(availableWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0)
            return finalSize;

        var (columns, cellWidth) = UniformWrapLayout.Calculate(finalSize.Width, ItemWidth, ColumnSpacing, count, StretchItems, MaxColumns);
        double cellHeight = ComputeCellHeight();

        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            double x = col * (cellWidth + ColumnSpacing);
            double y = row * (cellHeight + RowSpacing);
            Children[i].Arrange(new Rect(x, y, cellWidth, cellHeight));
        }

        int rows = (int)Math.Ceiling((double)count / columns);
        double totalHeight = rows * cellHeight + Math.Max(0, rows - 1) * RowSpacing;
        return new Size(finalSize.Width, totalHeight);
    }

    private double ComputeCellHeight()
    {
        double floor = ItemHeight > 0 ? ItemHeight : 0;
        double tallest = MaxChildDesiredHeight();
        return Math.Max(floor, tallest);
    }

    private double MaxChildDesiredHeight()
    {
        double max = 0;
        foreach (var child in Children)
            if (child.DesiredSize.Height > max)
                max = child.DesiredSize.Height;
        return max;
    }
}
