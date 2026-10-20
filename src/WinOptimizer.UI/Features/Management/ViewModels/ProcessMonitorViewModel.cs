using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Management.ViewModels;

/// <summary>Owns stable process rows, selected-lifetime details and page-scoped observation without process control.</summary>
public partial class ProcessMonitorViewModel : UtilityViewModel
{
    private readonly IProcessMonitorService _service;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<ProcessIdentity, ProcessDisplayRow> _all = [];
    private readonly HashSet<ProcessIdentity> _collapsed = [];
    private IReadOnlyDictionary<ProcessIdentity, ProcessIdentity> _parents = new Dictionary<ProcessIdentity, ProcessIdentity>();
    private CancellationTokenSource? _lifetime;
    private Task _loop = Task.CompletedTask;
    private Task _operation = Task.CompletedTask;
    private bool _sampling;
    private bool _capturing;
    private bool _sortDescending;
    private ProcessSnapshot? _snapshot;
    private ProcessInspection? _inspection;
    private ProcessPortSnapshot? _ports;
    private ProcessFileSnapshot? _files;
    private ProcessNetworkSnapshot? _network;
    protected override string TitleKey => "ProcessMonitor_Title";
    public ObservableCollection<ProcessDisplayRow> Rows { get; } = [];
    public ObservableCollection<ProcessPortDisplay> Ports { get; } = [];
    public ObservableCollection<ProcessOpenFile> Files { get; } = [];
    public ObservableCollection<ProcessIpDisplay> Hosts { get; } = [];
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int SortMode { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(LoadFilesCommand))] public partial bool LicenseAccepted { get; set; }
    [ObservableProperty] public partial ProcessDisplayRow? SelectedRow { get; private set; }

    public string Hint => Text("ProcessMonitor_Hint");
    public string SearchLabel => Text("ProcessMonitor_Search");
    public string TreeLabel => Text("ProcessMonitor_Tree");
    public string CpuLabel => "CPU";
    public string MemoryLabel => Text("ProcessMonitor_Memory");
    public string NameLabel => Text("ProcessMonitor_Name");
    public bool SortDescending => _sortDescending;
    public string NameHeader => NameLabel + SortIndicator(3);
    public string PidHeader => "PID" + SortIndicator(4);
    public string CpuHeader => CpuLabel + SortIndicator(1);
    public string MemoryHeader => MemoryLabel + SortIndicator(2);
    public string SortHint => Text("ProcessMonitor_SortHint");
    public string SortLabel => Text("ProcessMonitor_Sort");
    public string DetailsLabel => Text("ProcessMonitor_Details");
    public string FilesLabel => Text("ProcessMonitor_Files");
    public string PortsLabel => Text("ProcessMonitor_Ports");
    public string NetworkLabel => Text("ProcessMonitor_Network");
    public string LoadFilesLabel => Text("ProcessMonitor_LoadFiles");
    public string LicenseLabel => Text("ProcessMonitor_License");
    public string FilesHint => Text("ProcessMonitor_FilesHint");
    public string NetworkHint => Text("ProcessMonitor_NetworkHint");
    public string StartNetworkLabel => Text("ProcessMonitor_StartNetwork");
    public string StopNetworkLabel => Text("ProcessMonitor_StopNetwork");
    public string RefreshDetailsLabel => Text("ProcessMonitor_RefreshDetails");
    public string EmptyLabel => Text(_snapshot == null ? "ProcessMonitor_Loading" : "ProcessMonitor_Empty");
    public bool IsEmpty => Rows.Count == 0;
    public bool HasSelection => SelectedRow != null;
    public bool SelectedAlive => SelectedRow != null && _all.ContainsKey(SelectedRow.Identity);
    public string SelectedName => SelectedRow == null ? Text("ProcessMonitor_Select") : $"{SelectedRow.Name} · PID {SelectedRow.Identity.Pid}";
    public string SelectedState => SelectedRow == null ? "" : Text(SelectedAlive ? SelectedRow.Identity.CanInspect ? "ProcessMonitor_Observed" : "ProcessMonitor_Protected" : "ProcessMonitor_Exited");
    public string SelectedPath => SelectedRow?.Metric.Reading.ImagePath ?? "";
    public string SelectedCounters => SelectedRow == null ? "" : $"CPU {SelectedRow.Cpu} · {MemoryLabel} {SelectedRow.Memory} · Private {SelectedRow.PrivateMemory}\nI/O R {SelectedRow.ReadRate} · W {SelectedRow.WriteRate}\nThreads {SelectedRow.Metric.Reading.Threads?.ToString() ?? "—"} · Handles {SelectedRow.Metric.Reading.Handles?.ToString() ?? "—"}";
    public string SelectedMetadata => _inspection == null ? "" : string.Join("\n", _inspection.Description, _inspection.Publisher, _inspection.Owner, _inspection.CommandLine);
    public string SelectedDiagnostic => string.Join("\n", new[] { SelectedRow?.Metric.Reading.Diagnostic ?? "", _inspection == null ? "" : string.Join("\n", _inspection.Warnings), _ports == null ? "" : string.Join("\n", _ports.Warnings) }.Where(value => value.Length > 0));
    public string Summary => string.Format(Text("ProcessMonitor_Summary"), Rows.Count, _all.Count, _snapshot?.CapturedAt.ToLocalTime().ToString("T") ?? "—");
    public string PortsSummary => _ports == null ? Text("ProcessMonitor_NoPortsSnapshot") : $"{_ports.CapturedAt.ToLocalTime():T} · {Ports.Count} {PortsLabel}";
    public string FilesSummary => _files == null ? Text("ProcessMonitor_NoFilesSnapshot") : $"{_files.CapturedAt.ToLocalTime():T} · {Files.Count} {FilesLabel}\n{_files.Diagnostic}";
    public string NetworkSummary => _network == null ? Text("ProcessMonitor_NoNetwork") : string.Format(Text("ProcessMonitor_NetworkSummary"),
        _network.StartedAt.ToLocalTime().ToString("T"), _network.CapturedAt.ToLocalTime().ToString("T"),
        Text("ProcessMonitor_Network" + _network.State), _network.LostEvents?.ToString() ?? "—", _network.UnattributedEvents) +
        (_network.CapacityReached ? "\n" + Text("ProcessMonitor_Capacity") : "") + (_network.Diagnostic.Length > 0 ? "\n" + _network.Diagnostic : "");
    public string NetworkTotals => _network == null ? "" : $"↑ {ProcessDisplayRow.Bytes(_network.Hosts.Sum(host => (decimal)host.SentBytes))} · ↓ {ProcessDisplayRow.Bytes(_network.Hosts.Sum(host => (decimal)host.ReceivedBytes))}";
    public bool CanInspect => IsIdle && SelectedAlive && SelectedRow!.Identity.CanInspect;
    public bool CanLoadFiles => CanInspect && LicenseAccepted;
    public bool CanStartNetwork => CanInspect && !_capturing;
    public bool CanStopNetwork => IsIdle && _capturing;

    public ProcessMonitorViewModel(IProcessMonitorService service, ILocalizationService localization, IApplicationModeService mode,
        IDispatcherService dispatcher, ITaskProgressService tasks) : base(localization, mode, dispatcher, tasks, "Nav_Management") => _service = service;

    /// <summary>Starts only page-visible polling and waits for any previous page lifetime to drain.</summary>
    public async Task ActivateAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); _lifetime = new(); _loop = PollAsync(_lifetime.Token); }
        finally { _lifecycle.Release(); }
    }

    public async Task DeactivateAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var lifetime = _lifetime; _lifetime = null; lifetime?.Cancel();
        try { await _loop; await _operation; }
        finally
        {
            try { _network = await _service.StopNetworkAsync() ?? _network; }
            finally { lifetime?.Dispose(); _capturing = false; OnPropertyChanged(string.Empty); NotifyActions(); }
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await SampleAsync(token);
                if (_capturing)
                {
                    try { var result = await _service.ReadNetworkAsync(token); token.ThrowIfCancellationRequested(); ApplyNetwork(result); }
                    catch (UtilityOperationException ex) { Status = ex.Message; IsError = true; }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => SampleAsync(_lifetime?.Token ?? CancellationToken.None);

    /// <summary>Publishes one complete sample and reuses row identities so selection survives live updates.</summary>
    public async Task SampleAsync(CancellationToken token)
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var snapshot = await _service.SampleAsync(token); token.ThrowIfCancellationRequested();
            _snapshot = snapshot;
            var identities = snapshot.Processes.Select(metric => metric.Reading.Identity).ToHashSet();
            foreach (var id in _all.Keys.Where(id => !identities.Contains(id)).ToArray()) { _all.Remove(id); _collapsed.Remove(id); }
            foreach (var metric in snapshot.Processes)
            {
                if (_all.TryGetValue(metric.Reading.Identity, out var row)) row.Apply(metric);
                else _all.Add(metric.Reading.Identity, new(metric));
            }
            _parents = ProcessHierarchy.Parents(snapshot.Processes); FilterRows();
            if (SelectedAlive && !IsBusy)
            {
                var identity = SelectedRow!.Identity;
                try { var ports = await _service.ReadPortsAsync(identity, token); token.ThrowIfCancellationRequested(); if (SelectedRow?.Identity == identity) ApplyPorts(ports); }
                catch (UtilityOperationException ex) { Status = ex.Message; IsError = true; }
            }
            OnPropertyChanged(string.Empty); NotifyActions();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is UtilityOperationException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!token.IsCancellationRequested) { Status = ex.Message; IsError = true; } }
        finally { _sampling = false; }
    }

    public Task SelectAsync(ProcessDisplayRow row)
    {
        if (!IsIdle || ReferenceEquals(SelectedRow, row) || !_all.TryGetValue(row.Identity, out var known) || !ReferenceEquals(known, row)) return Task.CompletedTask;
        return RunOperation(async token =>
        {
            _network = await _service.StopNetworkAsync(); _capturing = false;
            token.ThrowIfCancellationRequested();
            SelectedRow = row; _inspection = null; _ports = null; _files = null; _network = null;
            Files.Clear(); Ports.Clear(); Hosts.Clear(); OnPropertyChanged(string.Empty);
            if (!row.Identity.CanInspect) return;
            var inspection = await _service.InspectAsync(row.Identity, token); token.ThrowIfCancellationRequested();
            _inspection = inspection; ApplyPorts(new(inspection.Identity, inspection.CapturedAt, inspection.Ports, inspection.Warnings));
        });
    }

    [RelayCommand(CanExecute = nameof(CanInspect))]
    private Task RefreshDetailsAsync() => RunOperation(async token =>
    {
        var id = SelectedRow!.Identity;
        var result = await _service.InspectAsync(id, token); token.ThrowIfCancellationRequested(); _inspection = result;
        ApplyPorts(new(result.Identity, result.CapturedAt, result.Ports, result.Warnings));
    });

    [RelayCommand(CanExecute = nameof(CanLoadFiles))]
    private Task LoadFilesAsync() => CanLoadFiles ? RunOperation(async token =>
    {
        var result = await _service.ReadOpenFilesAsync(SelectedRow!.Identity, LicenseAccepted, token); token.ThrowIfCancellationRequested();
        _files = result; Files.Clear(); foreach (var file in result.Files) Files.Add(file);
        OnPropertyChanged(nameof(FilesSummary));
    }) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStartNetwork))]
    private Task StartNetworkAsync() => CanStartNetwork ? RunOperation(async token =>
    {
        await _service.StartNetworkAsync(SelectedRow!.Identity, token); token.ThrowIfCancellationRequested();
        var snapshot = await _service.ReadNetworkAsync(token); token.ThrowIfCancellationRequested(); ApplyNetwork(snapshot);
    }) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStopNetwork))]
    private Task StopNetworkAsync() => RunOperation(async token => { var result = await _service.StopNetworkAsync(); token.ThrowIfCancellationRequested(); ApplyNetwork(result); });

    /// <summary>Cancellation suppresses stale page updates while the native resource owner finishes cleanup.</summary>
    private Task RunOperation(Func<CancellationToken, Task> operation)
    {
        var token = _lifetime?.Token ?? CancellationToken.None;
        return _operation = ExecuteAsync(async () =>
        {
            try { await operation(token); token.ThrowIfCancellationRequested(); OnPropertyChanged(string.Empty); NotifyActions(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
    }

    private void ApplyPorts(ProcessPortSnapshot snapshot)
    { _ports = snapshot; Ports.Clear(); foreach (var port in snapshot.Ports) Ports.Add(new(port)); OnPropertyChanged(nameof(PortsSummary)); }
    private void ApplyNetwork(ProcessNetworkSnapshot? snapshot)
    {
        _network = snapshot; _capturing = snapshot?.State == ProcessNetworkState.Running;
        Hosts.Clear(); if (snapshot != null) foreach (var host in snapshot.Hosts) Hosts.Add(new(host));
        OnPropertyChanged(nameof(NetworkSummary)); OnPropertyChanged(nameof(NetworkTotals)); NotifyActions();
    }

    partial void OnSearchTextChanged(string value) => FilterRows();
    partial void OnSortModeChanged(int value)
    { _sortDescending = value is 1 or 2; RefreshSort(); }
    partial void OnSelectedRowChanged(ProcessDisplayRow? value) { OnPropertyChanged(string.Empty); NotifyActions(); }

    public void ToggleExpanded(ProcessDisplayRow row)
    { if (!_collapsed.Add(row.Identity)) _collapsed.Remove(row.Identity); FilterRows(); }

    /// <summary>Column headers share the sort selector's state; repeated activation reverses the direction.</summary>
    [RelayCommand]
    private void SortByColumn(string column)
    {
        var mode = column switch { "Cpu" => 1, "Memory" => 2, "Name" => 3, "Pid" => 4,
            _ => throw new ArgumentException("Unknown process sort column.", nameof(column)) };
        if (SortMode != mode) SortMode = mode;
        else { _sortDescending = !_sortDescending; RefreshSort(); }
    }

    private string SortIndicator(int mode) => SortMode == mode ? _sortDescending ? " ↓" : " ↑" : "";

    /// <summary>Publishes ordering and header indicators together for both sorting entry points.</summary>
    private void RefreshSort()
    {
        FilterRows();
        OnPropertyChanged(nameof(SortDescending)); OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(PidHeader)); OnPropertyChanged(nameof(CpuHeader)); OnPropertyChanged(nameof(MemoryHeader));
    }

    /// <summary>Unavailable counters stay last in either direction instead of masquerading as zero.</summary>
    private IOrderedEnumerable<ProcessDisplayRow> OrderMeasured<T>(IEnumerable<ProcessDisplayRow> rows,
        Func<ProcessDisplayRow, T?> value) where T : struct, IComparable<T>
    {
        var knownFirst = rows.OrderBy(row => !value(row).HasValue);
        return _sortDescending ? knownFirst.ThenByDescending(value) : knownFirst.ThenBy(value);
    }

    private void FilterRows()
    {
        var matches = _all.Values.Where(row => string.IsNullOrWhiteSpace(SearchText) ||
            row.SearchContent.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase)).Select(row => row.Identity).ToHashSet();
        var ordered = new List<ProcessDisplayRow>();
        if (SortMode == 0)
        {
            foreach (var identity in matches.ToArray())
            { var cursor = identity; while (_parents.TryGetValue(cursor, out var parent)) { if (!matches.Add(parent)) break; cursor = parent; } }
            var children = _all.Values.Where(row => _parents.ContainsKey(row.Identity)).GroupBy(row => _parents[row.Identity]).ToDictionary(group => group.Key, group => group.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
            var pending = new Stack<(ProcessDisplayRow Row, int Depth)>();
            foreach (var row in _all.Values.Where(row => !_parents.ContainsKey(row.Identity)).OrderByDescending(row => row.Name, StringComparer.CurrentCultureIgnoreCase)) pending.Push((row, 0));
            while (pending.TryPop(out var item))
            {
                var (row, depth) = item;
                if (!matches.Contains(row.Identity)) continue;
                row.SetTree(depth, children.ContainsKey(row.Identity), !_collapsed.Contains(row.Identity)); ordered.Add(row);
                if (!string.IsNullOrWhiteSpace(SearchText) || !_collapsed.Contains(row.Identity))
                    if (children.TryGetValue(row.Identity, out var nested)) for (var index = nested.Length - 1; index >= 0; index--) pending.Push((nested[index], depth + 1));
            }
        }
        else
        {
            IEnumerable<ProcessDisplayRow> items = _all.Values.Where(row => matches.Contains(row.Identity));
            var sorted = SortMode switch
            {
                1 => OrderMeasured(items, row => row.Metric.CpuPercent),
                2 => OrderMeasured(items, row => row.Metric.Reading.WorkingSetBytes),
                4 => _sortDescending ? items.OrderByDescending(row => row.Identity.Pid) : items.OrderBy(row => row.Identity.Pid),
                _ => _sortDescending ? items.OrderByDescending(row => row.Name, StringComparer.CurrentCultureIgnoreCase) : items.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            };
            items = sorted.ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Identity.Pid).ThenBy(row => row.Identity.StartedAtUtcTicks);
            foreach (var row in items) { row.SetTree(0, false, true); ordered.Add(row); }
        }
        var visible = ordered.ToHashSet();
        for (var index = Rows.Count - 1; index >= 0; index--) if (!visible.Contains(Rows[index])) Rows.RemoveAt(index);
        for (var index = 0; index < ordered.Count; index++)
        { var current = Rows.IndexOf(ordered[index]); if (current < 0) Rows.Insert(index, ordered[index]); else if (current != index) Rows.Move(current, index); }
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(IsEmpty));
    }

    protected override void NotifyActions()
    {
        RefreshCommand.NotifyCanExecuteChanged(); RefreshDetailsCommand.NotifyCanExecuteChanged(); LoadFilesCommand.NotifyCanExecuteChanged();
        StartNetworkCommand.NotifyCanExecuteChanged(); StopNetworkCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanInspect)); OnPropertyChanged(nameof(CanLoadFiles)); OnPropertyChanged(nameof(CanStartNetwork)); OnPropertyChanged(nameof(CanStopNetwork));
    }
}

