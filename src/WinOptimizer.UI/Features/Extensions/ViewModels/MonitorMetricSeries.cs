namespace WinOptimizer.UI.Features.Extensions.ViewModels;

/// <summary>Retains a sensor's identity, stable chart color and real samples across short measurement gaps.</summary>
public sealed class MonitorMetricSeries
{
    public const int HistoryLength = 60;
    private readonly Queue<double?> _history = new();
    public string Name { get; }
    public string SensorLabel { get; }
    public string ShortName { get; internal set; }
    public string Value { get; private set; } = "—";
    public int ColorIndex { get; }
    public IReadOnlyCollection<double?> History => _history;

    internal MonitorMetricSeries(string name, string sensorLabel, int colorIndex, int missingSamples)
    {
        Name = name;
        SensorLabel = sensorLabel;
        ShortName = SensorLabel;
        ColorIndex = colorIndex;
        for (var index = 0; index < missingSamples; index++) _history.Enqueue(null);
    }

    /// <summary>Appends one measured or missing sample and bounds memory independently of widget visibility.</summary>
    internal void Append(double? value, string formatted)
    {
        Value = formatted;
        _history.Enqueue(value);
        if (_history.Count > HistoryLength) _history.Dequeue();
    }
}
