using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinOptimizer.UI.Features.Common.Controls;

/// <summary>Spaces settings cards whose list containers can remain present after their contents are filtered out.</summary>
public sealed partial class SettingsCardStackPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing),
        typeof(double), typeof(SettingsCardStackPanel), new PropertyMetadata(0d, OnSpacingChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SettingsCardStackPanel)sender).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var available = new Size(availableSize.Width, double.PositiveInfinity);
        var width = 0d;
        var height = 0d;
        foreach (var child in Children)
        {
            child.Measure(available);
            var desired = child.DesiredSize;
            if (desired.Height <= 0) continue;
            width = Math.Max(width, desired.Width);
            height += (height > 0 ? Spacing : 0) + desired.Height;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var offset = 0d;
        foreach (var child in Children)
        {
            var height = child.DesiredSize.Height;
            if (height > 0 && offset > 0) offset += Spacing;
            child.Arrange(new Rect(0, offset, finalSize.Width, height));
            offset += height;
        }
        return finalSize;
    }
}
