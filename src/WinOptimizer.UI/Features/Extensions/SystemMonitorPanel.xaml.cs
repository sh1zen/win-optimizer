using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinOptimizer.UI.Features.Extensions.ViewModels;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Renders two instrument layouts from the same measured snapshot; owns no native window or sampling.</summary>
public sealed partial class SystemMonitorPanel : UserControl
{
    public const double CompactWidth = 240;
    public const double ExpandedWidth = 244;
    public SystemMonitorReadingsViewModel ViewModel { get; }
    public FrameworkElement DragRegion => DragHeader;
    public event EventHandler? DisableRequested;
    public event EventHandler? ToggleSizeRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ContentSizeChanged;

    public SystemMonitorPanel(SystemMonitorReadingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    /// <summary>Measures the visible composition, allowing missing sensors and translated text to change its height.</summary>
    public Size MeasureContent(double availableWidth, double maximumHeight = double.PositiveInfinity)
    {
        Bindings.Update();
        var chrome = InstrumentLayout.Padding.Top + InstrumentLayout.Padding.Bottom +
            PanelFrame.BorderThickness.Top + PanelFrame.BorderThickness.Bottom +
            InstrumentLayout.RowDefinitions[0].Height.Value + InstrumentLayout.RowDefinitions[2].Height.Value +
            2 * InstrumentLayout.RowSpacing;
        ReadingsViewport.MaxHeight = Math.Max(0, maximumHeight - chrome);
        LayoutRoot.Measure(new Size(availableWidth, double.PositiveInfinity));
        return new Size(availableWidth, Math.Ceiling(LayoutRoot.DesiredSize.Height));
    }

    public void RefreshPresentation()
    {
        Bindings.Update();
        var used = Math.Clamp(ViewModel.MemoryPercent, 0, 100);
        UsedMemoryColumn.Width = new GridLength(used, GridUnitType.Star);
        FreeMemoryColumn.Width = new GridLength(100 - used, GridUnitType.Star);
        DrawHistory(CompactCpuChart); DrawHistory(ExpandedCpuChart);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void Disable_Click(object sender, RoutedEventArgs e) => DisableRequested?.Invoke(this, EventArgs.Empty);
    private void Size_Click(object sender, RoutedEventArgs e) => ToggleSizeRequested?.Invoke(this, EventArgs.Empty);
    private void ReadingsSurface_Tapped(object sender, TappedRoutedEventArgs e)
    { e.Handled = true; ToggleSizeRequested?.Invoke(this, EventArgs.Empty); }
    private void CpuChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHistory((Canvas)sender);
    private void ReadingsSurface_SizeChanged(object sender, SizeChangedEventArgs e) => ContentSizeChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Right-aligns the real 60-sample history; unavailable samples break the line.</summary>
    private void DrawHistory(Canvas canvas)
    {
        canvas.Children.Clear();
        if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        var brush = AccentDot.Fill;
        for (var line = 1; line <= 2; line++)
            canvas.Children.Add(new Line { X1 = 0, X2 = canvas.ActualWidth,
                Y1 = line * canvas.ActualHeight / 3, Y2 = line * canvas.ActualHeight / 3,
                Stroke = brush, Opacity = 0.12, StrokeThickness = 0.5 });
        var values = ViewModel.CpuHistory.ToArray();
        Polyline? segment = null;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is not { } value) { segment = null; continue; }
            if (segment == null)
            {
                segment = new Polyline { Stroke = brush, StrokeThickness = 1, StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round };
                canvas.Children.Add(segment);
            }
            segment.Points.Add(new Point((60 - values.Length + index) * canvas.ActualWidth / 59,
                1 + (canvas.ActualHeight - 2) * (1 - Math.Clamp(value, 0, 100) / 100)));
        }
    }
}
