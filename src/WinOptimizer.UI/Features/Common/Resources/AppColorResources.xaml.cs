using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinOptimizer.UI.Features.Common.Resources;

/// <summary>Supported application accents, independent of the Windows accent preference.</summary>
public enum AppColor { Blue, Teal, Green, Purple, Pink, Orange }

/// <summary>Owns shared accent brushes; updating them recolors existing controls without recreating pages.</summary>
public sealed partial class AppColorResources : ResourceDictionary
{
    public AppColorResources()
    {
        InitializeComponent();
        var interactionPalette = new ResourceDictionary
        {
            Source = new Uri("ms-appx:///Features/Common/Resources/InteractiveSurfaceResources.xaml")
        };
        // WinUI control templates look up these aliases rather than the base accent brushes.
        foreach (var theme in new[] { "Light", "Default" })
        {
            var dictionary = (ResourceDictionary)ThemeDictionaries[theme];
            dictionary["AccentSelectionForegroundBrush"] = dictionary["AccentTextFillColorPrimaryBrush"];

            // Give native transitions opaque endpoints to avoid flashing as both color and opacity change.
            var interactionTheme = (ResourceDictionary)interactionPalette.ThemeDictionaries[theme];
            var surface = dictionary["AppSurfaceBrush"];
            var hoveredSurface = interactionTheme["SettingsCardBackgroundPointerOver"];
            var pressedSurface = interactionTheme["SettingsCardBackgroundPressed"];
            dictionary["AppSurfacePointerOverBrush"] = hoveredSurface;
            dictionary["AppSurfacePressedBrush"] = pressedSurface;
            foreach (var key in new[] { "ButtonBackground", "ComboBoxBackground" })
            {
                dictionary[key] = surface;
                dictionary[key + "PointerOver"] = hoveredSurface;
                dictionary[key + "Pressed"] = pressedSurface;
            }
            dictionary["TextControlBackground"] = surface;
            dictionary["TextControlBackgroundPointerOver"] = hoveredSurface;

            foreach (var key in new[] { "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "ToggleButtonBackgroundChecked",
                "AccentButtonBackground", "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillIndeterminate",
                "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeIndeterminate",
                "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedStroke", "SliderThumbBackground", "SliderTrackValueFill" })
            {
                dictionary[key] = dictionary["AccentFillColorDefaultBrush"];
                dictionary[key + "PointerOver"] = dictionary["AccentFillColorSecondaryBrush"];
                dictionary[key + "Pressed"] = dictionary["AccentFillColorTertiaryBrush"];
            }
            foreach (var key in new[] { "HyperlinkForeground", "HyperlinkButtonForeground" })
            {
                dictionary[key] = dictionary["AccentTextFillColorPrimaryBrush"];
                dictionary[key + "PointerOver"] = dictionary["AccentTextFillColorSecondaryBrush"];
                dictionary[key + "Pressed"] = dictionary["AccentTextFillColorTertiaryBrush"];
            }
            foreach (var key in new[] { "ProgressBarForeground", "ProgressRingForegroundThemeBrush", "ComboBoxItemPillFillBrush",
                "ListViewItemCheckBoxSelectedBrush", "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush",
                "ListViewItemSelectionIndicatorPressedBrush", "NavigationViewSelectionIndicatorForeground", "PivotHeaderItemSelectedPipeFill" })
                dictionary[key] = dictionary["AccentFillColorDefaultBrush"];
        }
    }

    /// <summary>Returns readable accent variants for light and dark surfaces.</summary>
    public static (Color Light, Color Dark) ColorsFor(AppColor color) => color switch
    {
        AppColor.Blue => (Color.FromArgb(255, 36, 102, 217), Color.FromArgb(255, 141, 180, 255)),
        AppColor.Teal => (Color.FromArgb(255, 0, 117, 117), Color.FromArgb(255, 78, 214, 205)),
        AppColor.Green => (Color.FromArgb(255, 16, 124, 16), Color.FromArgb(255, 108, 211, 108)),
        AppColor.Purple => (Color.FromArgb(255, 116, 71, 190), Color.FromArgb(255, 194, 166, 255)),
        AppColor.Pink => (Color.FromArgb(255, 176, 31, 106), Color.FromArgb(255, 255, 153, 204)),
        AppColor.Orange => (Color.FromArgb(255, 167, 63, 0), Color.FromArgb(255, 255, 180, 112)),
        _ => throw new ArgumentOutOfRangeException(nameof(color))
    };

    /// <summary>Mutates the shared brushes while retaining theme bindings and Windows high-contrast resources.</summary>
    public void Apply(AppColor color)
    {
        var (light, dark) = ColorsFor(color);
        foreach (var theme in new[] { "Light", "Default" })
        {
            var dictionary = (ResourceDictionary)ThemeDictionaries[theme];
            var accent = theme == "Light" ? light : dark;
            // Blend the selection tint onto its surface before the template animates it.
            var surface = ((SolidColorBrush)dictionary["AppSurfaceBrush"]).Color;
            ((SolidColorBrush)dictionary["AccentSelectionBackgroundBrush"]).Color = Color.FromArgb(255,
                TintSelectionChannel(surface.R, accent.R), TintSelectionChannel(surface.G, accent.G), TintSelectionChannel(surface.B, accent.B));

            // Solid, deeper tiles keep white glyphs readable in both application themes.
            ((SolidColorBrush)dictionary["PageIconBackgroundBrush"]).Color = light;
            foreach (var (key, alpha) in new (string, byte)[]
            {
                ("AccentFillColorDefaultBrush", 255), ("AccentFillColorSecondaryBrush", 230), ("AccentFillColorTertiaryBrush", 204),
                ("AccentTextFillColorPrimaryBrush", 255), ("AccentTextFillColorSecondaryBrush", 255), ("AccentTextFillColorTertiaryBrush", 255)
            })
            {
                var brush = (SolidColorBrush)dictionary[key];
                brush.Color = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
            }
        }
    }

    /// <summary>Mixes the accent at the selection tint's opacity, rounded to the nearest channel value.</summary>
    private static byte TintSelectionChannel(byte surface, byte accent)
    {
        const int accentWeight = 26;
        return (byte)((surface * (255 - accentWeight) + accent * accentWeight + 127) / 255);
    }
}
