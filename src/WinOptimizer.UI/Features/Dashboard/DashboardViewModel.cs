using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using System.Globalization;
using System.IO;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;
using WinOptimizer.UI.Features.Benchmark;
using WinOptimizer.UI.Features.Cleaner;
using WinOptimizer.UI.Features.Drivers;
using WinOptimizer.UI.Features.Management;
using WinOptimizer.UI.Features.Optimize;
using WinOptimizer.UI.Features.Optimize.Pages;
using WinOptimizer.UI.Features.Restore;
using WinOptimizer.UI.Features.Security;
using WinOptimizer.UI.Features.SoftwareApps;
using WinOptimizer.UI.Features.SystemInformation.ViewModels;

namespace WinOptimizer.UI.Features.Dashboard;

/// <summary>Presents the local PC inventory and samples resource use for the active dashboard.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private static readonly TimeSpan InventoryReuseWindow = TimeSpan.FromSeconds(30);
    private static readonly SystemInformationCategory[] SummaryCategories =
    [
        SystemInformationCategory.Machine, SystemInformationCategory.Windows,
        SystemInformationCategory.Processors, SystemInformationCategory.Graphics,
        SystemInformationCategory.Volumes, SystemInformationCategory.Battery
    ];

    private readonly ISystemInformationService _systemInformation;
    private readonly ISystemMonitorService _monitor;
    private readonly IInteractiveUserService _user;
    private readonly ILocalizationService _localization;
    private readonly ILogService _log;
    private readonly IApplicationModeService _mode;
    private readonly TimeProvider _clock;
    private readonly string _systemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "").TrimEnd('\\');
    private IReadOnlyList<SystemInformationSection> _inventory = [];
    private SystemMonitorSnapshot? _sample;
    private long? _inventoryReadAt;

    [ObservableProperty] public partial string WindowsDescription { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<DashboardFact> Facts { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<DashboardAction> Actions { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool CanCopy { get; private set; }

    public string Heading => Text("Nav_Dashboard");
    public string ComputerName => Environment.MachineName;
    public string QuickActionsTitle => Text("Dashboard_QuickActions");
    public string CopyLabel => Text("Dashboard_Copy");
    public IReadOnlyList<DashboardMetric> Metrics { get; } = [new(Icon.DeveloperBoard), new(Icon.Ram), new(Icon.HardDrive)];

    public DashboardViewModel(ISystemInformationService systemInformation, ISystemMonitorService monitor,
        IInteractiveUserService user, ILocalizationService localization, ILogService log, IApplicationModeService mode, TimeProvider clock)
    {
        _systemInformation = systemInformation;
        _monitor = monitor;
        _user = user;
        _localization = localization;
        _log = log;
        _mode = mode;
        _clock = clock;
        WindowsDescription = "";
        Facts = [];
        Actions = [];
    }

    /// <summary>Refreshes the visible strings from the current language and existing readings.</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(QuickActionsTitle));
        OnPropertyChanged(nameof(CopyLabel));
        Metrics[0].Title = Text("Dashboard_Processor");
        Metrics[1].Title = Text("SysMonitor_Memory");
        Metrics[2].Title = _localization.GetString("Dashboard_SystemDrive", _systemDrive);
        foreach (var metric in Metrics) metric.RefreshReadout();
        RefreshActions();
        PresentInventory();
        PresentMemory();
    }

    /// <summary>Uses the same mode restriction for recovery as its Advanced Tools entry point.</summary>
    internal void RefreshActions()
    {
        Actions =
        [
            new(Text("Benchmark_Title"), Icon.DataHistogram, typeof(BenchmarkPage)),
            new(Text("Dashboard_RestorePoints"), Icon.History, typeof(RestorePage), CanOpen(typeof(RestorePage))),
            new(Text("WindowsStatus_Title"), Icon.Wrench, typeof(WindowsStatusPage)),
            new(Text("Nav_Optimize"), Icon.Options, typeof(OptimizePage)),
            new(Text("Nav_SoftwareAndApps"), Icon.Apps, typeof(SoftwareAppsPage)),
            new(Text("Nav_Drivers"), Icon.PlugConnected, typeof(DriversPage)),
            new(Text("Security_Title"), Icon.Shield, typeof(SecurityPage)),
            new(Text("Nav_Cleaner"), Icon.Broom, typeof(CleanerPage)),
            new(Text("SpeedTest_Title"), Icon.Gauge, typeof(NetworkOptimizePage))
        ];
    }

    internal bool CanOpen(Type destination) => destination != typeof(RestorePage) || _mode.CurrentMode == WinOptimizerMode.Normal;

    /// <summary>Loads only the sections used by the dashboard, so startup avoids a full device scan.</summary>
    internal async Task LoadInventoryAsync(CancellationToken token)
    {
        if (token.IsCancellationRequested || _inventoryReadAt is long timestamp &&
            _clock.GetElapsedTime(timestamp) < InventoryReuseWindow) return;

        IsLoading = true;
        try
        {
            var inventory = await _systemInformation.ReadSectionsAsync(SummaryCategories, token);
            token.ThrowIfCancellationRequested();
            _inventory = inventory;
            PresentInventory();
            _inventoryReadAt = inventory.Count == SummaryCategories.Length && inventory.All(section => section.Error is null)
                ? _clock.GetTimestamp() : null;
            CanCopy = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _log.LogWarning($"Could not load the dashboard inventory: {error.Message}"); }
        finally { IsLoading = false; }
    }

    /// <summary>Publishes the first reading promptly and releases the CPU baseline when the page closes.</summary>
    internal async Task WatchUsageAsync(CancellationToken token)
    {
        PresentSample(null);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                var sample = await _monitor.ReadAsync(token);
                token.ThrowIfCancellationRequested();
                PresentSample(sample);
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { _log.LogWarning($"Could not sample dashboard CPU and memory: {error.Message}"); }
        finally { await _monitor.StopAsync(); }
    }

    /// <summary>Copies the displayed device summary, leaving out the transient usage readings.</summary>
    internal string GetCopyText() => string.Join(Environment.NewLine,
        new[] { ComputerName, WindowsDescription }.Where(line => line.Length != 0)
            .Concat(Facts.Select(fact => $"{fact.Label}: {fact.Value}")));

    internal string CopyFeedback(bool copied) => Text(copied ? "Dashboard_Copied" : "Dashboard_CopyFailed");

    /// <summary>Projects the existing typed inventory into device facts and volume capacity.</summary>
    private void PresentInventory()
    {
        var machine = Section(SystemInformationCategory.Machine)?.Items.FirstOrDefault();
        var windows = Section(SystemInformationCategory.Windows)?.Items.FirstOrDefault();
        var processors = Section(SystemInformationCategory.Processors);
        var graphics = Section(SystemInformationCategory.Graphics);
        var batteries = Section(SystemInformationCategory.Battery);
        var drive = Section(SystemInformationCategory.Volumes)?.Items.FirstOrDefault(item =>
            string.Equals(item.Name, _systemDrive, StringComparison.OrdinalIgnoreCase));
        Metrics[0].Detail = ProcessorDetail(processors, Number(machine, "LogicalProcessors"));
        var disk = UsedCapacity(Number(drive, "Capacity"), Number(drive, "FreeSpace"));
        var fileSystem = ReadText(drive, "FileSystem");
        Metrics[2].Detail = WithDetail(disk is { } volume
                ? _localization.GetString("Dashboard_DriveDetail", Bytes(volume.Total - volume.Used), Bytes(volume.Total)) : Unknown,
            JoinParts(fileSystem is null ? null : DetailLine("FileSystem", fileSystem), ReadText(drive, "VolumeLabel")));
        Metrics[2].UpdateUsage(disk?.Percent);

        var lastBoot = Property(windows, "LastBoot") as SystemInformationDate;
        var uptime = lastBoot?.Value is DateTime boot ? _clock.GetLocalNow() - new DateTimeOffset(boot) : (TimeSpan?)null;
        WindowsDescription = JoinParts(windows?.Name, ReadText(windows, "Build"), ReadText(windows, "Architecture"));
        var model = ReadText(machine, "Model");
        var manufacturer = ReadText(machine, "Manufacturer");
        var modelDescription = model is not null && manufacturer is not null && model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase)
            ? model : JoinParts(manufacturer, model);
        var graphicsDrivers = JoinParts(graphics?.Items.Select(item => ReadText(item, "DriverVersion")).Distinct().ToArray() ?? []);

        Facts =
        [
            new(Text("Dashboard_Processor"), DeviceNames(processors)),
            new(Text("Dashboard_Graphics"), WithDetail(DeviceNames(graphics),
                graphicsDrivers.Length > 0 ? DetailLine("DriverVersion", graphicsDrivers) : "")),
            new(Text("SystemInfo_Field_Model"), modelDescription.Length > 0 ? modelDescription : Unknown),
            new(Text("Dashboard_SignedInAs"), Format(new SystemInformationText(_user.InteractiveUserName))),
            new(Text("Dashboard_Uptime"), uptime is { Ticks: >= 0 } elapsed
                ? _localization.GetString("Dashboard_UptimeValue", elapsed.Days, elapsed.Hours, elapsed.Minutes) : Unknown),
            new(Text("SystemInfo_Field_LastBoot"), Format(lastBoot)),
            new(Text("SystemInfo_Field_InstalledOn"), Format(Property(windows, "InstalledOn"))),
            new(Text("Dashboard_Battery"), BatteryCharge(batteries))
        ];
    }

    /// <summary>Reports total physical cores only when all sockets supplied a count.</summary>
    private string ProcessorDetail(SystemInformationSection? processors, ulong? logicalProcessors)
    {
        ulong? cores = processors is { Items.Count: > 0 } ? 0UL : null;
        ulong maximumClock = 0;
        foreach (var processor in processors?.Items ?? [])
        {
            var count = Number(processor, "Cores");
            cores = count is > 0 ? cores + count : null;
            maximumClock = Math.Max(maximumClock, Number(processor, "MaximumClock") ?? 0);
        }

        var topology = JoinParts(cores is ulong countOfCores ? DetailLine("Cores", countOfCores.ToString("N0", CultureInfo.CurrentUICulture)) : null,
            logicalProcessors is > 0 ? _localization.GetString("Dashboard_ProcessorDetail", logicalProcessors.Value) : null);
        var clock = maximumClock > 0 ? DetailLine("MaximumClock", Format(new SystemInformationNumber(maximumClock, SystemInformationUnit.Megahertz))) : "";
        return WithDetail(topology.Length > 0 ? topology : clock.Length > 0 ? "" : Unknown, clock);
    }

    /// <summary>Distinguishes a missing battery from a missing battery reading.</summary>
    private string BatteryCharge(SystemInformationSection? batteries) => batteries switch
    {
        { Error: null, Items.Count: 0 } => Text("Dashboard_NoBattery"),
        { Items.Count: > 0 } => string.Join(" · ", batteries.Items.Select(item => Format(Property(item, "Charge")))),
        _ => Unknown
    };

    private string DeviceNames(SystemInformationSection? section)
    {
        var names = section?.Items.Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray();
        return names is { Length: > 0 } ? string.Join(" · ", names) : Unknown;
    }

    /// <summary>Refreshes the counter cards without reformatting unchanged memory readings.</summary>
    private void PresentSample(SystemMonitorSnapshot? sample)
    {
        var previous = _sample;
        _sample = sample;
        Metrics[0].UpdateUsage(sample?.CpuUsagePercent);
        if (previous?.TotalMemoryBytes != sample?.TotalMemoryBytes || previous?.AvailableMemoryBytes != sample?.AvailableMemoryBytes)
            PresentMemory();
    }

    private void PresentMemory()
    {
        var memory = UsedCapacity(_sample?.TotalMemoryBytes, _sample?.AvailableMemoryBytes);
        Metrics[1].Detail = memory is { } ram
            ? WithDetail(_localization.GetString("Dashboard_MemoryDetail", Bytes(ram.Used), Bytes(ram.Total)),
                DetailLine("AvailableMemory", Bytes(ram.Total - ram.Used))) : Unknown;
        Metrics[1].UpdateUsage(memory?.Percent);
    }

    private static (ulong Used, ulong Total, double Percent)? UsedCapacity(ulong? total, ulong? free) =>
        total is ulong size && size > 0 && free is ulong available && available <= size
            ? (size - available, size, 100.0 * (size - available) / size) : null;

    private SystemInformationSection? Section(SystemInformationCategory category) =>
        _inventory.FirstOrDefault(section => section.Category == category);

    private static SystemInformationValue? Property(SystemInformationItem? item, string label)
    {
        var key = "SystemInfo_Field_" + label;
        return item?.Properties.FirstOrDefault(property => property.LabelKey == key)?.Value;
    }

    private static string? ReadText(SystemInformationItem? item, string label) => (Property(item, label) as SystemInformationText)?.Value;
    private static ulong? Number(SystemInformationItem? item, string label) => (Property(item, label) as SystemInformationNumber)?.Value;
    private static string JoinParts(params string?[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    private static string WithDetail(string main, string detail) =>
        main.Length == 0 ? detail : detail.Length == 0 ? main : $"{main}\n{detail}";
    private string DetailLine(string label, string value) => $"{Text("SystemInfo_Field_" + label)} · {value}";
    private string Bytes(ulong number) => Format(new SystemInformationNumber(number, SystemInformationUnit.Bytes));
    private string Format(SystemInformationValue? value) => value is null ? Unknown : SystemInformationPropertyViewModel.FormatValue(value, _localization);
    private string Unknown => Text("SystemInfo_Unknown");
    private string Text(string key) => _localization.GetString(key);

    internal void ReportClipboardFailure(Exception error) => _log.LogWarning($"Could not copy the dashboard summary: {error.Message}");
    internal void ReportSessionFailure(Exception error) => _log.LogWarning($"Could not complete dashboard sampling: {error.Message}");
}

public sealed record DashboardFact(string Label, string Value);
public sealed record DashboardAction(string Label, Icon Icon, Type Destination, bool IsEnabled = true);

/// <summary>Keeps the same metric card during sampling and exposes missing readings separately from zero.</summary>
public sealed partial class DashboardMetric(Icon icon) : ObservableObject
{
    private double? _usage;

    public Icon Icon { get; } = icon;
    [ObservableProperty] public partial string Title { get; internal set; } = "";
    [ObservableProperty] public partial string Detail { get; internal set; } = "";
    public double Percent => _usage ?? 0;
    public string Readout => _usage is double percent ? (percent / 100).ToString("P0", CultureInfo.CurrentUICulture) : "—";

    /// <summary>Updates the gauge at the resolution shown by its label.</summary>
    internal void UpdateUsage(double? percent)
    {
        double? rounded = percent is double value ? Math.Round(value) : null;
        if (_usage == rounded) return;
        var previous = Percent;
        _usage = rounded;
        if (previous != Percent) OnPropertyChanged(nameof(Percent));
        RefreshReadout();
    }

    internal void RefreshReadout() => OnPropertyChanged(nameof(Readout));
}
