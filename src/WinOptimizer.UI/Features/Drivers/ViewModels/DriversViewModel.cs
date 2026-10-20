using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Drivers.ViewModels;

/// <summary>Coordinates scans, details and confirmed removal of selected review candidates.</summary>
public partial class DriversViewModel : ObservableObject, IDisposable
{
    private readonly IDriverScanService _scanner;
    private readonly ILocalizationService _localization;
    private readonly ITaskProgressService _tasks;
    private readonly IDispatcherService _dispatcher;
    private readonly IDriverRemovalService _removal;
    private readonly IDialogService _dialogs;
    private readonly IApplicationModeService _mode;
    private CancellationTokenSource? _operation;
    private DriverScanReport? _report;
    private int _selectedCount;
    private bool _disposed;
    public DriverSettingsViewModel Settings { get; }
    public ObservableCollection<SelectableResultGroup> ResultGroups { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowEmptyState), nameof(ShowScanInformation), nameof(HasStatus))]
    public partial bool IsBusy { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] public partial string Status { get; private set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNotices))] public partial string Notices { get; private set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyCategory), nameof(CategoryEmptyTitle), nameof(CategoryEmptyDescription))]
    public partial SelectableResultGroup? SelectedGroup { get; set; }
    public bool IsIdle => !IsBusy;
    public bool HasReport => _report is not null;
    public bool ShowEmptyState => !HasReport && IsIdle;
    public bool HasStatus => IsIdle && !string.IsNullOrWhiteSpace(Status);
    public bool HasNotices => !string.IsNullOrWhiteSpace(Notices);
    /// <summary>Shows scan guidance only while waiting to start a scan without results.</summary>
    public bool ShowScanInformation => ShowEmptyState;
    public bool HasEmptyCategory => HasReport && SelectedGroup is { Items.Count: 0 };
    private bool UpdatesUnavailable => _report is { UpdatesChecked: false } && SelectedGroup == ResultGroups.LastOrDefault();
    public string CategoryEmptyTitle => Text(UpdatesUnavailable ? "Drivers_UpdatesUnavailableTitle" : "Drivers_CategoryEmpty");
    public string CategoryEmptyDescription => Text(UpdatesUnavailable ? "Drivers_UpdatesUnavailable" : "Drivers_CategoryEmptyDescription");
    public bool HasRescanHint => _report is not null && _report.Options != Settings.Options;
    public string Heading => Text("Nav_Drivers");
    public string IntroDescription => Text("Drivers_IntroDescription");
    public string EmptyTitle => Text("Drivers_ReadyTitle");
    public string EmptyDescription => Text("Drivers_ReadyDescription");
    public string Description => Text("Drivers_Description");
    public string ScanLabel => Text("Cleaner_Scan");
    public string CancelLabel => Text("Button_Cancel");
    public string InformationLabel => Text("Dialog_Information");
    public string FilterSummary => Text(Settings.Options.OnlyWhqlUpdates ? "Drivers_FilterWhql" : "Drivers_FilterAll");
    public string Summary => _report is null ? "" : string.Format(Text("Drivers_Summary"),
        _report.UnreferencedPackages.Count, _report.DisconnectedDevices.Count, _report.Updates.Count);
    public string RescanHint => HasRescanHint ? Text("Drivers_Rescan") : "";
    public string NameLabel => Text("Drivers_Name");
    public string DateLabel => Text("Drivers_Date");
    public string SizeLabel => Text("Drivers_Size");
    public string ResultsLabel => Text("Drivers_Results");
    public string UnusedPackagesLabel => Text("Drivers_Unreferenced");
    public string DisconnectedDevicesLabel => Text("Drivers_Disconnected");
    public string OfficialUpdatesLabel => Text("Drivers_Updates");
    public string SelectionHint => Text("Drivers_SelectionHint");
    public string SelectAllLabel => Text("Drivers_SelectCategory");
    public string UninstallLabel => Text("Drivers_Uninstall");
    public int SelectedCount => _selectedCount;
    public string SelectionSummary => string.Format(Text("Drivers_Selected"), SelectedCount);
    public bool CanUninstall => !_disposed && !IsBusy && SelectedCount > 0 && _mode.CurrentMode != WinOptimizerMode.Builder;
    public string TestModeHint => _mode.CurrentMode == WinOptimizerMode.Builder ? Text("Drivers_TestMode") : "";

    public DriversViewModel(IDriverScanService scanner, DriverSettingsViewModel settings, ILocalizationService localization,
        ITaskProgressService tasks, IDispatcherService dispatcher, IDriverRemovalService removal,
        IDialogService dialogs, IApplicationModeService mode)
    {
        _scanner = scanner; Settings = settings; _localization = localization; _tasks = tasks; _dispatcher = dispatcher;
        _removal = removal; _dialogs = dialogs; _mode = mode;
        _localization.LanguageChanged += OnLanguageChanged;
        Settings.PropertyChanged += OnSettingsChanged;
        _mode.ModeChanged += OnModeChanged;
    }

    public async Task InitializeAsync()
    {
        if (!await Settings.LoadAsync() && !_disposed) Status = Settings.Status;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_disposed || IsBusy) return;
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        IsBusy = true;
        CancellationTokenSource? owner = null;
        try
        {
            if (!await Settings.LoadAsync()) { Status = Settings.Status; return; }
            if (!await Settings.FlushAsync()) { Status = Settings.Status; return; }
            if (_disposed) return;
            if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
            ClearResults();
            _report = null; Notices = ""; NotifyReport();
            owner = _tasks.StartTask(Heading, isIndeterminate: true);
            _operation = owner;
            Status = Text("Drivers_Inventory");
            var report = await _scanner.ScanAsync(Settings.Options, CreateProgress(owner), owner.Token);
            owner.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            _report = report;
            PublishRows(report);
            Notices = string.Join(Environment.NewLine, report.Notices);
            Status = "";
            NotifyReport();
        }
        catch (OperationCanceledException) { if (!_disposed) Status = Text("Cleaner_Cancelled"); }
        catch (Exception ex) when (ex is DriverScanException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ReportFailure(ex.Message, owner); }
        finally
        {
            if (owner is not null)
            {
                _operation = null;
                if (ReferenceEquals(_tasks.CurrentTaskCancellationSource, owner)) _tasks.CompleteTask();
            }
            IsBusy = false;
        }
    }

    private string UpdateDetail(DriverUpdateMatch match)
    {
        var update = match.Update;
        var installed = string.Join(", ", match.Devices.Select(device => device.Version).Where(version => version.Length > 0).Distinct());
        return string.Format(Text("Drivers_UpdateDetail"), update.Title, update.Provider,
            installed.Length > 0 ? installed : Text("Drivers_Unknown"),
            update.Version.Length > 0 ? update.Version : Text("Drivers_Unknown"), update.Date,
            Text(update.Certification == DriverCertification.Whql ? "Drivers_WhqlVerified" : "Drivers_WhqlUnknown"));
    }

    /// <summary>Formats the original scan records only at the presentation boundary.</summary>
    private void PublishRows(DriverScanReport report)
    {
        var packages = report.Inventory.Packages.ToDictionary(package => package.PublishedName, StringComparer.OrdinalIgnoreCase);
        AddGroup("Drivers_Unreferenced", report.UnreferencedPackages.Select(package => new DriverResultItem(
            package.OriginalName, package.Date, package.SizeBytes, () =>
                $"{Text("SystemInfo_Field_InfName")}: {package.PublishedName}\n{Text("SystemInfo_Field_Provider")}: {package.Provider}\n{Text("SystemInfo_Field_DeviceClass")}: {package.ClassName}\n{Text("SystemInfo_Field_DriverVersion")}: {package.Version}",
            new(DriverRemovalKind.Package, package.PublishedName), _localization, _dialogs)));
        AddGroup("Drivers_Disconnected", report.DisconnectedDevices.Select(device =>
        {
            packages.TryGetValue(device.InfName, out var package);
            return new DriverResultItem(device.Name, device.Date, package?.SizeBytes, () =>
                $"{Text("SystemInfo_Field_Provider")}: {package?.Provider ?? Text("Drivers_Unknown")}\n{Text("SystemInfo_Field_DeviceClass")}: {device.ClassName}\n{Text("SystemInfo_Field_DriverVersion")}: {device.Version}\n{Text("SystemInfo_Field_InfName")}: {device.InfName}\n{Text("SystemInfo_Field_DeviceId")}: {device.InstanceId}\n{Text("Drivers_HardwareIds")}: {string.Join(", ", device.HardwareIds)}",
                new(DriverRemovalKind.Device, device.InstanceId), _localization, _dialogs);
        }));
        AddGroup("Drivers_Updates", report.Updates.Select(match => new DriverResultItem(match.Update.Title,
            match.Update.Date, match.Update.SizeBytes, () => $"{UpdateDetail(match)}\n{Text("Drivers_HardwareIds")}: {match.Update.HardwareId}\nID: {match.Update.Id}",
            null, _localization, _dialogs)));
        SelectedGroup = ResultGroups.FirstOrDefault(group => group.Items.Count > 0) ?? ResultGroups.FirstOrDefault();
        NotifySelection();
    }

    private void AddGroup(string key, IEnumerable<DriverResultItem> rows)
    {
        var items = rows.ToArray();
        foreach (var row in items) row.PropertyChanged += OnSelectionChanged;
        var icon = key switch
        {
            "Drivers_Disconnected" => FluentIcons.Common.Icon.PlugConnected,
            "Drivers_Updates" => FluentIcons.Common.Icon.ArrowSync,
            _ => FluentIcons.Common.Icon.HardDrive
        };
        ResultGroups.Add(new(key, items, "", icon, _localization));
    }
    private void ClearResults()
    {
        SelectedGroup = null;
        foreach (var group in ResultGroups)
        {
            foreach (var row in group.Items) row.PropertyChanged -= OnSelectionChanged;
            group.Dispose();
        }
        ResultGroups.Clear();
        _selectedCount = 0;
        NotifySelection();
    }

    /// <summary>Confirms the exact batch, retains successful changes on cancellation and keeps failed rows selected.</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (!CanUninstall || _report is null) return;
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        var selected = ResultGroups.SelectMany(group => group.Items).OfType<DriverResultItem>()
            .Where(row => row.IsSelected && row.Target is not null).ToArray();
        var report = _report;
        IsBusy = true;
        CancellationTokenSource? owner = null;
        using var lifetime = new CancellationTokenSource();
        _operation = lifetime;
        try
        {
            using var reservation = _mode.BeginSystemOperation();
            var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
            {
                Title = UninstallLabel, Message = Text("Drivers_UninstallConfirm"),
                Items = selected.Select(row => $"{row.Name} ({row.Target!.Id})").ToArray(),
                ConfirmButtonText = UninstallLabel, CancelButtonText = CancelLabel
            });
            if (!response.Confirmed || _disposed || lifetime.IsCancellationRequested) return;
            if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
            owner = _tasks.StartTask(UninstallLabel, isIndeterminate: true);
            _operation = owner;
            Status = Text("Drivers_Removing");
            var result = await _removal.RemoveAsync(report, selected.Select(row => row.Target).OfType<DriverRemovalTarget>().ToArray(), CreateProgress(owner), owner.Token);
            if (_disposed) return;
            var removed = result.Outcomes.Where(outcome => outcome.Success).Select(outcome => outcome.Target).ToHashSet();
            var categoryIndex = SelectedGroup is { } category ? ResultGroups.IndexOf(category) : 0;
            _report = report with
            {
                UnreferencedPackages = report.UnreferencedPackages.Where(package => !removed.Contains(new(DriverRemovalKind.Package, package.PublishedName))).ToArray(),
                DisconnectedDevices = report.DisconnectedDevices.Where(device => !removed.Contains(new(DriverRemovalKind.Device, device.InstanceId))).ToArray()
            };
            ClearResults();
            PublishRows(_report);
            SelectedGroup = ResultGroups[Math.Clamp(categoryIndex, 0, ResultGroups.Count - 1)];
            var remaining = selected.Select(row => row.Target).Where(target => target is not null && !removed.Contains(target)).ToHashSet();
            for (var index = 0; index < ResultGroups.Count; index++)
            {
                foreach (var row in ResultGroups[index].Items.OfType<DriverResultItem>()) row.IsSelected = row.Target is not null && remaining.Contains(row.Target);
            }
            var failures = result.Outcomes.Where(outcome => !outcome.Success).ToArray();
            Notices = string.Join(Environment.NewLine, report.Notices.Concat(failures.Select(outcome => $"{outcome.Target.Id}: {outcome.Error}")));
            Status = string.Format(Text("Drivers_RemovalComplete"), removed.Count, failures.Length);
            if (result.Cancelled) Status += " " + Text("Drivers_RemovalCancelled");
            if (result.Outcomes.Any(outcome => outcome.RestartRequired)) Status += " " + Text("Drivers_RestartRequired");
            NotifyReport();
        }
        catch (OperationCanceledException) { if (!_disposed) Status = Text("Drivers_RemovalCancelled"); }
        catch (Exception ex) when (ex is DriverScanException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ReportFailure(ex.Message, owner); }
        finally
        {
            _operation = null;
            if (owner is not null && ReferenceEquals(_tasks.CurrentTaskCancellationSource, owner)) _tasks.CompleteTask();
            IsBusy = false;
        }
    }

    /// <summary>Retains the page error and marks only this operation's footer task as failed.</summary>
    private void ReportFailure(string message, CancellationTokenSource? owner)
    {
        if (_disposed) return;
        Status = message;
        if (owner is not null && ReferenceEquals(_tasks.CurrentTaskCancellationSource, owner))
            _tasks.UpdateDetailedProgress(new TaskProgressDetail
            {
                LogLevel = LogLevel.Error, StatusText = Heading, TerminalOutput = message
            });
    }

    /// <summary>Maintains the summary in constant time, including bulk group selections.</summary>
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DriverResultItem.IsSelected) || sender is not DriverResultItem row) return;
        _selectedCount += row.IsSelected ? 1 : -1;
        NotifySelection();
    }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(CanUninstall));
        UninstallCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    {
        NotifySelection();
    }

    /// <summary>Publishes measured stage progress to the shared footer for the active operation.</summary>
    private IProgress<DriverScanProgress> CreateProgress(CancellationTokenSource owner) => new ScanProgress(value =>
        _dispatcher.RunOnUIThread(() =>
        {
            if (_disposed || _operation != owner || owner.IsCancellationRequested) return;
            double? progress = value.Total > 0 ? Math.Clamp(value.Completed * 100d / value.Total, 0, 100) : null;
            var status = Text(value.Stage) + (value.Total > 0 ? $" ({value.Completed}/{value.Total})" : "");
            _tasks.UpdateDetailedProgress(new TaskProgressDetail
            {
                Progress = progress, IsIndeterminate = progress is null, StatusText = Heading,
                TerminalOutput = status, IsProgressIndicator = true
            });
        }));
    private void OnModeChanged(object? sender, EventArgs e)
    { NotifySelection(); OnPropertyChanged(nameof(TestModeHint)); }
    /// <summary>Publishes presentation derived from the current report without adding scan state.</summary>
    private void NotifyReport()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RescanHint));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasRescanHint));
        OnPropertyChanged(nameof(ShowScanInformation));
        OnPropertyChanged(nameof(HasEmptyCategory));
        OnPropertyChanged(nameof(CategoryEmptyTitle));
        OnPropertyChanged(nameof(CategoryEmptyDescription));
    }
    private string Text(string key) => _localization.GetString(key);
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.Options))
        {
            OnPropertyChanged(nameof(FilterSummary));
            OnPropertyChanged(nameof(RescanHint));
            OnPropertyChanged(nameof(HasRescanHint));
        }
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
        foreach (var group in ResultGroups) { group.RefreshText(); foreach (var row in group.Items.OfType<DriverResultItem>()) row.RefreshText(); }
    }
    public void Dispose()
    {
        _disposed = true; _operation?.Cancel(); ClearResults();
        _localization.LanguageChanged -= OnLanguageChanged; Settings.PropertyChanged -= OnSettingsChanged;
        _mode.ModeChanged -= OnModeChanged;
    }
    private sealed class ScanProgress(Action<DriverScanProgress> callback) : IProgress<DriverScanProgress>
    { public void Report(DriverScanProgress value) => callback(value); }
}
