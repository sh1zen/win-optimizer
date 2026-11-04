using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Cleaner.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Cleaner.ViewModels;

/// <summary>Shared category icons and readable byte sizes for the Cleaner presentation.</summary>
internal static class CleanerVisuals
{
    public static FluentIcons.Common.Icon IconFor(CleanerArea area) => area switch
    {
        CleanerArea.Privacy => FluentIcons.Common.Icon.ShieldLock,
        CleanerArea.JunkFiles => FluentIcons.Common.Icon.Delete,
        CleanerArea.Shortcuts => FluentIcons.Common.Icon.Link,
        CleanerArea.RegistryDefrag => FluentIcons.Common.Icon.Database,
        CleanerArea.Registry => FluentIcons.Common.Icon.Storage,
        CleanerArea.Disk => FluentIcons.Common.Icon.HardDrive,
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824d:N1} GB",
        >= 1048576 => $"{bytes / 1048576d:N1} MB",
        >= 1024 => $"{bytes / 1024d:N1} KB",
        _ => $"{bytes:N0} B"
    };
}

/// <summary>Selectable scan result; diagnostic entries cannot be applied.</summary>
public sealed class CleanerResultItem(CleanerFinding finding, ILocalizationService localization) : ObservableObject, ISelectableResultItem
{
    public bool ShowSelection => true;
    public int DetailMaxLines => Finding.Area == CleanerArea.Disk ? 0 : Finding.UnavailableReason is null ? 1 : 2;
    public CleanerFinding Finding { get; } = finding;
    public ResultReport? Report { get; } = finding is { Area: CleanerArea.Disk, Target: CleanerDiskTarget disk }
        ? CleanerDiskReport.Parse(disk.Root, finding.Detail, finding.UnavailableReason) : null;
    public bool CanClean => Finding.CanClean;
    public string Title => localization.GetString(Finding.LabelKey);
    /// <summary>Shows the disk identity and full native analysis without leading blank report lines.</summary>
    public string Detail
    {
        get
        {
            var detail = Finding.Area == CleanerArea.Disk && Finding.Target is CleanerDiskTarget disk
                ? $"{disk.Root}{Environment.NewLine}{Finding.Detail.Trim()}".TrimEnd()
                : Finding.Detail;
            return Finding.UnavailableReason is { } reason ? $"{detail}{Environment.NewLine}{reason}" : detail;
        }
    }
    public string Size => Finding.Bytes > 0 ? CleanerVisuals.FormatSize(Finding.Bytes) : "";
    public FluentIcons.Common.Icon Icon => CleanerVisuals.IconFor(Finding.Area);
    private bool _isSelected = finding.CanClean && finding.Area != CleanerArea.Registry;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, CanClean && value); }
    public void RefreshText() => OnPropertyChanged(nameof(Title));
}

