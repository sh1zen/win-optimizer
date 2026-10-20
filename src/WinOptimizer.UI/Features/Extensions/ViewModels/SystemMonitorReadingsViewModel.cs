using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.ViewModels;

/// <summary>Formats measured quantities at the presentation boundary, retaining unavailable values.</summary>
public partial class SystemMonitorReadingsViewModel(ILocalizationService localization) : ObservableObject
{
    private SystemMonitorSnapshot? _snapshot;
    private readonly Queue<double?> _history = new();
    public IReadOnlyCollection<double?> CpuHistory => _history;
    public ObservableCollection<MonitorSensorItem> Temperatures { get; } = [];
    public ObservableCollection<MonitorSensorItem> Fans { get; } = [];
    public IReadOnlyList<MonitorMetricSection> MetricSections { get; } = Enum.GetValues<SystemMonitorMetricKind>()
        .Select(kind => new MonitorMetricSection(kind, localization)).ToArray();
    [ObservableProperty] public partial bool IsCompact { get; set; } = true;
    public bool IsExpanded => !IsCompact;
    partial void OnIsCompactChanged(bool value) => OnPropertyChanged(nameof(IsExpanded));
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; set; } = "";
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string CpuValue => Format(_snapshot?.CpuUsagePercent, "0", "%");
    public string CpuHeadline => "CPU · " + CpuValue;
    private SystemMonitorSensor? CpuSensor => _snapshot?.Temperatures.FirstOrDefault(sensor => sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase));
    public string CpuTemperature => Format(CpuSensor?.Value, "0", "°C");
    public string CpuTemperatureName => CpuSensor?.Name ?? UnavailableLabel;
    public string PrimaryFanValue => Format(_snapshot?.Fans.FirstOrDefault()?.Value, "0", " rpm");
    public double MemoryPercent => _snapshot is { TotalMemoryBytes: > 0, AvailableMemoryBytes: { } available }
        ? 100d * (_snapshot.TotalMemoryBytes.Value - Math.Min(available, _snapshot.TotalMemoryBytes.Value)) / _snapshot.TotalMemoryBytes.Value : 0;
    public string MemoryValue => _snapshot is { TotalMemoryBytes: > 0, AvailableMemoryBytes: { } available }
        ? $"{(_snapshot.TotalMemoryBytes.Value - Math.Min(available, _snapshot.TotalMemoryBytes.Value)) / 1073741824d:0.0} / {_snapshot.TotalMemoryBytes / 1073741824d:0.0} GB" : "—";
    public string TotalMemory => _snapshot?.TotalMemoryBytes is > 0 ? $"{_snapshot.TotalMemoryBytes / 1073741824d:0.##} GB" : "—";
    public string UsedMemoryNumber => _snapshot is { TotalMemoryBytes: > 0, AvailableMemoryBytes: { } available }
        ? $"{(_snapshot.TotalMemoryBytes.Value - Math.Min(available, _snapshot.TotalMemoryBytes.Value)) / 1073741824d:0.00}" : "—";
    public string FreeMemoryNumber => _snapshot is { TotalMemoryBytes: > 0, AvailableMemoryBytes: { } available }
        ? $"{Math.Min(available, _snapshot.TotalMemoryBytes.Value) / 1073741824d:0.00}" : "—";
    public string MemoryUsageValue => _snapshot is { TotalMemoryBytes: > 0, AvailableMemoryBytes: not null } ? $"{MemoryPercent:0}%" : "—";
    public string DownloadValue => Rate(_snapshot?.DownloadBytesPerSecond);
    public string UploadValue => Rate(_snapshot?.UploadBytesPerSecond);
    public string Notices => string.Join(Environment.NewLine, _snapshot?.Notices ?? []);
    public bool HasNotices => _snapshot?.Notices.Count > 0;
    public string DiagnosticsLabel => Text("SysMonitor_SensorsTitle");
    public bool NoTemperatures => Temperatures.Count == 0;
    public bool NoFans => Fans.Count == 0;
    public bool ShowTemperatures { get; private set; } = true;
    public bool ShowFans { get; private set; } = true;
    public bool ShowMemory { get; private set; } = true;
    public bool ShowCpu { get; private set; } = true;
    public bool ShowNetwork { get; private set; } = true;
    public bool HasVisibleMetrics => MetricSections.Any(section => section.IsVisible);
    public string TemperaturesLabel => Text("SysMonitor_Temperatures");
    public string FansLabel => Text("SysMonitor_Fans");
    public string MemoryLabel => Text("SysMonitor_Memory");
    public string DownloadLabel => Text("SysMonitor_Download");
    public string UploadLabel => Text("SysMonitor_Upload");
    public string UsedLabel => Text("SysMonitor_Used");
    public string FreeLabel => Text("SysMonitor_Free");
    public string UnavailableLabel => Text("SysMonitor_Unavailable");
    public string SensorHint => Text("SysMonitor_SensorsDescription");
    public string ExpandLabel => Text(IsCompact ? "SysMonitor_Expand" : "SysMonitor_Collapse");
    public string SettingsLabel => Text("SysMonitor_Settings");
    public string DisableLabel => Text("SysMonitor_Disable");
    public string CpuHistoryLabel => Text("SysMonitor_CpuHistory");
    private string Text(string key) => localization.GetString(key);

    /// <summary>Applies expanded-only choices without changing the compact composition or collected history.</summary>
    public void ApplyOptions(SystemMonitorOptions options)
    {
        IsCompact = options.IsCompact;
        ShowTemperatures = options.ShowTemperatures;
        ShowFans = options.ShowFans;
        ShowMemory = options.ShowMemory;
        ShowCpu = options.ShowCpu;
        ShowNetwork = options.ShowNetwork;
        foreach (var section in MetricSections) section.IsVisible = options.IsMetricVisible(section.Kind);
        RefreshLabels();
    }

    /// <summary>Updates all cards from the same sample and bounds the in-memory chart to 60 readings.</summary>
    public void Publish(SystemMonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        Error = "";
        _history.Enqueue(snapshot.CpuUsagePercent);
        if (_history.Count > 60) _history.Dequeue();
        Replace(Temperatures, snapshot.Temperatures);
        Replace(Fans, snapshot.Fans);
        var metrics = snapshot.HardwareMetrics.ToLookup(metric => metric.Kind);
        foreach (var section in MetricSections) section.Publish(metrics[section.Kind]);
        OnPropertyChanged(string.Empty);
    }

    public void RefreshLabels()
    {
        OnPropertyChanged(string.Empty);
        foreach (var section in MetricSections) section.RefreshLabels();
    }

    private static void Replace(ObservableCollection<MonitorSensorItem> target,
        IReadOnlyList<SystemMonitorSensor> sensors)
    {
        target.Clear();
        foreach (var sensor in sensors) target.Add(new(sensor.Name, $"{sensor.Value:0}"));
    }
    private static string Format(double? value, string format, string suffix) => value is { } number ? number.ToString(format) + suffix : "—";
    internal static string Rate(double? value) => value switch
    {
        null => "—",
        >= 1048576 => $"{value / 1048576:0.0} MB/s",
        >= 1024 => $"{value / 1024:0.0} KB/s",
        _ => $"{value:0} B/s"
    };
}

public sealed record MonitorSensorItem(string Name, string Value);
