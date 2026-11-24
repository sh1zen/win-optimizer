using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Security;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Security;

/// <summary>Owns explicit security scanning, category review, selection and confirmed remediation.</summary>
public partial class SecurityViewModel : UtilityViewModel
{
    private readonly ISecurityScanService _service;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly ITaskProgressService _tasks;
    private SecurityScanReport? _report;
    private CancellationTokenSource? _activeTask;
    private SecurityFindingRow? _lastUnavailable;
    private readonly List<SecurityFindingRow> _allRows = [];
    protected override string TitleKey => "Security_Title";
    public ObservableCollection<SecurityCategoryRow> Categories { get; } = [];
    public ObservableCollection<SecurityFindingRow> Findings { get; } = [];
    public IReadOnlyList<SecurityCoverageItem> ScanCoverage { get; }
    [ObservableProperty] public partial SecurityCategoryRow? SelectedCategory { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIntroduction), nameof(ShowOtherProgress))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand), nameof(ShowScanDetailsCommand))]
    public partial bool IsScanning { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanProgressHint))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    public partial bool IsScanCancelling { get; private set; }
    public bool HasReport => _report != null;
    public string PageDescription => HasReport ? Text("Cleaner_Results") : Description;
    public bool ShowIntroduction => !HasReport && !IsScanning;
    public bool ShowOtherProgress => IsBusy && !IsScanning;
    public string ReadyTitle => Text("Scan_ReadyTitle");
    public string ScanLabel => Text("Security_Scan");
    public string ScanAgainLabel => Text("UninstallResidue_Rescan");
    public string DeepScanLabel => Text("Security_DeepScan");
    public string ScanInProgressLabel => Text("Security_ScanInProgress");
    public string ScanProgressTitle => Text("Security_ScanProgressTitle");
    public string ScanProgressHint => Text(IsScanCancelling ? "Progress_Cancelling" : "Security_ScanResultsHint");
    public string CancelScanLabel => Text("Security_CancelScan");
    public string ScanDetailsLabel => Text("Security_ViewScanDetails");
    public string ReviewStepLabel => Text("Security_ReviewStepLabel");
    public string ApplyStepLabel => Text("Security_ApplyStepLabel");
    public string ScanCoverageLabel => Text("Security_ScanCoverage");
    public string ThreatCoverageNote => Text("Security_ThreatCoverageNote");
    public string DeepScanOptionsLabel => Text("Security_DeepScanOptions");
    public string DeepScanOptionsDescription => Text("Security_DeepScanOptionsDescription");
    public IReadOnlyList<string> DeepCheckNames => SecurityCheck.All.Where(check => check.DeepOnly)
        .Select(check => Text("Security_Check_" + check.Id)).ToArray();
    public string FixLabel => Text("Security_Fix");
    public string NameLabel => Text("Security_Item");
    public string CategoriesLabel => Text("SystemInfo_Categories");
    public string EmptyCategoryLabel => Text("Security_NoCategoryChecks");
    public bool ShowEmptyCategory => HasReport && SelectedCategory is { Rows.Count: 0 };
    public string Intro => Text("Security_Intro");
    public string SelectAllLabel => Text("Security_SelectAll");
    public string Selection => string.Format(Text("Security_Selection"), _allRows.Count(row => row.IsSelected));
    public bool CanFix => CanWrite && _allRows.Any(row => row.IsSelected && row.CanSelect);
    public int ReviewCount => _allRows.Count(row => row.Observation.State == SecurityCheckState.Attention);
    public int UnavailableCount => _allRows.Count(row => row.IsUnavailable);
    public string ReviewCountLabel => string.Format(Text(ReviewCount == 1 ? "Security_FindingsToReviewOne" : "Security_FindingsToReviewMany"), ReviewCount);
    public string UnavailableCountLabel => string.Format(Text(UnavailableCount == 1 ? "Security_UnavailableOne" : "Security_UnavailableMany"), UnavailableCount);
    public bool HasUnavailable => UnavailableCount > 0;
    public string ViewUnavailableLabel => Text("Security_ViewUnavailable");
    private string ScanTime => _report?.ScannedAt.ToLocalTime().ToString("g") ?? "";
    public string LastScanLabel => _report is null ? "" : string.Format(
        Text(_report.Deep ? "Security_ResultsLastDeepScan" : "Security_ResultsLastScan"), ScanTime);
    public string Summary => _report == null ? "" : string.Format(Text(_report.Deep ? "Security_DeepSummary" : "Security_Summary"),
        ScanTime, ReviewCount, UnavailableCount);

    public SecurityViewModel(ISecurityScanService service, IDialogService dialogs, ILocalizationService localization,
        IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks, "Security_Title")
    {
        _service = service; _dialogs = dialogs; _localization = localization; _tasks = tasks;
        var areas = SecurityCheck.All.Where(check => !check.DeepOnly).Select(check => check.Area).Distinct().ToArray();
        ScanCoverage = areas.Select((area, index) => new SecurityCoverageItem(area, index < areas.Length - 1, localization)).ToArray();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task ScanAsync() => RunScanAsync(false);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DeepScanAsync() => RunScanAsync(true);

    /// <summary>Invalidates prior selections before discovery and publishes only a completed scan.</summary>
    private Task RunScanAsync(bool deep) => ExecuteAsync(async () =>
    {
        if (_tasks.IsTaskRunning) { Status = Text("Utilities_Wait"); IsError = true; return; }
        ClearReport();
        var task = _tasks.StartTask(deep ? DeepScanLabel : ScanLabel, true, true, true);
        _activeTask = task;
        IsScanning = true;
        try
        {
            IProgress<bool> cancellation = new Progress<bool>(_ =>
            {
                if (IsScanning && ReferenceEquals(_activeTask, task)) IsScanCancelling = true;
            });
            using var registration = task.Token.Register(() => cancellation.Report(true));
            var checks = ScanCoverage.Select(area => area.Title);
            if (deep) checks = checks.Concat(DeepCheckNames);
            ReportScanProgress(ScanProgressTitle, string.Join(Environment.NewLine, checks.Prepend(ScanProgressTitle)));

            var report = await _service.ScanAsync(deep, task.Token);
            task.Token.ThrowIfCancellationRequested();
            _report = report;
            foreach (var item in _report.Items)
            {
                var row = new SecurityFindingRow(item, _localization);
                row.PropertyChanged += OnRowChanged;
                _allRows.Add(row);
            }
            foreach (var area in Enum.GetValues<SecurityArea>())
                Categories.Add(new(area, _allRows.Where(row => row.Area == area).ToArray(), _localization));
            SelectedCategory = Categories.FirstOrDefault(category => category.Area == SecurityArea.Weaknesses);
            NotifyReport();
            ReportScanProgress(Title, Summary, 100);
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested)
        {
            Status = Text("Security_Cancelled");
            ReportScanProgress(Title, Status);
        }
        finally
        {
            IsScanning = false;
            IsScanCancelling = false;
            CompleteTask(task);
        }
    });

    private bool CanCancelScan() => IsScanning && !IsScanCancelling;

    /// <summary>Requests cancellation on the scan token shared with the task footer.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        if (!CanCancelScan()) return;
        IsScanCancelling = true;
        _activeTask?.Cancel();
    }

    /// <summary>Opens the shared task output while the scan is active.</summary>
    [RelayCommand(CanExecute = nameof(CanShowScanDetails))]
    private Task ShowScanDetailsAsync() => CanShowScanDetails()
        ? _dialogs.ShowTaskOutputDialogAsync(Text("Dialog_TerminalOutput_Title"), _tasks.GetTerminalOutputLines())
        : Task.CompletedTask;

    private bool CanShowScanDetails() => IsScanning && ReferenceEquals(_activeTask, _tasks.CurrentTaskCancellationSource);

    private void ReportScanProgress(string status, string output, double? percentage = null)
    {
        if (!ReferenceEquals(_activeTask, _tasks.CurrentTaskCancellationSource)) return;
        _tasks.UpdateDetailedProgress(new TaskProgressDetail
        {
            StatusText = status, TerminalOutput = output, IsIndeterminate = percentage is null, Progress = percentage
        });
    }

    [RelayCommand(CanExecute = nameof(CanFix))]
    private Task FixAsync() => ExecuteAsync(async () =>
    {
        var selected = _allRows.Where(row => row.IsSelected && row.CanSelect).ToArray();
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = Title, Message = Text("Security_Confirm"),
            Items = selected.Select(row => string.Join(Environment.NewLine, row.Name + ": " + row.Recommendation, row.Description, row.Evidence)).ToArray(),
            ConfirmButtonText = FixLabel, CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        var task = _tasks.CurrentTaskCancellationSource ?? throw new InvalidOperationException("The security task is unavailable.");
        _activeTask = task;
        try
        {
            var result = await _service.FixAsync(_report!.Id, selected.Select(row => row.Observation.Id).ToArray(), task.Token);
            foreach (var row in selected)
            {
                var outcome = result.Outcomes.SingleOrDefault(item => item.Id == row.Observation.Id);
                // Attempted or cancelled evidence needs a rescan before it can authorize another write.
                row.SetOutcome(outcome);
            }
            Status = string.Format(Text(result.Cancelled ? "Security_FixCancelled" : "Security_Fixed"),
                result.Outcomes.Count(item => item.Fixed), selected.Length);
            IsError = result.Outcomes.Any(item => !item.Fixed);
            foreach (var category in Categories) category.RefreshText();
            NotifyReport();
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested)
        {
            foreach (var row in selected) row.SetOutcome(null);
            Status = string.Format(Text("Security_FixCancelled"), 0, selected.Length);
            NotifyReport();
        }
        finally { _activeTask = null; }
    }, write: true, canCancel: true);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectAll()
    {
        var select = Findings.Any(row => row.CanSelect && !row.IsSelected);
        foreach (var row in Findings) row.IsSelected = select && row.CanSelect;
    }

    /// <summary>Reveals the next unavailable finding in report order without altering fix selections.</summary>
    internal SecurityFindingRow? ViewUnavailable()
    {
        if (!IsIdle) return null;
        var unavailable = _allRows.Where(row => row.IsUnavailable).ToArray();
        if (unavailable.Length == 0) return null;
        var index = Array.IndexOf(unavailable, _lastUnavailable);
        var row = unavailable[(index + 1) % unavailable.Length];
        SelectedCategory = Categories.First(category => category.Area == row.Area);
        row.AreDetailsExpanded = true;
        _lastUnavailable = row;
        return row;
    }

    /// <summary>Stops a scan on navigation; a native write finishes its current unit before stopping.</summary>
    public void Deactivate() => _activeTask?.Cancel();

    partial void OnSelectedCategoryChanged(SecurityCategoryRow? value)
    {
        Findings.Clear();
        if (value != null) foreach (var row in value.Rows) Findings.Add(row);
        OnPropertyChanged(nameof(ShowEmptyCategory));
    }

    private void ClearReport()
    {
        foreach (var row in _allRows) row.PropertyChanged -= OnRowChanged;
        _allRows.Clear();
        _report = null;
        _lastUnavailable = null;
        SelectedCategory = null;
        Findings.Clear(); Categories.Clear(); NotifyReport();
    }

    private void CompleteTask(CancellationTokenSource task)
    {
        if (ReferenceEquals(_tasks.CurrentTaskCancellationSource, task)) _tasks.CompleteTask();
        _activeTask = null;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecurityFindingRow.IsSelected)) NotifyActions();
    }

    private void NotifyReport()
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(PageDescription));
        OnPropertyChanged(nameof(ShowIntroduction));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ReviewCountLabel));
        OnPropertyChanged(nameof(UnavailableCountLabel));
        OnPropertyChanged(nameof(HasUnavailable));
        OnPropertyChanged(nameof(LastScanLabel));
        OnPropertyChanged(nameof(ShowEmptyCategory));
        NotifyActions();
    }

    protected override void NotifyActions()
    {
        OnPropertyChanged(nameof(ShowOtherProgress));
        ScanCommand.NotifyCanExecuteChanged(); DeepScanCommand.NotifyCanExecuteChanged(); FixCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanFix)); OnPropertyChanged(nameof(Selection));
    }

    /// <summary>Refreshes child presentation with the retained scan when the UI language changes.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is "" or null)
        {
            foreach (var area in ScanCoverage) area.RefreshText();
            foreach (var row in _allRows) row.RefreshText();
            foreach (var category in Categories) category.RefreshText();
        }
    }
}

