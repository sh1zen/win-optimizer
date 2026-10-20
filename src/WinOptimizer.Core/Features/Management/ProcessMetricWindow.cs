namespace WinOptimizer.Core.Features.Management;

/// <summary>Computes rates only across continuous readings of the same process lifetime.</summary>
public sealed class ProcessMetricWindow
{
    private Dictionary<ProcessIdentity, ProcessReading> _previous = [];

    public ProcessSnapshot Add(DateTimeOffset at, TimeSpan elapsed, int processors, IReadOnlyList<ProcessReading> readings)
    {
        if (processors <= 0) throw new ArgumentOutOfRangeException(nameof(processors));
        var continuous = elapsed > TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(10);
        var current = new Dictionary<ProcessIdentity, ProcessReading>();
        var metrics = new List<ProcessMetric>(readings.Count);
        foreach (var reading in readings)
        {
            if (!current.TryAdd(reading.Identity, reading)) throw new ArgumentException("Duplicate process lifetime in a sample.", nameof(readings));
            _previous.TryGetValue(reading.Identity, out var prior);
            var valid = continuous && reading.Identity.CanInspect && prior != null;
            double? cpu = valid && reading.CpuTicks is { } ticks && prior!.CpuTicks is { } oldTicks && ticks >= oldTicks
                ? Math.Clamp((ticks - oldTicks) / (double)elapsed.Ticks / processors * 100, 0, 100) : null;
            double? Rate(ulong? value, ulong? old) => valid && value.HasValue && old.HasValue && value >= old
                ? (value.Value - old.Value) / elapsed.TotalSeconds : null;
            metrics.Add(new(reading, cpu, Rate(reading.ReadBytes, prior?.ReadBytes), Rate(reading.WriteBytes, prior?.WriteBytes)));
        }
        _previous = current;
        return new(at, metrics);
    }
}

/// <summary>Links only plausible parent lifetimes and breaks cycles without dropping processes.</summary>
public static class ProcessHierarchy
{
    public static IReadOnlyDictionary<ProcessIdentity, ProcessIdentity> Parents(IReadOnlyList<ProcessMetric> metrics)
    {
        var byPid = metrics.ToDictionary(metric => metric.Reading.Identity.Pid);
        var parents = new Dictionary<ProcessIdentity, ProcessIdentity>();
        foreach (var metric in metrics)
        {
            var child = metric.Reading;
            if (child.Identity.CanInspect && byPid.TryGetValue(child.ParentPid, out var parent) &&
                parent.Reading.Identity.CanInspect && parent.Reading.Identity != child.Identity &&
                parent.Reading.Identity.StartedAtUtcTicks <= child.Identity.StartedAtUtcTicks)
                parents.Add(child.Identity, parent.Reading.Identity);
        }
        var finished = new HashSet<ProcessIdentity>();
        foreach (var identity in parents.Keys.ToArray())
        {
            if (finished.Contains(identity)) continue;
            var visited = new HashSet<ProcessIdentity>();
            var cursor = identity;
            while (!finished.Contains(cursor) && parents.TryGetValue(cursor, out var parent))
            {
                if (!visited.Add(cursor)) { parents.Remove(cursor); break; }
                cursor = parent;
            }
            finished.UnionWith(visited);
        }
        return parents;
    }
}
