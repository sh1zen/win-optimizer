using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly List<SecurityFindingRow> _allRows = [];
    protected override string TitleKey => "Security_Title";
    public ObservableCollection<SecurityCategoryRow> Categories { get; } = [];
    public ObservableCollection<SecurityFindingRow> Findings { get; } = [];
    [ObservableProperty] public partial SecurityCategoryRow? SelectedCategory { get; set; }
    public bool HasReport => _report != null;
    public bool ShowIntroduction => !HasReport;
    public string ScanLabel => Text("Security_Scan");
    public string DeepScanLabel => Text("Security_DeepScan");
    public string FixLabel => Text("Security_Fix");
    public string NameLabel => Text("Security_Item");
    public string StateLabel => Text("Security_State");
    public string RecommendationLabel => Text("Security_Recommendation");
    public string Intro => Text("Security_Intro");
    public string SelectAllLabel => Text("Security_SelectAll");
    public string Selection => string.Format(Text("Security_Selection"), _allRows.Count(row => row.IsSelected));
    public bool CanFix => CanWrite && _allRows.Any(row => row.IsSelected && row.CanSelect);
    public string Summary => _report == null ? "" : string.Format(Text(_report.Deep ? "Security_DeepSummary" : "Security_Summary"),
        _report.ScannedAt.ToLocalTime().ToString("g"), _allRows.Count(row => row.Observation.State == SecurityCheckState.Attention),
        _allRows.Count(row => row.Observation.State == SecurityCheckState.Unknown));

    public SecurityViewModel(ISecurityScanService service, IDialogService dialogs, ILocalizationService localization,
        IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks, "Security_Title")
    { _service = service; _dialogs = dialogs; _localization = localization; _tasks = tasks; }

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
        try
        {
            _report = await _service.ScanAsync(deep, task.Token);
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
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested) { Status = Text("Security_Cancelled"); }
        finally { CompleteTask(task); }
    });

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

    /// <summary>Stops a scan on navigation; a native write finishes its current unit before stopping.</summary>
    public void Deactivate() => _activeTask?.Cancel();

    partial void OnSelectedCategoryChanged(SecurityCategoryRow? value)
    {
        Findings.Clear();
        if (value != null) foreach (var row in value.Rows) Findings.Add(row);
    }

    private void ClearReport()
    {
        foreach (var row in _allRows) row.PropertyChanged -= OnRowChanged;
        _allRows.Clear();
        _report = null;
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
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(ShowIntroduction)); OnPropertyChanged(nameof(Summary)); NotifyActions();
    }

    protected override void NotifyActions()
    {
        ScanCommand.NotifyCanExecuteChanged(); DeepScanCommand.NotifyCanExecuteChanged(); FixCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanFix)); OnPropertyChanged(nameof(Selection));
    }

    /// <summary>Refreshes child presentation with the retained scan when the UI language changes.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is "" or null)
        {
            foreach (var row in _allRows) row.RefreshText();
            foreach (var category in Categories) category.RefreshText();
        }
    }
}

/// <summary>Projects one immutable observation and its explicit selection without rebuilding native evidence.</summary>
public partial class SecurityFindingRow(SecurityObservation observation, ILocalizationService localization) : ObservableObject
{
    public SecurityObservation Observation { get; private set; } = observation;
    private bool _attempted;
    private string _outcome = "";
    public SecurityArea Area => SecurityCheck.All.Single(check => check.Id == Observation.Id).Area;
    public string Name => localization.GetString($"Security_Check_{Observation.Id}");
    public string Description => localization.GetString($"Security_Description_{Observation.Id}");
    public string State => localization.GetString($"Security_Status_{Observation.State}");
    public string Recommendation => Observation.State is SecurityCheckState.Good or SecurityCheckState.NotApplicable
        ? "—" : localization.GetString($"Security_Action_{Observation.Id}");
    public string ReviewSummary => Recommendation == "—" ? State : State + " · " + Recommendation;
    public string Evidence => string.Join(Environment.NewLine, new[] { Observation.Current, Observation.Evidence, _outcome }.Where(value => value.Length != 0));
    public bool CanSelect => !_attempted && Observation.CanFix && Observation.State == SecurityCheckState.Attention;
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Retains errors and marks successful rows resolved; every attempted item requires fresh scan evidence.</summary>
    public void SetOutcome(SecurityFixOutcome? outcome)
    {
        _attempted = true;
        IsSelected = false;
        _outcome = outcome == null ? localization.GetString("Security_Rescan") : outcome.Error;
        if (outcome?.Fixed == true) Observation = Observation with { State = SecurityCheckState.Good, Current = "", Evidence = localization.GetString("Security_Verified"), CanFix = false };
        RefreshText();
    }
    public void RefreshText() => OnPropertyChanged(string.Empty);
}

/// <summary>Summarizes the same result rows used by the findings table.</summary>
public sealed class SecurityCategoryRow(SecurityArea area, IReadOnlyList<SecurityFindingRow> rows, ILocalizationService localization) : ObservableObject
{
    public SecurityArea Area => area;
    public IReadOnlyList<SecurityFindingRow> Rows => rows;
    public string Name => localization.GetString($"Security_Area_{area}");
    public string Summary => string.Format(localization.GetString("Security_CategorySummary"),
        Rows.Count(row => row.Observation.State == SecurityCheckState.Attention), Rows.Count(row => row.Observation.State == SecurityCheckState.Unknown));
    public void RefreshText() => OnPropertyChanged(string.Empty);
}