/// <summary>Describes a catalogued scan area in the in-progress coverage strip.</summary>
public sealed class SecurityCoverageItem(SecurityArea area, bool hasDivider, ILocalizationService localization) : ObservableObject
{
    public bool HasDivider => hasDivider;
    public Icon Icon => IconFor(area);
    internal static Icon IconFor(SecurityArea area) => area switch
    {
        SecurityArea.Antivirus => Icon.Shield,
        SecurityArea.Firewall => Icon.Table,
        SecurityArea.Spyware => Icon.Target,
        SecurityArea.Weaknesses => Icon.Warning,
        SecurityArea.Updates => Icon.ArrowSync,
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };
    public string Title => localization.GetString(area switch
    {
        SecurityArea.Spyware => "Security_CoverageDefender",
        SecurityArea.Updates => "Security_CoverageUpdates",
        _ => "Security_Area_" + area
    });
    public string Description => localization.GetString("Security_CoverageDescription_" + area);

    internal void RefreshText() => OnPropertyChanged(string.Empty);
}

/// <summary>Projects one immutable observation and its explicit selection without rebuilding native evidence.</summary>
public partial class SecurityFindingRow(SecurityObservation observation, ILocalizationService localization) : ObservableObject
{
    public SecurityObservation Observation { get; private set; } = observation;
    private bool _attempted;
    private string _outcome = "";
    public SecurityArea Area => SecurityCheck.All.Single(check => check.Id == Observation.Id).Area;
    public Icon Icon => Observation.Id == SecurityCheckId.Uac ? Icon.ShieldSettings : SecurityCoverageItem.IconFor(Area);
    public string Name => localization.GetString($"Security_Check_{Observation.Id}");
    public string Description => localization.GetString($"Security_Description_{Observation.Id}");
    public string State => localization.GetString($"Security_Status_{Observation.State}");
    public string CurrentState => Observation.Current switch
    {
        "" => "—",
        "Enabled" => localization.GetString("TechnicalDetails_State_Enabled"),
        "Disabled" => localization.GetString("TechnicalDetails_State_Disabled"),
        _ => Observation.Current
    };
    public string CheckStatusLabel => localization.GetString("Security_CheckStatus");
    public string CurrentStateLabel => localization.GetString("Security_CurrentState");
    public string RecommendationLabel => localization.GetString("Security_Recommendation");
    public string DetailsLabel => localization.GetString("Logs_Details");
    public bool IsUnavailable => Observation.State == SecurityCheckState.Unknown;
    public string Recommendation => Observation.State is SecurityCheckState.Good or SecurityCheckState.NotApplicable
        ? "—" : localization.GetString($"Security_Action_{Observation.Id}");
    public string Evidence => string.Join(Environment.NewLine, new[] { Observation.Current, Observation.Evidence, _outcome }.Where(value => value.Length != 0));
    public IReadOnlyList<SecurityEvidenceValue> EvidenceValues { get; private set; } = ReadEvidenceValues(observation);
    public string DetailsText => string.Join(Environment.NewLine, new[] { EvidenceValues.Count == 0 ? Observation.Evidence : "", _outcome }
        .Where(value => value.Length != 0));
    public bool HasDetailsText => DetailsText.Length > 0;
    public bool HasDetails => EvidenceValues.Count > 0 || HasDetailsText;
    public bool CanSelect => !_attempted && Observation.CanFix && Observation.State == SecurityCheckState.Attention;
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool AreDetailsExpanded { get; set; }