/// <summary>Coordinates explicit scanning, review and confirmed cleanup with global task cancellation.</summary>
public partial class CleanerViewModel : ObservableObject, IDisposable
{
    private readonly ICleanerService _cleaner;
    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogs;
    private readonly ITaskProgressService _tasks;
    private readonly ILogService _log;
    private readonly IUserPreferencesService _preferences;
    private Task? _areaSelectionLoadTask;
    private Task _areaSelectionSaveTask = Task.CompletedTask;
    private bool _areaSelectionLoaded;
    private bool _restoringAreaSelection;
    private bool _areaSelectionEdited;
    private const string AreaSelectionPreference = "Cleaner.SelectedAreas";
    private CancellationTokenSource? _operationCts;
    private CleanerOptions? _scannedOptions;
    private int _selectedCount;
    private long _selectedBytes;
    private readonly Dictionary<CleanerArea, CleanerCategoryReport> _reportsByArea = [];
    private string? _applyFailure;
    private bool _reportCancelled;
    private int _reportErrorCount;
    private enum RunResult { Completed, Cancelled, Failed }
    public CleanerSettingsViewModel Settings { get; }
    public ObservableCollection<CleanerChoice<CleanerArea>> Areas { get; } = [];
    public ObservableCollection<CleanerResultItem> Results { get; } = [];
    public ObservableCollection<SelectableResultGroup> ResultGroups { get; } = [];
    public ObservableCollection<CleanerCategoryReport> CategoryReports { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCategoryGroups), nameof(HasCategoryItems), nameof(HasEmptyCategory))]
    public partial CleanerCategoryReport? SelectedCategory { get; set; }
    public IReadOnlyList<SelectableResultGroup> SelectedCategoryGroups => SelectedCategory?.ReviewGroups ?? [];
    public bool HasCategoryItems => SelectedCategory?.HasSelectableItems == true;
    public bool HasEmptyCategory => SelectedCategory is { HasSelectableItems: false };
    public bool ShowCategoryReview => HasScanned && !HasCleanupReport;
    public string CategoriesLabel => Text("SystemInfo_Categories");
    public string NoSelectableItemsLabel => Text("Cleaner_NoSelectableResults");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanChooseAreas), nameof(CanSelectResults), nameof(HasStatus), nameof(HasScanStatus), nameof(ReportState))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ApplyCommand))]
    public partial bool IsBusy { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanChooseAreas))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsLoadingSelection { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAreaSelectionError))]
    public partial string AreaSelectionError { get; private set; } = "";
    public bool HasAreaSelectionError => !string.IsNullOrEmpty(AreaSelectionError);
    public bool CanChooseAreas => IsIdle && !IsLoadingSelection;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus), nameof(HasScanStatus))] public partial string Status { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNotices), nameof(ReportDetails), nameof(HasReportDetails))] public partial string Notices { get; set; } = "";
    [ObservableProperty] public partial bool AreDetailsExpanded { get; set; }
    [ObservableProperty] public partial bool HasCleanupReport { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasScanStatus), nameof(HasEmptyResults), nameof(RequiresRescan), nameof(CanSelectResults))] public partial bool HasScanned { get; private set; }
    public bool IsIdle => !IsBusy;
    public bool HasStatus => IsIdle && !string.IsNullOrWhiteSpace(Status);
    public bool HasScanStatus => HasStatus && !HasScanned && !HasCleanupReport;
    public bool HasNotices => !string.IsNullOrWhiteSpace(Notices);
    public bool HasResults => Results.Count > 0;
    public bool HasEmptyResults => HasScanned && !HasResults;
    public bool RequiresRescan => HasScanned && !ReferenceEquals(_scannedOptions, Settings.Options);
    public bool CanSelectResults => IsIdle && HasScanned && !RequiresRescan;
    public string RescanDescription => Text("Cleaner_Rescan");
    public int ResultCount => Results.Count;
    public int SelectedCount => _selectedCount;
    public string SelectedSize => CleanerVisuals.FormatSize(_selectedBytes);
    public bool HasSummary => HasScanned || HasCleanupReport;
    public bool ShowReviewItems => HasResults && !HasCleanupReport;
    public int RemovedCount => CategoryReports.Sum(report => report.RemovedCount);
    public int ReportErrorCount => _reportErrorCount;
    public string ReportTitle => HasCleanupReport ? Text("Cleaner_ReportTitle") : ResultsLabel;
    public string ReportDescription => HasCleanupReport ? Text("Cleaner_ReportNote") : ReviewDescription;
    public string OutcomeLabel => HasCleanupReport ? Text("Cleaner_ReportRemoved") : SelectedLabel;
    public int OutcomeCount => HasCleanupReport ? RemovedCount : SelectedCount;
    public string IssueLabel => Text(HasCleanupReport ? "Cleaner_ReportErrors" : "Cleaner_ReportProtected");
    public int IssueCount => HasCleanupReport ? ReportErrorCount : Results.Count(item => !item.CanClean);
    public string ReportSpace => HasCleanupReport
        ? CategoryReports.Any(report => report.HasFileSelection)
            ? CleanerVisuals.FormatSize(CategoryReports.Sum(report => report.EstimatedFreedFileBytes)) : "—"
        : SelectedSize;
    public string ReportState => !HasCleanupReport || IsBusy ? "" : _reportCancelled ? Text("Cleaner_ReportCancelled")
        : _reportErrorCount > 0 ? $"{_reportErrorCount:N0} {Text("Cleaner_ReportErrors")}" : Text("Logs_Level_Success");
    public string ReportDetails => string.Join(Environment.NewLine, new[] { Notices, _applyFailure }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public bool HasReportDetails => HasNotices || !string.IsNullOrWhiteSpace(_applyFailure);
    public string CategorySummary => string.Format(Text("Cleaner_CategorySummary"), Areas.Count(x => x.IsSelected), Areas.Count);
    public string ReviewDescription => Text("Cleaner_ReviewDescription");
    public string DetailsLabel => Text("Logs_Details");
    public string ReviewLabel => Text("Cleaner_ReviewResults");
    public string AreasLabel => Text("Cleaner_Areas");
    public string FoundLabel => Text("Cleaner_Found");
    public string SelectedLabel => Text("Cleaner_Selected");
    public string SpaceLabel => Text(HasCleanupReport ? "Cleaner_ReportFreed" : "Cleaner_SelectedSpace");
    public string IntroTitle => Text("Cleaner_IntroTitle");
    public string IntroDescription => Text("Cleaner_IntroDescription");
    public string ScanStep => Text("Cleaner_StepScan");
    public string Title => Text("Nav_Cleaner");
    public string Description => Text("Cleaner_Description");
    public string EventsCleanupDisplayName => Text("EventsCleanup_Title");
    public string EventsCleanupDescription => Text("EventsCleanup_TitleDescription");
    public string ScanLabel => Text("Cleaner_Scan");
    public string ApplyLabel => Text("Cleaner_Apply");
    public string SelectAllLabel => Text("Cleaner_SelectAll");
    public string ClearLabel => Text("Cleaner_ClearSelection");
    public string ResultsLabel => Text("Cleaner_Results");
    public string CancelLabel => Text("Button_Cancel");
    private string Text(string key) => _localization.GetString(key);

    public CleanerViewModel(ICleanerService cleaner, CleanerSettingsViewModel settings, ILocalizationService localization,
        IDialogService dialogs, ITaskProgressService tasks, ILogService log,
        IApplicationModeService applicationModeService, IUserPreferencesService preferences)
    {
        _cleaner = cleaner; Settings = settings; _localization = localization; _dialogs = dialogs; _tasks = tasks; _log = log;
        _preferences = preferences;
        _applicationModeService = applicationModeService;
        _applicationModeService.ModeChanged += OnApplicationModeChanged;
        foreach (var area in Enum.GetValues<CleanerArea>())
        {
            var choice = new CleanerChoice<CleanerArea>(area, $"Cleaner_{area}", localization, descriptionKey: $"Cleaner_{area}_Description")
            { IsSelected = area is not (CleanerArea.Disk or CleanerArea.RegistryDefrag), Icon = CleanerVisuals.IconFor(area) };
            choice.PropertyChanged += OnSelectionChanged;
            Areas.Add(choice);
        }
        Settings.PropertyChanged += OnSettingsChanged;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Restores the scan selection once before loading Cleaner settings and Windows choices.</summary>
    public async Task InitializeAsync()
    {
        await (_areaSelectionLoadTask ??= LoadAreaSelectionAsync());
        await Settings.LoadAsync();
    }

    /// <summary>Loads validated area identifiers without writing them back or overwriting edits made during loading.</summary>
    private async Task LoadAreaSelectionAsync()
    {
        IsLoadingSelection = true;
        try
        {
            var selected = await _preferences.GetPreferenceAsync(AreaSelectionPreference, Areas.Where(x => x.IsSelected).Select(x => x.Value).ToArray());
            if (!_areaSelectionEdited)
            {
                if (selected is null || selected.Any(area => !Enum.IsDefined(area)))
                    throw new ArgumentException(Text("Cleaner_InvalidSelection"));
                _restoringAreaSelection = true;
                foreach (var area in Areas) area.IsSelected = selected.Contains(area.Value);
            }
            _areaSelectionLoaded = true;
            AreaSelectionError = "";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.LogError($"Cleaner scan selection load: {ex.Message}", ex);
            AreaSelectionError = string.Format(Text("Cleaner_SelectionLoadFailed"), ex.Message);
        }
        finally
        {
            _restoringAreaSelection = false;
            IsLoadingSelection = false;
            ScanCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Serializes selection snapshots so rapid edits cannot persist an older selection last.</summary>
    private async Task SaveAreaSelectionAsync(Task precedingSave, CleanerArea[] selected)
    {
        await precedingSave;
        try
        {
            var saved = await _preferences.SetPreferenceAsync(AreaSelectionPreference, selected);
            if (!saved.Success) throw new IOException(saved.ErrorMessage ?? "Unable to save Cleaner scan selection.");
            AreaSelectionError = "";
        }
        catch (Exception ex)
        {
            _log.LogError($"Cleaner scan selection save: {ex.Message}", ex);
            AreaSelectionError = string.Format(Text("Cleaner_SelectionSaveFailed"), ex.Message);
        }
    }

    /// <summary>Waits for pending preference writes before the application shuts down.</summary>
    public async Task FlushAreaSelectionAsync()
    {
        Task pending;
        do
        {
            pending = _areaSelectionSaveTask;
            await pending;
        } while (!ReferenceEquals(pending, _areaSelectionSaveTask));
    }

    private bool CanScan() => CanChooseAreas && (_areaSelectionLoaded || _areaSelectionEdited) && Areas.Any(x => x.IsSelected);
    private readonly IApplicationModeService _applicationModeService;
    private bool IsTrialMode => _applicationModeService.CurrentMode == WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder;
    private void OnApplicationModeChanged(object? sender, EventArgs e)
    {
        ApplyCommand.NotifyCanExecuteChanged();
        if (IsTrialMode) Status = Text("TrialMode_Blocked");
    }
    private bool CanApply() => !IsTrialMode && !IsBusy && ReferenceEquals(_scannedOptions, Settings.Options) && _selectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        await InitializeAsync();
        if (!CanScan()) return;
        if (Settings.IsBusy) { Status = Text("Cleaner_Wait"); return; }
        if (!Settings.IsLoaded) { Status = Settings.Status; return; }
        if (!await Settings.FlushAsync()) { Status = Settings.Status; return; }
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        var options = Settings.Options;
        var selectedAreas = Areas.Where(area => area.IsSelected).Select(area => area.Value).ToArray();
        var completed = await RunAsync(async ct =>
        {
            ClearResults();
            HasScanned = false;
            Notices = "";
            _scannedOptions = null;
            var report = await _cleaner.ScanAsync(options, selectedAreas, CreateProgress(), ct);
            foreach (var finding in report.Findings)
            {
                ct.ThrowIfCancellationRequested();
                var item = new CleanerResultItem(finding, _localization);
                item.PropertyChanged += OnSelectionChanged;
                Results.Add(item);
                if (item.IsSelected) { _selectedCount++; _selectedBytes += item.Finding.Bytes; }
                if (Results.Count % 128 == 0) await Task.Yield();
            }
            ct.ThrowIfCancellationRequested();
            // Groups reference the same selectable items used by cleanup, preserving scan order.
            var groupsByArea = new Dictionary<CleanerArea, List<SelectableResultGroup>>();
            foreach (var group in Results.Where(item => item.CanClean).GroupBy(item => (item.Finding.Area, item.Finding.LabelKey)))
            {
                var bytes = group.Sum(item => item.Finding.Bytes);
                var resultGroup = new SelectableResultGroup(group.Key.LabelKey, group.ToArray(),
                    bytes > 0 ? CleanerVisuals.FormatSize(bytes) : "", CleanerVisuals.IconFor(group.Key.Area), _localization);
                ResultGroups.Add(resultGroup);
                if (!groupsByArea.TryGetValue(group.Key.Area, out var categoryGroups))
                    groupsByArea.Add(group.Key.Area, categoryGroups = []);
                categoryGroups.Add(resultGroup);
            }
            var itemsByArea = Results.GroupBy(item => item.Finding.Area).ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var area in selectedAreas)
            {
                var category = new CleanerCategoryReport(area, itemsByArea.GetValueOrDefault(area) ?? [],
                    groupsByArea.GetValueOrDefault(area)?.ToArray() ?? [], _localization);
                CategoryReports.Add(category);
                _reportsByArea.Add(area, category);
            }
            SelectedCategory = CategoryReports.FirstOrDefault(category => category.HasSelectableItems) ?? CategoryReports.FirstOrDefault();
            RefreshResultSummary();
            Notices = string.Join(Environment.NewLine, report.Notices);
            Status = string.Format(Text("Cleaner_ScanComplete"), Results.Count, CleanerVisuals.FormatSize(Results.Sum(x => x.Finding.Bytes)), report.CheckedItems);
        });
        // Publish review availability only after the entire operation has stopped successfully.
        if (completed == RunResult.Completed)
        {
            _scannedOptions = options;
            HasScanned = true;
            ApplyCommand.NotifyCanExecuteChanged();
        }
        RefreshReport();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (IsTrialMode) { Status = Text("TrialMode_Blocked"); return; }
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        if (!await Settings.FlushAsync()) { Status = Settings.Status; return; }
        if (!ReferenceEquals(_scannedOptions, Settings.Options)) { Status = Text("Cleaner_Rescan"); return; }
        var ids = Results.Where(x => x.IsSelected).Select(x => x.Finding.Id).ToArray();
        // Lock both commands while the confirmation dialog is open.
        IsBusy = true;
        try
        {
            var answer = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
            {
                Title = Title, Message = string.Format(Text("Cleaner_Confirm"), ids.Length) + Environment.NewLine +
                    Text(Settings.Options.BackupBeforeCleaning ? "Cleaner_BackupOn" : "Cleaner_BackupOff") + Environment.NewLine +
                    Text(Settings.Options.BackupBeforeRegistryDefrag ? "Cleaner_HiveBackupOn" : "Cleaner_HiveBackupOff"),
                ConfirmButtonText = ApplyLabel, CancelButtonText = CancelLabel
            });
            if (!answer.Confirmed) return;
            if (IsTrialMode) { Status = Text("TrialMode_Blocked"); return; }
            if (!ReferenceEquals(_scannedOptions, Settings.Options) || _tasks.IsTaskRunning) { Status = Text("Cleaner_Rescan"); return; }
        }
        catch (Exception ex)
        {
            _log.LogError($"Cleaner confirmation: {ex.Message}", ex);
            Status = ex.Message;
            return;
        }
        finally { IsBusy = false; }
        var confirmed = ids.ToHashSet();
        foreach (var category in CategoryReports) category.SnapshotSelection(confirmed);
        HasCleanupReport = true;
        AreDetailsExpanded = false;
        RefreshReport();
        var run = await RunAsync(async ct =>
        {
            var result = await _cleaner.ApplyAsync(ids, CreateProgress(), ct);
            var outcomes = result.Items.GroupBy(item => item.Finding.Area).ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var category in CategoryReports) category.SetOutcomes(outcomes.GetValueOrDefault(category.Area) ?? []);
            _reportErrorCount = result.Errors.Count;
            _reportCancelled = result.IsCancelled;
            Status = result.IsCancelled ? Text("Cleaner_Cancelled") : string.Format(Text("Cleaner_ApplyComplete"), result.Completed, result.Errors.Count);
            Notices = string.Join(Environment.NewLine, new[] { Notices }.Concat(result.Errors).Where(text => !string.IsNullOrWhiteSpace(text)));
            // Keep the final outcome in task details even when coalesced progress never reaches the footer.
            var output = result.Errors.Count > 0 ? $"{Status}{Environment.NewLine}{Notices}" : Status;
            _tasks.UpdateDetailedProgress(new TaskProgressDetail
            {
                StatusText = Title,
                TerminalOutput = output,
                DetailedMessage = output,
                LogLevel = result.Errors.Count > 0 ? WinOptimizer.Core.Features.Common.Enums.LogLevel.Error : WinOptimizer.Core.Features.Common.Enums.LogLevel.Info
            });
        });
        if (run == RunResult.Cancelled) _reportCancelled = true;
        if (run == RunResult.Failed) { _applyFailure = Status; _reportErrorCount++; }
        _scannedOptions = null;
        RefreshReviewState();
        RefreshReport();
    }

    private async Task<RunResult> RunAsync(Func<CancellationToken, Task> operation)
    {
        IsBusy = true;
        CancellationTokenSource? cts = null;
        try
        {
            cts = _tasks.StartTask(Title, isIndeterminate: true);
            _operationCts = cts;
            await operation(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            return RunResult.Completed;
        }
        catch (OperationCanceledException)
        {
            if (_scannedOptions is null) { ClearResults(); HasScanned = false; }
            Status = Text("Cleaner_Cancelled");
            return RunResult.Cancelled;
        }
        catch (Exception ex)
        {
            if (_scannedOptions is null) ClearResults();
            _log.LogError($"Cleaner: {ex.Message}", ex);
            Status = ex.Message;
            _tasks.UpdateDetailedProgress(new TaskProgressDetail
            {
                LogLevel = WinOptimizer.Core.Features.Common.Enums.LogLevel.Error,
                StatusText = Title, TerminalOutput = ex.Message
            });
            return RunResult.Failed;
        }
        finally
        {
            _operationCts = null;
            if (cts is not null) _tasks.CompleteTask();
            IsBusy = false;
        }
    }

    private IProgress<CleanerProgress> CreateProgress()
    {
        var owner = _operationCts;
        return new CleanerProgressReporter(value =>
        {
            // Ignore callbacks queued by an operation that has already stopped or been cancelled.
            if (!IsBusy || !ReferenceEquals(owner, _operationCts) || owner?.IsCancellationRequested != false) return;
            var message = value.Message.StartsWith("Cleaner_", StringComparison.Ordinal) ? Text(value.Message) : value.Message;
            var status = value.IsIndeterminate
                ? $"{message} — {Text("Cleaner_Discovering")}: {value.Completed:N0}"
                : string.Format(Text("Cleaner_MeasuredProgress"), message, value.Stage, value.Stages, value.Completed, value.Total, value.Percentage);
            _tasks.UpdateDetailedProgress(new TaskProgressDetail
            {
                Progress = value.Percentage, IsIndeterminate = value.IsIndeterminate,
                StatusText = message, TerminalOutput = status, IsProgressIndicator = true
            });
        });
    }

    [RelayCommand] private void SelectAll() { if (CanSelectResults) foreach (var result in Results) result.IsSelected = true; }
    [RelayCommand] private void ClearSelection() { if (CanSelectResults) foreach (var result in Results) result.IsSelected = false; }
    private void ClearResults()
    {
        foreach (var group in ResultGroups) group.Dispose();
        ResultGroups.Clear();
        foreach (var result in Results) result.PropertyChanged -= OnSelectionChanged;
        Results.Clear();
        CategoryReports.Clear();
        SelectedCategory = null;
        _reportsByArea.Clear();
        HasCleanupReport = false;
        AreDetailsExpanded = false;
        _applyFailure = null;
        _reportCancelled = false;
        _reportErrorCount = 0;
        _selectedCount = 0;
        _selectedBytes = 0;
        RefreshResultSummary();
        RefreshReport();
    }

    /// <summary>Publishes counters after a scan/reset; selection changes update their cached totals in constant time.</summary>
    private void RefreshResultSummary()
    {
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasEmptyResults));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedSize));
        OnPropertyChanged(nameof(ShowReviewItems));
        OnPropertyChanged(nameof(ShowCategoryReview));
    }
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CleanerResultItem.IsSelected)) return;
        if (sender is CleanerResultItem result)
        {
            _selectedCount += result.IsSelected ? 1 : -1;
            _selectedBytes += result.IsSelected ? result.Finding.Bytes : -result.Finding.Bytes;
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SelectedSize));
            OnPropertyChanged(nameof(OutcomeCount));
            OnPropertyChanged(nameof(ReportSpace));
            if (!HasCleanupReport && _reportsByArea.TryGetValue(result.Finding.Area, out var category)) category.SelectionChanged(result);
        }
        else
        {
            if (!_restoringAreaSelection)
            {
                _areaSelectionEdited = true;
                _areaSelectionSaveTask = SaveAreaSelectionAsync(_areaSelectionSaveTask, Areas.Where(x => x.IsSelected).Select(x => x.Value).ToArray());
            }
            ScanCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CategorySummary));
        }
        ApplyCommand.NotifyCanExecuteChanged();
    }
    /// <summary>Marks a consumed or changed scan read-only until a fresh scan supplies current evidence.</summary>
    private void RefreshReviewState()
    {
        OnPropertyChanged(nameof(RequiresRescan));
        OnPropertyChanged(nameof(CanSelectResults));
        ApplyCommand.NotifyCanExecuteChanged();
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(Settings.Options)) RefreshReviewState(); }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
        foreach (var area in Areas) area.RefreshText();
        foreach (var result in Results) result.RefreshText();
        foreach (var group in ResultGroups) group.RefreshText();
        foreach (var report in CategoryReports) report.RefreshText();
    }

    /// <summary>Publishes numerical scan/cleanup snapshots independently from technical diagnostic text.</summary>
    private void RefreshReport()
    {
        foreach (var property in new[] { nameof(HasCleanupReport), nameof(HasSummary), nameof(ShowReviewItems), nameof(ShowCategoryReview),
            nameof(RemovedCount), nameof(ReportErrorCount), nameof(ReportTitle), nameof(ReportDescription), nameof(OutcomeLabel),
            nameof(OutcomeCount), nameof(IssueLabel), nameof(IssueCount), nameof(ReportSpace), nameof(SpaceLabel), nameof(ReportState),
            nameof(ReportDetails), nameof(HasReportDetails), nameof(HasScanStatus) }) OnPropertyChanged(property);
    }
    public void Dispose()
    {
        _applicationModeService.ModeChanged -= OnApplicationModeChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        Settings.PropertyChanged -= OnSettingsChanged;
        foreach (var area in Areas) area.PropertyChanged -= OnSelectionChanged;
        ClearResults();
    }
}
