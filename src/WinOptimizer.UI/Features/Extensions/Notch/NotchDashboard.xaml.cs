using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Utilities;
using WinOptimizer.UI.Features.Extensions.ViewModels;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Displays the selected dashboard rows and formats the latest available measurements.</summary>
public sealed partial class NotchDashboard : UserControl
{
    private const string Unavailable = "—";
    private const double ReadingRowSpace = 90;
    private NotchModule _modules;
    private NotchReadingRow[] _rows = [];
    private NotchReadingRow? _uptime;
    private NotchReadingRow? _traffic;
    private NotchPercentTrend? _cpu;
    private NotchPercentTrend? _memory;
    private MonitorMetricSection? _gpu;
    private NotchReadingSnapshot _snapshot = new();
    private ConnectionDailyUsage? _today;
    private bool _active;

    public NotchDashboard() => InitializeComponent();

    internal bool CanScroll => ReadingsScroll.ScrollableHeight > 0;

    /// <summary>Uses the measured cards, reserving space for the selected rows before the first layout.</summary>
    internal double PreferredHeight => Readings.ActualHeight > 0
        // Leave one DIP for rounding when the native window is sized in physical pixels.
        ? Math.Ceiling(Readings.ActualHeight) + 1
        : Math.Ceiling(_rows.Length / (double)(int)Resources["ReadingColumns"]) * ReadingRowSpace;

    internal event EventHandler? ReadingHeightChanged;

    private void OnReadingsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Height > 0 && args.NewSize.Height != args.PreviousSize.Height)
            ReadingHeightChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplyModules(NotchModule modules, ILocalizationService localization)
    {
        modules &= NotchExtension.DashboardModules;
        if (modules != _modules)
        {
            _modules = modules;
            _cpu = modules.HasFlag(NotchModule.Cpu) ? _cpu ?? new(NotchModule.Cpu, localization) : null;
            _memory = modules.HasFlag(NotchModule.Memory) ? _memory ?? new(NotchModule.Memory, localization) : null;
            _gpu = modules.HasFlag(NotchModule.Gpu) ? _gpu ?? new(SystemMonitorMetricKind.GpuUsage, localization) : null;
            _rows = Enum.GetValues<NotchModule>()
                .Where(module => module != NotchModule.None && modules.HasFlag(module))
                .Select(module => new NotchReadingRow(module) { Graph = module switch
                {
                    NotchModule.Cpu => _cpu,
                    NotchModule.Memory => _memory,
                    NotchModule.Gpu => _gpu,
                    _ => null
                } }).ToArray();
            _uptime = _rows.FirstOrDefault(row => row.Module == NotchModule.Uptime);
            _traffic = _rows.FirstOrDefault(row => row.Module == NotchModule.Traffic);
            Readings.ItemsSource = _rows;
        }
        foreach (var row in _rows)
            row.Title = NotchModuleOption.GetTitle(localization, row.Module);
        if (_active) RefreshPresentation(localization);
    }

    /// <summary>Updates the presentation only when the System view becomes visible.</summary>
    internal void SetActive(bool active, ILocalizationService localization)
    {
        if (_active == active) return;
        _active = active;
        if (_active) RefreshPresentation(localization);
    }

    private void RefreshPresentation(ILocalizationService localization)
    {
        _cpu?.Refresh();
        _memory?.Refresh();
        _gpu?.RefreshLabels();
        Render(localization);
    }

    /// <summary>Retains history independently of view changes and updates the charts when visible.</summary>
    internal void PublishSystem(NotchReadingSnapshot snapshot, ILocalizationService localization)
    {
        _snapshot = snapshot;
        _cpu?.Publish(snapshot.System?.CpuUsagePercent, _active);
        _memory?.Publish(snapshot.MemoryPercent, _active);
        _gpu?.Publish(snapshot.System?.HardwareMetrics.Where(metric => metric.Kind == SystemMonitorMetricKind.GpuUsage) ?? [], _active);
        if (_active) Render(localization);
    }

    /// <summary>Updates today's Windows usage independently of live counter samples.</summary>
    internal void PublishToday(ConnectionDailyUsage? usage, ILocalizationService localization)
    {
        _today = usage;
        if (_active) RenderToday(localization);
    }

    internal void RefreshUptime(ILocalizationService localization)
    {
        if (_uptime is not null) _uptime.Value = FormatUptime(localization);
    }

    private void Render(ILocalizationService localization)
    {
        var system = _snapshot.System;
        foreach (var row in _rows)
        {
            row.Value = row.Module switch
            {
                NotchModule.Cpu => Percent(system?.CpuUsagePercent),
                NotchModule.Memory => Memory(_snapshot.Memory),
                NotchModule.Gpu => Percent(_snapshot.GpuPercent),
                NotchModule.Traffic => $"↓ {SystemMonitorReadingsViewModel.Rate(system?.DownloadBytesPerSecond)}   ↑ {SystemMonitorReadingsViewModel.Rate(system?.UploadBytesPerSecond)}",
                NotchModule.Uptime => FormatUptime(localization),
                _ => throw new InvalidOperationException("The Notch dashboard cannot display this module.")
            };
        }
        RenderToday(localization);
    }

    private static string Percent(double? value) => value is double percent ? $"{percent:0}%" : Unavailable;

    private static string Memory((ulong Used, ulong Total)? usage)
    {
        const double bytesPerGibibyte = 1024 * 1024 * 1024;
        return usage is { } memory ? $"{memory.Used / bytesPerGibibyte:0.0} / {memory.Total / bytesPerGibibyte:0.0} GB" : Unavailable;
    }

    private void RenderToday(ILocalizationService localization)
    {
        if (_traffic is null) return;
        var total = _today is { BytesSent: decimal sent, BytesReceived: decimal received } usage &&
            usage.Date == DateOnly.FromDateTime(DateTime.Today)
            ? ByteSizeFormatter.FormatBytes(sent + received, CultureInfo.CurrentCulture) : Unavailable;
        _traffic.Detail = localization.GetString("Notch_TrafficToday", total);
    }

    private static string FormatUptime(ILocalizationService localization)
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return uptime.Days > 0 ? localization.GetString("Notch_UptimeDays", uptime.Days, uptime.Hours, uptime.Minutes)
            : localization.GetString("Notch_UptimeHours", uptime.Hours, uptime.Minutes);
    }
}

