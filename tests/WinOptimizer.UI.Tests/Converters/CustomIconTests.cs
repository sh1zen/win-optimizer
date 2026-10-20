using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.UI.Features.Common.Converters;
using WinOptimizer.UI.Helpers;
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

    /// <summary>Renders the real resources in both themes for visual inspection.</summary>
    [Fact]
    public async Task OriginalArtworkRendersInLightAndDarkThemes()
    {
        var resources = OriginalPaths().ToList();
        const int columns = 12;
        var rows = (resources.Count + columns - 1) / columns;
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            var foreground = new SolidColorBrush(theme == ElementTheme.Light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
            var grid = new Grid
            {
                Width = columns * 92, Height = rows * 58, RequestedTheme = theme,
                Background = new SolidColorBrush(theme == ElementTheme.Light ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black)
            };
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition());
            for (var i = 0; i < resources.Count; i++)
            {
                var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), resources[i].Value);
                var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2) };
                cell.Children.Add(new PathIcon { Data = geometry, Width = 28, Height = 28, Foreground = foreground });
                cell.Children.Add(new TextBlock { Text = resources[i].Key.Replace("IconPath", string.Empty), Foreground = foreground,
                    FontSize = 8, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 88, HorizontalAlignment = HorizontalAlignment.Center });
                Grid.SetColumn(cell, i % columns); Grid.SetRow(cell, i / columns); grid.Children.Add(cell);
            }
            var window = new Window { Content = grid };
            try
            {
                window.Activate();
                await StartupUiCoordinator.RunAfterFirstRenderAsync(grid, () => Task.CompletedTask);
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(grid);
                Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0);
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
                await encoder.FlushAsync();
                stream.Seek(0);
                await using var output = File.Create(Path.Combine(AppContext.BaseDirectory, $"custom-icons-{theme.ToString().ToLowerInvariant()}.png"));
                await stream.AsStreamForRead().CopyToAsync(output);
            }
            finally { window.Close(); }
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
