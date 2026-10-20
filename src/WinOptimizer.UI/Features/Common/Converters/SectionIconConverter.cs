using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace WinOptimizer.UI.Features.Common.Converters;

/// <summary>Builds a fresh icon from the catalogue's path or Fluent symbol resource for each visual host.</summary>
public sealed partial class SectionIconConverter : IValueConverter
{
    private const double IconSize = 28;

    /// <summary>Creates independent artwork centered in the shared category-icon box.</summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = (string)value;
        var data = (string)Microsoft.UI.Xaml.Application.Current.Resources[key];
        if (key.EndsWith("Symbol", StringComparison.Ordinal))
            return new FluentIcon { Icon = Enum.Parse<Icon>(data, true), IconVariant = IconVariant.Regular, FontSize = IconSize };

        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);
        var bounds = geometry.Bounds;
        // Center the artwork itself inside the icon's box without changing its size.
        geometry.Transform = new TranslateTransform
        {
            X = IconSize / 2 - bounds.X - bounds.Width / 2,
            Y = IconSize / 2 - bounds.Y - bounds.Height / 2
        };
        return new PathIcon { Data = geometry, Width = IconSize, Height = IconSize };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
