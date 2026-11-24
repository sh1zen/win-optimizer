namespace WinOptimizer.UI.Features.Extensions.ViewModels;

/// <summary>Provides plotted samples and axis labels for a monitor chart.</summary>
public interface IMonitorChartData
{
    IReadOnlyList<MonitorMetricSeries> Readings { get; }
    bool IsEmpty { get; }
    double Minimum { get; }
    double Maximum { get; }
    string Label { get; }
    string UnavailableLabel { get; }
    string HistoryLabel { get; }
    string MinimumLabel { get; }
    string MaximumLabel { get; }
    event EventHandler? ReadingsUpdated;
}
