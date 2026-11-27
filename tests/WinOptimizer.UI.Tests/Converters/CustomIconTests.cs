using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.UI.Features.Common.Converters;
using Xunit;

namespace WinOptimizer.UI.Tests.Converters;

/// <summary>Exercises production icon resources and catalogue bindings on a native UI dispatcher.</summary>
public sealed class CustomIconTests
{
    /// <summary>Checks every unfiltered catalogue entry, including OS-specific settings.</summary>
    [Fact]
    public void CatalogueIconsResolveToOriginalArtwork()
    {
        var settings = new[]
        {
            ExplorerCustomizations.GetExplorerCustomizations(), StartMenuCustomizations.GetStartMenuCustomizations(),
            TaskbarCustomizations.GetTaskbarCustomizations(), WindowsThemeCustomizations.GetWindowsThemeCustomizations(),
            PowerOptimizations.GetPowerOptimizations(), GamingAndPerformanceOptimizations.GetGamingAndPerformanceOptimizations(),
            NotificationOptimizations.GetNotificationOptimizations(), PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations(),
            SoundOptimizations.GetSoundOptimizations(), UpdateOptimizations.GetUpdateOptimizations()
        }.SelectMany(group => group.Settings).Where(setting => !string.IsNullOrEmpty(setting.Icon)
            && !string.Equals(setting.IconPack, "Fluent", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(settings);
        var converter = new IconConverter();
        foreach (var setting in settings)
        {
            Assert.Equal(BaseDefinition.DefaultIconPack, setting.IconPack);
            var icon = Assert.IsType<PathIcon>(converter.Convert(setting, typeof(IconElement), string.Empty, "en"));
            Assert.True(icon.Data.Bounds.Width > 0 && icon.Data.Bounds.Height > 0, setting.Id);
        }
    }

    /// <summary>Checks independent elements, case-insensitive lookup and the host-controlled foreground contract.</summary>
    [Fact]
    public void OriginalIconsCreateIndependentForegroundAwareElements()
    {
        var converter = new IconConverter();
        var first = Assert.IsType<PathIcon>(converter.Convert("Camera", typeof(IconElement), string.Empty, "en"));
        var second = Assert.IsType<PathIcon>(converter.Convert("camera", typeof(IconElement), string.Empty, "en"));
        Assert.NotSame(first, second);
        Assert.NotSame(first.Data, second.Data);
        Assert.Equal(first.Data.Bounds, second.Data.Bounds);
        Assert.Equal(new Thickness(0, 0, 0, 1), first.Margin);
        var parent = new SolidColorBrush(Microsoft.UI.Colors.Teal);
        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);
        Assert.Same(DependencyProperty.UnsetValue, first.ReadLocalValue(IconElement.ForegroundProperty));
        Assert.Same(DependencyProperty.UnsetValue, second.ReadLocalValue(IconElement.ForegroundProperty));
        first.Foreground = parent;
        Assert.Equal(parent, first.Foreground);
        Assert.NotEqual(parent, second.Foreground);
    }

    /// <summary>Preserves explicit Fluent symbols and the empty/unknown-name contract.</summary>
    [Fact]
    public void ExplicitFluentAndUnknownNamesKeepTheirContracts()
    {
        var converter = new IconConverter();
        var fluent = Assert.IsType<FluentIcons.WinUI.FluentIcon>(converter.Convert("ArrowSync", typeof(IconElement), "Fluent", "en"));
        Assert.Equal(FluentIcons.Common.Icon.ArrowSync, fluent.Icon);
        Assert.Null(converter.Convert(string.Empty, typeof(IconElement), string.Empty, "en"));
        Assert.Null(converter.Convert("UnknownCustomIcon", typeof(IconElement), string.Empty, "en"));
    }

    /// <summary>Checks the production section converter against every original drawing's 24-unit bounds.</summary>
    [Fact]
    public void AllOriginalResourcesHaveVisibleBoundedGeometry()
    {
        var resources = OriginalPaths();
        var converter = new SectionIconConverter();
        foreach (var entry in resources)
        {
            var icon = Assert.IsType<PathIcon>(converter.Convert(entry.Key, typeof(IconElement), string.Empty, "en"));
            Assert.True(icon.Data.Bounds.Width > 0 && icon.Data.Bounds.Height > 0, entry.Key);
            // The converter centers geometry in its 28-unit box.
            Assert.InRange(icon.Data.Bounds.Width, .1, 24.01);
            Assert.InRange(icon.Data.Bounds.Height, .1, 24.01);
        }
    }

    /// <summary>Loads the same resource dictionary as the application shell.</summary>
    private static IEnumerable<KeyValuePair<string, string>> OriginalPaths()
    {
        var resources = new ResourceDictionary
        {
            Source = new Uri("ms-appx:///Features/Common/Resources/FeatureIcons.xaml")
        };
        return resources.Select(entry => KeyValuePair.Create(Assert.IsType<string>(entry.Key), Assert.IsType<string>(entry.Value)))
            .Where(entry => entry.Key.EndsWith("IconPath", StringComparison.Ordinal));
    }
}
