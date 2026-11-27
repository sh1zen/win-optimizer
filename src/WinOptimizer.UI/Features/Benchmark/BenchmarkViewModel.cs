using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Benchmark;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;
using WinOptimizer.UI.Features.SystemInformation.ViewModels;

namespace WinOptimizer.UI.Features.Benchmark;

/// <summary>Presents local hardware and coordinates one cancellable sequence of benchmark results.</summary>
public sealed partial class BenchmarkViewModel : ObservableObject
{
    private readonly IBenchmarkService _service;
    private readonly ILocalizationService _localization;
    private readonly ILogService _log;
    private BenchmarkSystemInfo? _hardware;
    private CancellationTokenSource? _run;
    private DateTimeOffset? _lastRun;

    [ObservableProperty] public partial IReadOnlyList<SystemInformationPropertyViewModel> HardwareFacts { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<string> DriveRoots { get; private set; } = [];
    [ObservableProperty] public partial string? SelectedDrive { get; set; }
    [ObservableProperty] public partial BenchmarkGraphicsAdapter? SelectedGraphicsAdapter { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial bool HasHardwareError { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; private set; } = "";

    public IReadOnlyList<BenchmarkRow> Rows { get; }
    public bool CanStart => !IsRunning && !IsLoading;
    public bool CanSelectDrive => CanStart && DriveRoots.Count > 0;
    public bool CanSelectGraphicsAdapter => CanStart && GraphicsAdapters.Count > 0;
    public IReadOnlyList<BenchmarkGraphicsAdapter> GraphicsAdapters => _hardware?.Graphics ?? [];
    public bool HasStatus => Status.Length != 0;
    public string Heading => _localization.GetString("Benchmark_Title");
    public string HardwareTitle => _localization.GetString("Nav_SystemInformation");
    public string TestsTitle => _localization.GetString("Benchmark_Tests");
    public string RunAllLabel => _localization.GetString("Benchmark_RunAll");
    public string CancelLabel => _localization.GetString("Button_Cancel");
    public string DriveLabel => _localization.GetString("Benchmark_Drive");
    public string GraphicsLabel => _localization.GetString("Dashboard_Graphics");
    public string HardwareError => _localization.GetString("Benchmark_InfoUnavailable");

    public BenchmarkViewModel(IBenchmarkService service, ILocalizationService localization, ILogService log)
    {
        _service = service;
        _localization = localization;
        _log = log;
        Rows =
        [
            new(BenchmarkKind.CpuClock, "Benchmark_CpuClock", Icon.Gauge, localization),
            new(BenchmarkKind.CpuFlops, "Benchmark_Flops", Icon.DeveloperBoard, localization),
            new(BenchmarkKind.MemoryBandwidth, "Benchmark_MemoryBandwidth", Icon.Ram, localization),
            new(BenchmarkKind.DiskWrite, "Benchmark_DiskWrite", Icon.HardDrive, localization),
            new(BenchmarkKind.GpuFlops, "Benchmark_GpuFlops", Icon.Desktop, localization)
        ];
        UpdateAvailability();
    }

    /// <summary>Refreshes hardware and the list of available volumes when the page is opened.</summary>
    internal async Task LoadAsync(CancellationToken token)
    {
        IsLoading = true;
        HasHardwareError = false;
        try
        {
            var hardware = await _service.ReadSystemAsync(token);
            token.ThrowIfCancellationRequested();
            var previousGraphicsAdapter = SelectedGraphicsAdapter?.Id;
            _hardware = hardware;
            OnPropertyChanged(nameof(GraphicsAdapters));
            SelectedGraphicsAdapter = GraphicsAdapters.FirstOrDefault(adapter => adapter.Id == previousGraphicsAdapter) ??
                GraphicsAdapters.FirstOrDefault();
            var previousDrive = SelectedDrive;
            DriveRoots = hardware.DriveRoots;
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
            SelectedDrive = DriveRoots.FirstOrDefault(root => root.Equals(previousDrive, StringComparison.OrdinalIgnoreCase)) ??
                DriveRoots.FirstOrDefault(root => root.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)) ?? DriveRoots.FirstOrDefault();
            HasHardwareError = hardware.Inventory.Count == 0 || hardware.Inventory.All(section => section.Error is not null);
            PresentHardware();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            HasHardwareError = true;
            _log.LogWarning($"Could not read benchmark hardware information: {error.Message}");
        }
        finally { IsLoading = false; }
    }

    /// <summary>Runs the chosen row or the full test list in order, keeping completed results if cancelled.</summary>
    internal async Task RunAsync(BenchmarkRow? row, CancellationToken visit)
    {
        if (!CanStart || visit.IsCancellationRequested || row is { CanRun: false }) return;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(visit);
        _run = run;
        IsRunning = true;
        var drive = SelectedDrive;
        var graphics = SelectedGraphicsAdapter;
        try
        {
            foreach (var test in row is null ? Rows : [row])
            {
                run.Token.ThrowIfCancellationRequested();
                if (test.Kind == BenchmarkKind.DiskWrite && drive is null)
                {
                    test.SetError("Benchmark_DriveUnavailable");
                    continue;
                }
                if (test.Kind == BenchmarkKind.GpuFlops && graphics is null)
                {
                    test.SetError("Benchmark_GpuUnavailable");
                    continue;
                }

                test.Start();
                RefreshStatus();
                try
                {
                    var request = new BenchmarkRequest(test.Kind, test.Kind == BenchmarkKind.DiskWrite ? drive : null,
                        test.Kind == BenchmarkKind.GpuFlops ? graphics?.Id : null);
                    var result = await _service.RunAsync(request, run.Token);
                    run.Token.ThrowIfCancellationRequested();
                    test.SetResult(result, test.Kind switch
                    {
                        BenchmarkKind.DiskWrite => drive,
                        BenchmarkKind.GpuFlops => graphics?.Name,
                        _ => null
                    });
                    _lastRun = result.CompletedAt;
                }
                catch (BenchmarkException error)
                {
                    if (error.InnerException is not null) _log.LogWarning($"Benchmark {test.Kind}: {error}");
                    test.SetError(error.Failure switch
                    {
                        BenchmarkFailure.Busy => "Benchmark_Busy",
                        BenchmarkFailure.ClockUnavailable => "Benchmark_ClockUnavailable",
                        BenchmarkFailure.DriveUnavailable => "Benchmark_DriveUnavailable",
                        BenchmarkFailure.InsufficientSpace => "Benchmark_DriveSpace",
                        BenchmarkFailure.DiskWriteFailed => "Benchmark_DriveWriteFailed",
                        BenchmarkFailure.GraphicsUnavailable => "Benchmark_GpuUnavailable",
                        _ => "Benchmark_Failed"
                    });
                }
                catch (Exception error) when (error is not OperationCanceledException || !run.IsCancellationRequested)
                {
                    _log.LogWarning($"Benchmark {test.Kind} failed: {error}");
                    test.SetError("Benchmark_Failed");
                }
                finally { test.Stop(); }
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        finally
        {
            _run = null;
            IsRunning = false;
            RefreshStatus();
        }
    }

    internal void Cancel() => _run?.Cancel();

    internal void RefreshText()
    {
        PresentHardware();
        foreach (var row in Rows) row.RefreshText();
        OnPropertyChanged(string.Empty);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var current = Rows.FirstOrDefault(row => row.IsRunning);
        Status = current is not null ? _localization.GetString("Benchmark_Running", current.Title) :
            _lastRun is { } time ? _localization.GetString("Benchmark_LastRun", time.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)) : "";
    }

    private void UpdateAvailability()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanSelectDrive));
        OnPropertyChanged(nameof(CanSelectGraphicsAdapter));
        foreach (var row in Rows)
            row.CanRun = CanStart && (row.Kind switch
            {
                BenchmarkKind.DiskWrite => SelectedDrive is not null,
                BenchmarkKind.GpuFlops => SelectedGraphicsAdapter is not null,
                _ => true
            });
    }

    partial void OnSelectedDriveChanged(string? value) => UpdateAvailability();
    partial void OnSelectedGraphicsAdapterChanged(BenchmarkGraphicsAdapter? value) => UpdateAvailability();
    partial void OnDriveRootsChanged(IReadOnlyList<string> value) => UpdateAvailability();
    partial void OnIsLoadingChanged(bool value) => UpdateAvailability();
    partial void OnIsRunningChanged(bool value) => UpdateAvailability();

    private void PresentHardware()
    {
        if (_hardware is not { } hardware) return;
        IReadOnlyList<SystemInformationItem> Items(SystemInformationCategory category) =>
            hardware.Inventory.FirstOrDefault(section => section.Category == category)?.Items ?? [];
        var processors = Items(SystemInformationCategory.Processors);
        var machine = Items(SystemInformationCategory.Machine).FirstOrDefault();
        var windows = Items(SystemInformationCategory.Windows).FirstOrDefault();
        var processor = processors.FirstOrDefault();
        var coreCounts = processors.Select(item => (Read(item, "SystemInfo_Field_Cores") as SystemInformationNumber)?.Value).ToArray();
        var os = string.Join(" · ", new[] { windows?.Name,
            (Read(windows, "SystemInfo_Field_Version") as SystemInformationText)?.Value }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var graphicsNames = hardware.Graphics.Count > 0 ? hardware.Graphics.Select(adapter => adapter.Name) :
            Items(SystemInformationCategory.Graphics).Select(item => item.Name);
        var videoMemory = hardware.Graphics.Select(adapter =>
        {
            var size = SystemInformationPropertyViewModel.FormatValue(new SystemInformationNumber(adapter.DedicatedVideoMemory,
                SystemInformationUnit.Bytes), _localization);
            return hardware.Graphics.Count == 1 ? size : $"{adapter.Name} · {size}";
        });

        HardwareFacts =
        [
            Fact("Dashboard_Processor", new SystemInformationText(JoinNames(processors.Select(item => item.Name)))),
            Fact("SystemInfo_Field_Manufacturer", Read(processor, "SystemInfo_Field_Manufacturer")),
            Fact("SystemInfo_Field_MaximumClock", Read(processor, "SystemInfo_Field_MaximumClock")),
            Fact("Benchmark_CpuSignature", new SystemInformationText(hardware.Processor is { } identity
                ? _localization.GetString("Benchmark_CpuSignatureValue", identity.Family, identity.Model.ToString("X", CultureInfo.InvariantCulture),
                    identity.Stepping) : null)),
            Fact("SystemInfo_Field_Cores", new SystemInformationNumber(coreCounts.Length > 0 && coreCounts.All(count => count.HasValue)
                ? coreCounts.Aggregate(0UL, (sum, count) => sum + count.GetValueOrDefault()) : null, SystemInformationUnit.Count)),
            Fact("SystemInfo_Field_LogicalProcessors", Read(machine, "SystemInfo_Field_LogicalProcessors")),
            Fact("Benchmark_Instructions", new SystemInformationText(hardware.Processor is { } cpu ? string.Join(", ", cpu.Instructions) : null)),
            Fact("SystemInfo_Field_TotalMemory", Read(machine, "SystemInfo_Field_TotalMemory")),
            Fact("SystemInfo_Section_Windows", new SystemInformationText(os)),
            Fact("Dashboard_Graphics", new SystemInformationText(JoinNames(graphicsNames))),
            Fact("Benchmark_VideoMemory", new SystemInformationText(string.Join(Environment.NewLine, videoMemory)))
        ];
    }

    private static string JoinNames(IEnumerable<string?> names) =>
        string.Join(Environment.NewLine, names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static SystemInformationValue? Read(SystemInformationItem? item, string label) =>
        item?.Properties.FirstOrDefault(property => property.LabelKey == label)?.Value;

    private SystemInformationPropertyViewModel Fact(string label, SystemInformationValue? value) =>
        new(new(label, value ?? new SystemInformationText(null)), _localization);
}
