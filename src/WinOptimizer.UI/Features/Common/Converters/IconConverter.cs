using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Resources;

namespace WinOptimizer.UI.Features.Common.Converters;

/// <summary>
/// Converts icon names and icon packs to IconElement controls.
/// Uses original app paths and explicitly selected Fluent symbols, compatible
/// with SettingsCard.HeaderIcon.
/// </summary>
public sealed partial class IconConverter : IValueConverter
{

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        string? iconName = null;
        string iconPack = BaseDefinition.DefaultIconPack;

        // Check if value is a string (direct icon name)
        if (value is string strValue)
        {
            iconName = strValue;
            iconPack = parameter?.ToString() ?? BaseDefinition.DefaultIconPack;
        }
        // Check if value is an object with Icon and IconPack properties (like SettingItemViewModel)
        else if (value != null)
        {
            var type = value.GetType();
            var iconProperty = type.GetProperty("Icon");
            var iconPackProperty = type.GetProperty("IconPack");

            iconName = iconProperty?.GetValue(value)?.ToString();
            iconPack = iconPackProperty?.GetValue(value)?.ToString() ?? BaseDefinition.DefaultIconPack;
        }

        if (string.IsNullOrEmpty(iconName))
        {
            return null; // Return null so no icon is shown
        }

        return string.Equals(iconPack, "Fluent", StringComparison.OrdinalIgnoreCase)
            ? CreateFluentIcon(iconName)
            : CreateCustomPathIcon(iconName);
    }

    /// <summary>
    /// Creates an independent, foreground-aware PathIcon from original app artwork.
    /// </summary>
    private static IconElement? CreateCustomPathIcon(string iconName)
    {
        var key = CustomIconCatalog.ResourceKeyFor(iconName);
        if (key is null)
            return null;

        var pathData = (string)Microsoft.UI.Xaml.Application.Current.Resources[key];
        var geometry = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), pathData);
        return new PathIcon
        {
            Data = geometry,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 1)
        };
    }

    /// <summary>
    /// Creates a FluentIcon from FluentIcons.WinUI for Fluent icons.
    /// </summary>
    private static IconElement? CreateFluentIcon(string iconName)
    {
        if (Enum.TryParse<FluentIcons.Common.Icon>(iconName, ignoreCase: true, out var symbol))
        {
            return new FluentIcons.WinUI.FluentIcon
            {
                Icon = symbol,
                IconVariant = FluentIcons.Common.IconVariant.Regular
            };
        }

        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
