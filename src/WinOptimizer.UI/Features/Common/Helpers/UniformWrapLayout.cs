namespace WinOptimizer.UI.Features.Common.Helpers;

/// <summary>Calculates columns and cell widths together so stretching cannot change the column count.</summary>
internal static class UniformWrapLayout
{
    public static (int Columns, double CellWidth) Calculate(double availableWidth, double itemWidth,
        double columnSpacing, int itemCount, bool stretchItems, int maxColumns)
    {
        var columns = itemWidth > 0
            ? Math.Max(1, (int)Math.Floor((availableWidth + columnSpacing) / (itemWidth + columnSpacing)))
            : Math.Max(1, itemCount);
        if (stretchItems) columns = Math.Min(columns, Math.Max(1, maxColumns));
        var cellWidth = stretchItems
            ? Math.Max(0, (availableWidth - (columns - 1) * columnSpacing) / columns)
            : itemWidth > 0 ? itemWidth : availableWidth / columns;
        return (columns, cellWidth);
    }
}