    /// <summary>Retains errors and marks successful rows resolved; every attempted item requires fresh scan evidence.</summary>
    public void SetOutcome(SecurityFixOutcome? outcome)
    {
        _attempted = true;
        IsSelected = false;
        _outcome = outcome == null ? localization.GetString("Security_Rescan") : outcome.Error;
        if (outcome?.Fixed == true)
        {
            Observation = Observation with { State = SecurityCheckState.Good, Current = "", Evidence = localization.GetString("Security_Verified"), CanFix = false };
            EvidenceValues = [];
        }
        RefreshText();
    }

    /// <summary>Displays the native UAC and firewall key-value evidence while keeping the original text for fix authorization.</summary>
    private static IReadOnlyList<SecurityEvidenceValue> ReadEvidenceValues(SecurityObservation observation)
    {
        if (observation.Id is not (SecurityCheckId.Uac or SecurityCheckId.Firewall)) return [];
        var values = new List<SecurityEvidenceValue>();
        foreach (var pair in observation.Evidence.Split("; ", StringSplitOptions.None))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || separator == pair.Length - 1 ||
                !pair[..separator].All(character => char.IsAsciiLetterOrDigit(character) || character == '_')) return [];
            values.Add(new(pair[..separator], pair[(separator + 1)..]));
        }
        return values;
    }

    public void RefreshText() => OnPropertyChanged(string.Empty);
}

