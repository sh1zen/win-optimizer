using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.ViewModels;

/// <summary>Owns the current readings, bounded sensor histories and shared axis for one hardware chart.</summary>
public partial class MonitorMetricSection(SystemMonitorMetricKind kind, ILocalizationService localization) : ObservableObject
{
    private readonly Dictionary<(string Name, int Occurrence), MonitorMetricSeries> _series = [];
    private int _samples;
    private int _nextColor;
    public SystemMonitorMetricKind Kind { get; } = kind;
    public string Label => localization.GetString($"SysMonitor_{Kind}");
    public string UnavailableLabel => localization.GetString("SysMonitor_Unavailable");
    public string HistoryLabel => localization.GetString("SysMonitor_History");
    public ObservableCollection<MonitorMetricSeries> Readings { get; } = [];
    public bool IsEmpty => Readings.Count == 0;
    public double Minimum { get; private set; }
    public double Maximum { get; private set; } = 100;
    public string MinimumLabel => FormatValue(Minimum);
    public string MaximumLabel => FormatValue(Maximum);
    public event EventHandler? ReadingsUpdated;
    [ObservableProperty] public partial bool IsVisible { get; set; }

    /// <summary>Aligns each sensor to the same sample timeline; missing readings break its line instead of becoming zero.</summary>
    public void Publish(IEnumerable<SystemMonitorMetric> metrics)
    {
        var current = new Dictionary<(string Name, int Occurrence), double>();
        var occurrences = new Dictionary<string, int>();
        Readings.Clear();
        foreach (var metric in metrics)
        {
            var occurrence = occurrences.GetValueOrDefault(metric.Name);
            occurrences[metric.Name] = occurrence + 1;
            var key = (metric.Name, occurrence);
            if (!_series.TryGetValue(key, out var series))
            {
                series = new(metric.Name, SensorLabel(metric.Name), _nextColor++, Math.Min(_samples, MonitorMetricSeries.HistoryLength - 1));
                _series.Add(key, series);
            }
            current.Add(key, metric.Value);
            Readings.Add(series);
        }
        foreach (var (key, series) in _series)
        {
            double? value = current.TryGetValue(key, out var measured) ? measured : null;
            series.Append(value, value is { } reading ? FormatValue(reading) : "—");
        }
        foreach (var key in _series.Where(pair => pair.Value.History.All(value => value is null)).Select(pair => pair.Key).ToArray())
            _series.Remove(key);
        _samples = Math.Min(_samples + 1, MonitorMetricSeries.HistoryLength);

        Minimum = 0;
        Maximum = Kind is SystemMonitorMetricKind.GpuUsage or SystemMonitorMetricKind.StorageActivity ? 100 : 1;
        foreach (var group in Readings.GroupBy(reading => reading.SensorLabel))
        {
            var index = 0;
            var duplicated = group.Skip(1).Any();
            foreach (var series in group)
            {
                series.ShortName = duplicated ? $"{series.SensorLabel} {++index}" : series.SensorLabel;
                foreach (var value in series.History)
                {
                    if (value is not { } measured) continue;
                    Minimum = Math.Min(Minimum, measured);
                    Maximum = Math.Max(Maximum, measured);
                }
            }
        }
        OnPropertyChanged(nameof(IsEmpty));
        ReadingsUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes hardware identity and redundant GPU category prefixes from legends, retaining the full source name separately.</summary>
    private string SensorLabel(string name)
    {
        var separator = name.IndexOf(" · ", StringComparison.Ordinal);
        var label = separator >= 0 ? name[..separator] : name;
        if (Kind == SystemMonitorMetricKind.GpuMemory)
        {
            if (label.StartsWith("GPU Memory ", StringComparison.Ordinal)) return label[11..];
            if (label.StartsWith("D3D ", StringComparison.Ordinal)) return label[4..].Replace(" Memory", "", StringComparison.Ordinal);
        }
        if (Kind == SystemMonitorMetricKind.GpuUsage && label.StartsWith("GPU ", StringComparison.Ordinal)) return label[4..];
        return label;
    }

    /// <summary>Uses the hardware boundary's physical units for both latest values and chart axes.</summary>
    private string FormatValue(double value) => Kind switch
    {
        SystemMonitorMetricKind.GpuUsage or SystemMonitorMetricKind.StorageActivity => $"{value:0}%",
        SystemMonitorMetricKind.GpuMemory => $"{value:0.00} GB",
        SystemMonitorMetricKind.ClockSpeed => $"{value:0} MHz",
        SystemMonitorMetricKind.Power => $"{value:0.0} W",
        SystemMonitorMetricKind.Voltage => $"{value:0.000} V",
        SystemMonitorMetricKind.StorageThroughput => SystemMonitorReadingsViewModel.Rate(value),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };

    /// <summary>Updates localized section headings without discarding the most recent measurements.</summary>
    public void RefreshLabels()
    {
        OnPropertyChanged(string.Empty);
        ReadingsUpdated?.Invoke(this, EventArgs.Empty);
    }
}
