using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Benchmark;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Benchmark;

/// <summary>Owns a test's current result, failure and localized readout.</summary>
public sealed partial class BenchmarkRow(
    BenchmarkKind kind, string titleKey, Icon icon, ILocalizationService localization) : ObservableObject
{
    private BenchmarkMeasurement? _measurement;
    private string? _target;
    private string? _errorKey;
    internal BenchmarkKind Kind { get; } = kind;
    public Icon Icon { get; } = icon;
    public string Title => localization.GetString(titleKey);
    public string RunLabel => localization.GetString("Benchmark_Run");
    public string RunName => localization.GetString("Benchmark_RunNamed", Title);
    public string Detail => _errorKey is { } error ? localization.GetString(error) : _target ?? "";
    public bool HasDetail => _errorKey is not null || _target is not null;
    internal bool IsRunning { get; private set; }
    [ObservableProperty] public partial bool CanRun { get; internal set; }

    public string Result
    {
        get
        {
            if (_measurement is not { } measurement) return "—";
            var (scale, unit) = Kind switch
            {
                BenchmarkKind.CpuClock => (1_000d, "GHz"),
                BenchmarkKind.CpuFlops or BenchmarkKind.GpuFlops => (1_000_000_000d, "GFLOPS"),
                BenchmarkKind.MemoryBandwidth => (1_000_000_000d, "GB/s"),
                _ => (1_000_000d, "MB/s")
            };
            return $"{(measurement.Value / scale).ToString("N2", CultureInfo.CurrentUICulture)} {unit}";
        }
    }

    internal void Start()
    {
        IsRunning = true;
        _errorKey = null;
        RefreshText();
    }

    internal void Stop() => IsRunning = false;

    internal void SetError(string key)
    {
        _measurement = null;
        _target = null;
        _errorKey = key;
        RefreshText();
    }

    internal void SetResult(BenchmarkMeasurement measurement, string? target)
    {
        _measurement = measurement;
        _target = target;
        RefreshText();
    }

    internal void RefreshText() => OnPropertyChanged(string.Empty);
}
