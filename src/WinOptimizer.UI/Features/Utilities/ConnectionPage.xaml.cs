using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Utilities;

/// <summary>Scopes sampling to the visible page and renders responsive traffic charts.</summary>
public sealed partial class ConnectionPage : Page
{
    private CancellationTokenSource? _session;
    public ConnectionViewModel ViewModel { get; } = App.Services.GetRequiredService<ConnectionViewModel>();

    public ConnectionPage()
    {
        InitializeComponent();
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
        ActualThemeChanged += (_, _) => DrawCharts();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_session != null) return;
        using var session = new CancellationTokenSource();
        _session = session;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        DrawCharts();
        var loading = ViewModel.LoadAsync();
        try
        {
            while (!session.IsCancellationRequested)
            {
                await ViewModel.SampleTrafficAsync(session.Token);
                await Task.Delay(TimeSpan.FromSeconds(2), session.Token);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        finally { await loading; }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _session?.Cancel();
        _session = null;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    /// <summary>Opens saved Wi-Fi credentials within the existing shell navigation journal.</summary>
    private void SavedWifi_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SavedWifiPage));

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)) DrawCharts();
        else if (e.PropertyName == nameof(ConnectionViewModel.LiveIntervals)) DrawLiveChart();
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, DailyChart)) DrawDailyChart();
        else DrawLiveChart();
    }

    private void DrawCharts() { DrawLiveChart(); DrawDailyChart(); }

    /// <summary>Uses the same rate scale for upload/download and breaks lines at missing samples.</summary>
    private void DrawLiveChart()
    {
        if (LiveChart == null || SentLegend == null || ReceivedLegend == null) return;
        DrawGrid(LiveChart);
        if (LiveChart.ActualWidth <= 0 || ViewModel.LiveCapturedAt is not { } at) return;
        var intervals = ViewModel.LiveIntervals;
        var maximum = Math.Max(1, intervals.SelectMany(interval => new[]
            { interval.UploadBytesPerSecond, interval.DownloadBytesPerSecond }).Max() ?? 0);
        foreach (var download in new[] { true, false })
        {
            Polyline? segment = null;
            foreach (var interval in intervals)
            {
                var value = download ? interval.DownloadBytesPerSecond : interval.UploadBytesPerSecond;
                if (value == null) { segment = null; continue; }
                if (segment == null)
                {
                    segment = new Polyline { Stroke = download ? ReceivedLegend.Background : SentLegend.Background,
                        StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
                    LiveChart.Children.Add(segment);
                    segment.Points.Add(new Point(X(interval.Start), Y(value.Value)));
                }
                segment.Points.Add(new Point(X(interval.End), Y(value.Value)));
            }
        }
        double X(DateTimeOffset time) => Math.Clamp((time - at.AddMinutes(-15)).TotalSeconds / 900, 0, 1) * LiveChart.ActualWidth;
        double Y(decimal value) => 2 + (LiveChart.ActualHeight - 4) * (1 - (double)(value / maximum));
    }

    /// <summary>Stacks upload above download in one accessible, keyboard-focusable bar per day.</summary>
    private void DrawDailyChart()
    {
        if (DailyChart == null || SentLegend == null || ReceivedLegend == null) return;
        DrawGrid(DailyChart);
        var days = ViewModel.HistoryDays;
        if (DailyChart.ActualWidth <= 0 || days.Count == 0) return;
        var maximum = Math.Max(1, ViewModel.HistoryMaximumBytes);
        var slot = DailyChart.ActualWidth / days.Count;
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var sent in new[] { true, false })
            {
                var value = sent ? day.BytesSent : day.BytesReceived;
                var bar = new Border { Name = "DailyUsageBar", VerticalAlignment = VerticalAlignment.Bottom,
                    Height = value.HasValue ? (double)(value.Value / maximum) * (DailyChart.Height - 4) : 2,
                    Background = value.HasValue ? (sent ? SentLegend.Background : ReceivedLegend.Background) : SentLegend.Background,
                    Opacity = value.HasValue ? 1 : 0.25,
                    CornerRadius = sent || day.BytesSent == 0 ? new CornerRadius(2, 2, 0, 0) : new CornerRadius(0) };
                bars.Children.Add(bar);
            }
            var button = new Button { Name = "DailyUsageDay", Content = bars, Width = slot,
                Height = DailyChart.Height, Padding = new Thickness(Math.Min(2, slot / 8), 0, Math.Min(2, slot / 8), 0),
                BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            AutomationProperties.SetName(button, ViewModel.DayDetails(day));
            var tooltip = PopupHandler.CreateToolTip(button, ViewModel.DayDetails(day));
            ToolTipService.SetToolTip(button, tooltip);
            button.Click += (_, _) => tooltip.IsOpen = true;
            Canvas.SetLeft(button, index * slot); DailyChart.Children.Add(button);
        }
    }

    private void DrawGrid(Canvas canvas)
    {
        canvas.Children.Clear();
        for (var index = 0; index < 4; index++)
            canvas.Children.Add(new Line { X1 = 0, X2 = canvas.ActualWidth,
                Y1 = index * (canvas.Height - 2) / 3 + 1, Y2 = index * (canvas.Height - 2) / 3 + 1,
                Stroke = SentLegend.Background, Opacity = 0.15, StrokeThickness = 1, IsHitTestVisible = false });
    }
}
