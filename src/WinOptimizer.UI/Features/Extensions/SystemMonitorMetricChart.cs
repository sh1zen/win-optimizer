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
    private readonly List<LegendEntry> _legendEntries = [];
    private readonly TextBlock _maximum = new() { FontSize = 8, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.75 };
    private readonly TextBlock _minimum = new() { FontSize = 8, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.75 };
    private MonitorMetricSection? _subscribed;
    private SolidColorBrush[]? _lightBrushes;
    private SolidColorBrush[]? _darkBrushes;
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
        var section = Section;
        Visibility = section is { IsEmpty: false } ? Visibility.Visible : Visibility.Collapsed;
        _maximum.Text = section?.MaximumLabel ?? "";
        _minimum.Text = section?.MinimumLabel ?? "";
        var count = section?.Readings.Count ?? 0;
        while (_legendEntries.Count > count)
        {
            _legend.Children.RemoveAt(_legendEntries.Count - 1);
            _legendEntries.RemoveAt(_legendEntries.Count - 1);
        }
        if (section is not null)
        {
            AutomationProperties.SetName(this, section.Label);
            ToolTipService.SetToolTip(_plot, section.HistoryLabel);
            for (var index = 0; index < count; index++)
            {
                if (index == _legendEntries.Count)
                {
                    var entry = new LegendEntry();
                    _legendEntries.Add(entry);
                    _legend.Children.Add(entry.Root);
                }
                var series = section.Readings[index];
                _legendEntries[index].Update(series, SeriesBrush(series.ColorIndex));
            }
        }
        Draw();
    }

    /// <summary>Right-aligns 60 real samples, breaks missing segments and marks the latest point even at first sampling.</summary>
    private void Draw()
    {
        if (Section is not { IsEmpty: false } section || _plot.ActualWidth <= 0)
        {
            _plot.Children.Clear();
            return;
        }
        var shapeIndex = 0;
        for (var index = 0; index <= 2; index++)
        {
            var y = 1 + index * (_plot.Height - 2) / 2;
            var line = TakeShape<Line>(ref shapeIndex);
            line.X2 = _plot.ActualWidth;
            line.Y1 = line.Y2 = y;
            line.Stroke = Foreground;
            line.Opacity = 0.12;
            line.StrokeThickness = 0.5;
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
                    segment = TakeShape<Polyline>(ref shapeIndex);
                    segment.Points.Clear();
                    segment.Stroke = brush;
                    segment.StrokeThickness = 1.25;
                    segment.StrokeLineJoin = PenLineJoin.Round;
                }
                segment.Points.Add(point);
                latest = point;
            }
            if (latest is not { } last) continue;
            var dot = TakeShape<Ellipse>(ref shapeIndex);
            dot.Width = dot.Height = 4;
            dot.Fill = brush;
            Canvas.SetLeft(dot, last.X - 2);
            Canvas.SetTop(dot, last.Y - 2);
        }
        while (_plot.Children.Count > shapeIndex) _plot.Children.RemoveAt(_plot.Children.Count - 1);
    }

    /// <summary>Reuses shapes in drawing order and replaces a slot when the sensor's gap pattern changes.</summary>
    private T TakeShape<T>(ref int index) where T : Shape, new()
    {
        var children = _plot.Children;
        var slot = index++;
        if (slot < children.Count && children[slot] is T existing) return existing;
        var shape = new T();
        if (slot < children.Count) children[slot] = shape;
        else children.Add(shape);
        return shape;
    }

    /// <summary>Uses stable sensor slots with contrasting palettes for each widget theme.</summary>
    private SolidColorBrush SeriesBrush(int index)
    {
        var brushes = ActualTheme == ElementTheme.Light
            ? (_lightBrushes ??= LightPalette.Select(color => new SolidColorBrush(color)).ToArray())
            : (_darkBrushes ??= DarkPalette.Select(color => new SolidColorBrush(color)).ToArray());
        return brushes[index % brushes.Length];
    }

    /// <summary>Owns the reusable row controls for a chart legend slot.</summary>
    private sealed class LegendEntry
    {
        public Grid Root { get; } = new() { ColumnSpacing = 4 };
        private readonly Ellipse _dot = new() { Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _label = new() { FontSize = 8, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
        private readonly TextBlock _value = new() { FontSize = 9, VerticalAlignment = VerticalAlignment.Center };

        public LegendEntry()
        {
            Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            Grid.SetColumn(_label, 1);
            Grid.SetColumn(_value, 2);
            Root.Children.Add(_dot);
            Root.Children.Add(_label);
            Root.Children.Add(_value);
        }

        public void Update(MonitorMetricSeries series, SolidColorBrush brush)
        {
            _dot.Fill = brush;
            _label.Text = series.ShortName;
            _value.Text = series.Value;
            ToolTipService.SetToolTip(Root, $"{series.Name}\n{series.Value}");
            AutomationProperties.SetName(Root, series.Name);
            AutomationProperties.SetHelpText(Root, series.Value);
        }
    }
}