/// <summary>Updates measurements in place while preserving the process identity used by selection and the tree.</summary>
public sealed class ProcessDisplayRow(ProcessMetric metric) : ObservableObject
{
    public ProcessMetric Metric { get; private set; } = metric;
    public ProcessIdentity Identity => Metric.Reading.Identity;
    public string Name => Metric.Reading.Name;
    public string SearchContent => $"{Name}\n{Identity.Pid}\n{Metric.Reading.ImagePath}";
    public string Cpu => Metric.CpuPercent?.ToString("N1") + (Metric.CpuPercent.HasValue ? " %" : "—");
    public double CpuBar => Metric.CpuPercent ?? 0;
    public bool HasCpu => Metric.CpuPercent.HasValue;
    public string Memory => Metric.Reading.WorkingSetBytes is { } value ? Bytes(value) : "—";
    public string PrivateMemory => Metric.Reading.PrivateBytes is { } value ? Bytes(value) : "—";
    public string ReadRate => Metric.ReadBytesPerSecond is { } value ? Bytes((decimal)value) + "/s" : "—";
    public string WriteRate => Metric.WriteBytesPerSecond is { } value ? Bytes((decimal)value) + "/s" : "—";
    public Thickness Indent { get; private set; }
    public bool HasChildren { get; private set; }
    public string Expander { get; private set; } = "\uE70D";
    public void Apply(ProcessMetric value) { Metric = value; OnPropertyChanged(string.Empty); }
    public void SetTree(int depth, bool children, bool expanded)
    { Indent = new(Math.Min(depth, 6) * 12, 0, 0, 0); HasChildren = children; Expander = expanded ? "\uE70D" : "\uE76C"; OnPropertyChanged(string.Empty); }
    public static string Bytes(decimal value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"]; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "N1") + " " + units[unit];
    }
}
public sealed record ProcessPortDisplay(ProcessPort Port)
{
    private static string Endpoint(string address, int port) => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
    public string Title => $"{Port.Protocol} · {Endpoint(Port.LocalAddress, Port.LocalPort)}";
    public string Details => Port.RemotePort is > 0 ? $"→ {Endpoint(Port.RemoteAddress, Port.RemotePort.Value)} · {Port.State}" : Port.State;
}
public sealed record ProcessIpDisplay(ProcessIpTraffic Host)
{
    public string Title => $"{Host.RemoteAddress} · {Host.Protocol}";
    public string Totals => $"↑ {ProcessDisplayRow.Bytes(Host.SentBytes)} · ↓ {ProcessDisplayRow.Bytes(Host.ReceivedBytes)} · {Host.Events} events";
}
