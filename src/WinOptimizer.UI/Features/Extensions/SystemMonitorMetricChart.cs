using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.Features.Extensions.ViewModels;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Draws a hardware category on one physical-unit axis, with compact legends and full sensor tooltips.</summary>
public sealed partial class SystemMonitorMetricChart : UserControl
{
    public static readonly DependencyProperty SectionProperty = DependencyProperty.Register(nameof(Section),
        typeof(MonitorMetricSection), typeof(SystemMonitorMetricChart), new PropertyMetadata(null, OnSectionChanged));
    public MonitorMetricSection? Section
    {
        get => (MonitorMetricSection?)GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    private readonly Canvas _plot = new() { Height = 64 };
    private readonly UniformWrapPanel _legend = new() { ItemWidth = 92, ItemHeight = 22, ColumnSpacing = 6, MaxColumns = 2, StretchItems = true };
    private readonly TextBlock _maximum = new() { FontSize = 8, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.75 };
    private readonly TextBlock _minimum = new() { FontSize = 8, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.75 };
    private MonitorMetricSection? _subscribed;
    private bool _loaded;
    private static readonly Color[] LightPalette = [Color.FromArgb(255, 0, 115, 125), Color.FromArgb(255, 25, 96, 175),
        Color.FromArgb(255, 167, 67, 26), Color.FromArgb(255, 111, 61, 166), Color.FromArgb(255, 119, 97, 0), Color.FromArgb(255, 163, 44, 91)];
    private static readonly Color[] DarkPalette = [Color.FromArgb(255, 109, 222, 217), Color.FromArgb(255, 108, 188, 230),
        Color.FromArgb(255, 238, 173, 145), Color.FromArgb(255, 201, 170, 245), Color.FromArgb(255, 237, 207, 130), Color.FromArgb(255, 244, 158, 195)];

    public SystemMonitorMetricChart()
    {
        var composition = new StackPanel { Spacing = 3 };
        composition.Children.Add(_maximum);
        composition.Children.Add(_plot);
        composition.Children.Add(_minimum);
        composition.Children.Add(new ScrollViewer { Content = _legend, MaxHeight = 66,
            Padding = (Thickness)Microsoft.UI.Xaml.Application.Current.Resources["ScrollContentPadding"],
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = composition;
        _plot.SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Refresh();
        Loaded += (_, _) => { _loaded = true; Subscribe(); };
        Unloaded += (_, _) => { _loaded = false; Subscribe(); };
    }

    private static void OnSectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SystemMonitorMetricChart)sender).Subscribe();

    /// <summary>Subscribes only while attached to the visual tree so retained histories do not retain old controls.</summary>
    private void Subscribe()
    {
        if (_subscribed is not null) _subscribed.ReadingsUpdated -= OnReadingsUpdated;
        _subscribed = _loaded ? Section : null;
        if (_subscribed is not null) _subscribed.ReadingsUpdated += OnReadingsUpdated;
        Refresh();
    }

    private void OnReadingsUpdated(object? sender, EventArgs args) => Refresh();

    /// <summary>Keeps current values and trace colors paired while preserving full device identity for assistive tools.</summary>
    private void Refresh()
    {
        _legend.Children.Clear();
        Visibility = Section is { IsEmpty: false } ? Visibility.Visible : Visibility.Collapsed;
        _maximum.Text = Section?.MaximumLabel ?? "";
        _minimum.Text = Section?.MinimumLabel ?? "";
        if (Section is not { } section) return;
        AutomationProperties.SetName(this, section.Label);
        ToolTipService.SetToolTip(_plot, section.HistoryLabel);
        foreach (var series in section.Readings)
        {
            var entry = new Grid { ColumnSpacing = 4 };
            entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            entry.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            entry.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = SeriesBrush(series.ColorIndex), VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = series.ShortName, FontSize = 8, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
            Grid.SetColumn(label, 1);
            entry.Children.Add(label);
            var value = new TextBlock { Text = series.Value, FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(value, 2);
            entry.Children.Add(value);
            ToolTipService.SetToolTip(entry, $"{series.Name}\n{series.Value}");
            AutomationProperties.SetName(entry, series.Name);
            AutomationProperties.SetHelpText(entry, series.Value);
            _legend.Children.Add(entry);
        }
        Draw();
    }

    /// <summary>Right-aligns 60 real samples, breaks missing segments and marks the latest point even at first sampling.</summary>
    private void Draw()
    {
        _plot.Children.Clear();
        if (Section is not { IsEmpty: false } section || _plot.ActualWidth <= 0) return;
        for (var index = 0; index <= 2; index++)
        {
            var y = 1 + index * (_plot.Height - 2) / 2;
            _plot.Children.Add(new Line { X1 = 0, X2 = _plot.ActualWidth, Y1 = y, Y2 = y, Stroke = Foreground, Opacity = 0.12, StrokeThickness = 0.5 });
        }
        foreach (var series in section.Readings)
        {
            var brush = SeriesBrush(series.ColorIndex);
            Polyline? segment = null;
            Point? latest = null;
            var index = MonitorMetricSeries.HistoryLength - series.History.Count;
            foreach (var sample in series.History)
            {
                var x = index++ * (_plot.ActualWidth - 4) / (MonitorMetricSeries.HistoryLength - 1) + 2;
                if (sample is not { } value) { segment = null; latest = null; continue; }
                var point = new Point(x, 2 + (_plot.Height - 4) * (1 - (value - section.Minimum) / (section.Maximum - section.Minimum)));
                if (segment is null)
                {
                    segment = new Polyline { Stroke = brush, StrokeThickness = 1.25, StrokeLineJoin = PenLineJoin.Round };
                    _plot.Children.Add(segment);
                }
                segment.Points.Add(point);
                latest = point;
            }
            if (latest is not { } last) continue;
            var dot = new Ellipse { Width = 4, Height = 4, Fill = brush };
            Canvas.SetLeft(dot, last.X - 2);
            Canvas.SetTop(dot, last.Y - 2);
            _plot.Children.Add(dot);
        }
    }

    /// <summary>Uses stable sensor slots with contrasting palettes for each widget theme.</summary>
    private SolidColorBrush SeriesBrush(int index)
    {
        var colors = ActualTheme == ElementTheme.Light ? LightPalette : DarkPalette;
        return new(colors[index % colors.Length]);
    }
}