/// <summary>Retains the identity of a dashboard row while its reading and translated label change.</summary>
public sealed partial class NotchReadingRow(NotchModule module) : ObservableObject
{
    internal NotchModule Module { get; } = module;
    public IMonitorChartData? Graph { get; internal init; }
    public Visibility DetailVisibility => Module == NotchModule.Traffic ? Visibility.Visible : Visibility.Collapsed;
    [ObservableProperty] public partial string Title { get; internal set; } = "";
    [ObservableProperty] public partial string Value { get; internal set; } = "";
    [ObservableProperty] public partial string Detail { get; internal set; } = "";
}

/// <summary>Feeds a bounded CPU or memory percentage series into the shared chart.</summary>
internal sealed class NotchPercentTrend(NotchModule module, ILocalizationService localization) : IMonitorChartData
{
    public IReadOnlyList<MonitorMetricSeries> Readings { get; } = module == NotchModule.Cpu
        ? [new("CPU", "CPU", 0, 0)] : [new("RAM", "RAM", 1, 0)];
    public bool IsEmpty { get; private set; } = true;
    public double Minimum => 0;
    public double Maximum => 100;
    public string MinimumLabel => "0%";
    public string MaximumLabel => "100%";
    public string Label => NotchModuleOption.GetTitle(localization, module);
    public string UnavailableLabel => localization.GetString("SysMonitor_Unavailable");
    public string HistoryLabel => localization.GetString("SysMonitor_History");
    public event EventHandler? ReadingsUpdated;

    internal void Publish(double? value, bool notify)
    {
        if (value is double reading && double.IsFinite(reading)) value = Math.Clamp(reading, 0, 100);
        else value = null;
        var series = Readings[0];
        series.Append(value, value is double percent ? $"{percent:0}%" : "—");
        IsEmpty = !series.History.Any(sample => sample.HasValue);
        if (notify) Refresh();
    }

    internal void Refresh() => ReadingsUpdated?.Invoke(this, EventArgs.Empty);
}

/// <summary>Selects a plot card for readings with a sensor history.</summary>
public sealed partial class NotchReadingTemplateSelector : DataTemplateSelector
{
    public DataTemplate TextTemplate { get; set; } = null!;
    public DataTemplate GraphTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        item is NotchReadingRow { Graph: not null } ? GraphTemplate : TextTemplate;
}
