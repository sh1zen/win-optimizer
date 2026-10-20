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
        // WinUI control templates look up these aliases rather than the base accent brushes.
        foreach (var theme in new[] { "Light", "Default" })
        {
            var dictionary = (ResourceDictionary)ThemeDictionaries[theme];
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
        AppColor.Blue => (Color.FromArgb(255, 0, 103, 192), Color.FromArgb(255, 96, 205, 255)),
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
            // Solid, deeper tiles keep white glyphs readable in both application themes.
            ((SolidColorBrush)dictionary["PageIconBackgroundBrush"]).Color = light;
            foreach (var (key, alpha) in new (string, byte)[]
            {
                ("AccentFillColorDefaultBrush", 255), ("AccentFillColorSecondaryBrush", 230), ("AccentFillColorTertiaryBrush", 204),
                ("AccentTextFillColorPrimaryBrush", 255), ("AccentTextFillColorSecondaryBrush", 255), ("AccentTextFillColorTertiaryBrush", 255),
                ("SidebarBackgroundBrush", 20), ("NavigationSelectedBackgroundBrush", 26)
            })
            {
                var brush = (SolidColorBrush)dictionary[key];
                brush.Color = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
            }
        }
    }
}
