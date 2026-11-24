using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WeatherPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WinOptimizer.UI.Features.Extensions.Notch;

internal enum NotchWeatherIconKind { Sun, Cloud, Drizzle, Rain, Snow, Fog, Storm, Unknown }

/// <summary>Renders the day and night weather artwork adapted from Astro's native WeatherSymbol.</summary>
internal static class NotchWeatherIcon
{
    internal static FrameworkElement Create(NotchWeatherIconKind kind, bool isDay, double size = 64)
    {
        var canvas = new Canvas { Width = 64, Height = 64, IsHitTestVisible = false };
        if (kind is NotchWeatherIconKind.Sun or NotchWeatherIconKind.Cloud)
        {
            var warm = Gradient(0xFFE979, 0xFF8C05);
            if (isDay)
            {
                var sun = new Ellipse { Width = 42, Height = 42, Fill = warm, Stroke = Brush(0xFFDA67), StrokeThickness = 1 };
                Canvas.SetLeft(sun, 11);
                Canvas.SetTop(sun, 8);
                canvas.Children.Add(sun);
            }
            else
            {
                var moon = Shape("M38 7A25 25 0 1 1 8 43A27 27 0 0 0 38 7Z");
                moon.Fill = warm;
                canvas.Children.Add(moon);
            }
        }
        if (kind != NotchWeatherIconKind.Sun)
        {
            var cloud = Shape("M16 48C2 48 1 31 14 29C17 10 43 12 47 30C64 28 67 48 51 48Z");
            cloud.Fill = Gradient(0xFFFFFF, 0x84B7F9);
            canvas.Children.Add(cloud);
            var highlight = Shape("M12 48C7 40 14 34 21 36C24 25 41 26 44 38C55 35 62 45 54 49Z");
            highlight.Fill = Gradient(0xFFFFFF, 0xC9E3FF);
            canvas.Children.Add(highlight);

            var marks = kind switch
            {
                NotchWeatherIconKind.Rain => "M18 52L14 60 M32 52L28 60 M46 52L42 60",
                NotchWeatherIconKind.Drizzle => "M18 54V57 M32 54V57 M46 54V57",
                NotchWeatherIconKind.Snow => "M17 51V61 M12 56H22 M13 52L21 60 M13 60L21 52 M43 51V61 M38 56H48 M39 52L47 60 M39 60L47 52",
                NotchWeatherIconKind.Fog => "M9 50H55 M15 56H49 M20 62H44",
                NotchWeatherIconKind.Storm => "M34 43L24 55H33L28 64L44 50H35L41 43Z",
                NotchWeatherIconKind.Unknown => "M27 30C27 21 43 22 40 31C39 35 33 34 33 39 M33 44V45",
                _ => null
            };
            if (marks is not null)
            {
                var mark = Shape(marks);
                if (kind == NotchWeatherIconKind.Storm) mark.Fill = Brush(0xFFB626);
                else
                {
                    mark.Stroke = Brush(0x6CA9ED);
                    mark.StrokeThickness = 3;
                    mark.StrokeStartLineCap = PenLineCap.Round;
                    mark.StrokeEndLineCap = PenLineCap.Round;
                }
                canvas.Children.Add(mark);
            }
        }
        return new Viewbox { Child = canvas, Width = size, Height = size };
    }

    private static WeatherPath Shape(string geometry) => (WeatherPath)XamlReader.Load(
        $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{geometry}' />");

    private static LinearGradientBrush Gradient(int start, int end) => new()
    {
        StartPoint = new(.3, 0), EndPoint = new(.7, 1),
        GradientStops = { new GradientStop { Color = Rgb(start), Offset = 0 }, new GradientStop { Color = Rgb(end), Offset = 1 } }
    };

    private static SolidColorBrush Brush(int rgb) => new(Rgb(rgb));

    private static Color Rgb(int rgb) => Microsoft.UI.ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