public sealed record SecurityEvidenceValue(string Name, string Value);

/// <summary>Summarizes the findings in one report category.</summary>
public sealed class SecurityCategoryRow(SecurityArea area, IReadOnlyList<SecurityFindingRow> rows, ILocalizationService localization) : ObservableObject
{
    public SecurityArea Area => area;
    public IReadOnlyList<SecurityFindingRow> Rows => rows;
    public string Name => localization.GetString($"Security_Area_{area}");
    public string Description => localization.GetString("Security_CoverageDescription_" + area);
    public Icon Icon => SecurityCoverageItem.IconFor(area);
    private int ReviewCount => Rows.Count(row => row.Observation.State == SecurityCheckState.Attention);
    private int UnavailableCount => Rows.Count(row => row.IsUnavailable);
    public bool HasUnavailable => UnavailableCount > 0;
    public string CategoryReviewLabel => string.Format(localization.GetString("Security_CategoryReviewCount"), ReviewCount);
    public string CategoryUnavailableLabel => string.Format(localization.GetString("Security_CategoryUnavailableCount"), UnavailableCount);
    public string Summary => string.Format(localization.GetString("Security_CategorySummary"),
        ReviewCount, UnavailableCount);
    public void RefreshText() => OnPropertyChanged(string.Empty);
}
