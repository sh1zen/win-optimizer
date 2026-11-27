using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.Common.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Management.ViewModels;

/// <summary>Owns explicit servicing requests, repair options and retained operation reports.</summary>
public partial class WindowsStatusViewModel : UtilityViewModel
{
    private const string RepairTitleKey = "WindowsStatus_Title";
    private readonly IWindowsServicingService _service;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _pickers;
    private readonly ITaskProgressService _tasks;
    private readonly ScanCancellationNotifications? _scanNotifications;
    private CancellationTokenSource? _activeTask;
    protected override string TitleKey => RepairTitleKey;
    [ObservableProperty] public partial string SourcePath { get; set; } = "";
    [ObservableProperty] public partial double WimIndex { get; set; } = 1;
    [ObservableProperty] public partial bool LimitAccess { get; set; }
    [ObservableProperty] public partial bool ResetBase { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiagnosisAction))]
    public partial bool UseQuickCheck { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport), nameof(ReportTitle), nameof(ReportSummary), nameof(ReportSeverity), nameof(ReportIcon), nameof(ReportActionLabel))]
    [NotifyPropertyChangedFor(nameof(NeedsRepair), nameof(ReportHealthNote), nameof(ProblemCount), nameof(ProblemCategory), nameof(StoreSize), nameof(StoreCleanupAdvice))]
    public partial WindowsServicingReport? Report { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationRunning), nameof(OperationLabel), nameof(ProgressTitle))]
    [NotifyCanExecuteChangedFor(nameof(CancelOperationCommand))]
    public partial WindowsServicingAction? ActiveAction { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressTitle), nameof(ProgressCaption), nameof(ProgressPercent), nameof(IsProgressIndeterminate))]
    public partial WindowsServicingProgress? OperationProgress { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressCaption))]
    [NotifyCanExecuteChangedFor(nameof(CancelOperationCommand))]
    public partial bool IsCancelling { get; private set; }

    public bool HasReport => Report != null;
    public bool IsOperationRunning => ActiveAction.HasValue;
    public string OperationLabel => GetActionLabel(ActiveAction);
    public string ProgressTitle => OperationProgress is { } progress ? Text("WindowsStatus_Step" + progress.Step) : OperationLabel;
    public string ProgressCaption => IsCancelling ? Text("Progress_Cancelling")
        : OperationProgress is { } progress ? $"{progress.Completed:N0} / {progress.Total:N0}" : "";
    public double ProgressPercent => OperationProgress is { Total: > 0 } progress ? 100.0 * progress.Completed / progress.Total : 0;
    public bool IsProgressIndeterminate => OperationProgress is not { Total: > 1 };
    public string CancelLabel => Text("Button_Cancel");
    public string ReportActionLabel => GetActionLabel(Report?.Request.Action);
    public string Hint => Text("WindowsStatus_Hint");
    public string QuickCheckLabel => Text("WindowsStatus_QuickCheck");
    public string DiagnosisLabel => Text("WindowsStatus_DiagnosisShort");
    public string AnalyzeLabel => Text("WindowsStatus_AnalyzeStore");
    public string RepairLabel => Text("WindowsStatus_Repair");
    public string ReportRepairLabel => Text("WindowsStatus_RepairShort");
    public string CleanupLabel => Text("WindowsStatus_Cleanup");
    public string CleanupDescription => Text("WindowsStatus_CleanupDescription");
    public string SourceLabel => Text("WindowsStatus_Source");
    public string SourceHint => Text("WindowsStatus_SourceHint");
    public string IndexLabel => Text("WindowsStatus_Index");
    public string BrowseWimLabel => Text("WindowsStatus_BrowseWim");
    public string BrowseFolderLabel => Text("WindowsStatus_BrowseFolder");
    public string LimitAccessLabel => Text("WindowsStatus_LimitAccess");
    public string ResetBaseLabel => Text("WindowsStatus_ResetBase");
    public string LogsHint => Text("WindowsStatus_Logs");
    public string DocsLabel => Text("WindowsStatus_Docs");
    public string ScanStep => Text("WindowsStatus_ScanStep");
    public string ResultsLabel => Text("WindowsStatus_Results");
    public string RepairOptionsLabel => Text("WindowsStatus_RepairOptions");
    public string MaintenanceLabel => Text("WindowsStatus_Maintenance");
    public bool NeedsRepair => Report is { State: WindowsServicingState.NeedsAttention, ImageHealth: WindowsImageHealth.Repairable };
    /// <summary>Selects the servicing action for the overview's Diagnose button.</summary>
    public string DiagnosisAction => UseQuickCheck ? nameof(WindowsServicingAction.QuickCheck) : nameof(WindowsServicingAction.Diagnosis);
    public string IntroTitle => Text("WindowsStatus_ScanTitle");
    public string IntroDescription => Text("WindowsStatus_ScanDescription");

    /// <summary>Names the requested operation consistently in progress and completed reports.</summary>
    private string GetActionLabel(WindowsServicingAction? action) => action switch
    {
        WindowsServicingAction.QuickCheck => QuickCheckLabel,
        WindowsServicingAction.Diagnosis => Text("WindowsStatus_Diagnosis"),
        WindowsServicingAction.AnalyzeStore => AnalyzeLabel,
        WindowsServicingAction.Repair => RepairLabel,
        WindowsServicingAction.Cleanup => CleanupLabel,
        _ => ""
    };

    public string ReportTitle => Report switch
    {
        null => "",
        { State: WindowsServicingState.RestartRequired } => Text("WindowsStatus_RestartRequiredTitle"),
        { State: WindowsServicingState.Failed } => Text("WindowsStatus_OperationFailed"),
        { State: WindowsServicingState.NeedsAttention } report => Text("WindowsStatus_Health" + report.ImageHealth),
        { Request.Action: WindowsServicingAction.QuickCheck, ImageHealth: WindowsImageHealth.Healthy } => Text("WindowsStatus_HealthHealthy"),
        _ => Text("WindowsStatus_OperationCompleted")
    };

    /// <summary>Distinguishes detected corruption from a repair or cleanup that actually stopped.</summary>
    public string ReportSummary => Report switch
    {
        null => "",
        { State: WindowsServicingState.NeedsAttention, ImageHealth: WindowsImageHealth.Repairable } =>
            Text(Report.Request.Action switch
            {
                WindowsServicingAction.Repair => "WindowsStatus_StateRepairIncomplete",
                WindowsServicingAction.Cleanup => "WindowsStatus_StateCleanupNeedsRepair",
                _ => "WindowsStatus_StateRepairable"
            }),
        { State: WindowsServicingState.NeedsAttention, ImageHealth: WindowsImageHealth.NonRepairable } => Text("WindowsStatus_StateNonRepairable"),
        { State: WindowsServicingState.Completed, Request.Action: WindowsServicingAction.QuickCheck } => "",
        { State: WindowsServicingState.Completed, Request.Action: WindowsServicingAction.AnalyzeStore } =>
            StoreAnalysis == null ? Text("WindowsStatus_AnalysisUnavailable") : "",
        _ => Text("WindowsStatus_State" + Report.State)
    };
    public string ReportActionError => Text("WindowsStatus_StateFailed");
    public string ReportHealthNote => Report is { State: WindowsServicingState.RestartRequired or WindowsServicingState.Failed or WindowsServicingState.ReviewFileResults,
        ImageHealth: not WindowsImageHealth.Unknown } report ? Text("WindowsStatus_Health" + report.ImageHealth) : "";
    private bool HasStoreProblems => Report?.ImageHealth is WindowsImageHealth.Repairable or WindowsImageHealth.NonRepairable;
    private WindowsComponentStoreFindings? StoreFindings => Report?.Steps.FirstOrDefault(step => step.StoreFindings != null)?.StoreFindings;

    /// <summary>Distinguishes a component count from a count of corruption findings.</summary>
    public string ProblemCount => !HasStoreProblems ? "" : StoreFindings switch
    {
        { AffectedComponents: { } count } => string.Format(Text("WindowsStatus_AffectedComponentCount"), count),
        { DetectedProblems: var count } => string.Format(Text("WindowsStatus_DetectedProblemCount"), count),
        _ => Text(Report?.Request.Action == WindowsServicingAction.QuickCheck
            ? "WindowsStatus_QuickCheckDetails" : "WindowsStatus_ComponentCountUnavailable")
    };
    public string ProblemCategory => HasStoreProblems ? StoreFindings?.SingleComponentName ?? Text("WindowsStatus_ProblemCategory") : "";
    private WindowsComponentStoreAnalysis? StoreAnalysis => Report is { Request.Action: not WindowsServicingAction.Cleanup } report
        ? report.Steps.FirstOrDefault(step => step.StoreAnalysis != null)?.StoreAnalysis : null;
    public string StoreSize => StoreAnalysis?.SizeBytes is { } size
        ? string.Format(Text("WindowsStatus_StoreSize"), ByteSizeFormatter.FormatBytes(size, CultureInfo.CurrentCulture)) : "";
    public string StoreCleanupAdvice => StoreAnalysis?.CleanupRecommended switch
    {
        true => Text("WindowsStatus_CleanupRecommended"),
        false => Text("WindowsStatus_CleanupNotRecommended"),
        null => ""
    };
    public InfoBarSeverity ReportSeverity => Report?.State switch
    {
        WindowsServicingState.Failed => InfoBarSeverity.Error,
        WindowsServicingState.NeedsAttention or WindowsServicingState.RestartRequired => InfoBarSeverity.Warning,
        WindowsServicingState.Completed => InfoBarSeverity.Success,
        _ => InfoBarSeverity.Informational
    };
    public Icon ReportIcon => Report?.State == WindowsServicingState.RestartRequired ? Icon.ArrowClockwise : ReportSeverity switch
    {
        InfoBarSeverity.Error => Icon.ErrorCircle,
        InfoBarSeverity.Warning => Icon.Warning,
        InfoBarSeverity.Success => Icon.CheckmarkCircle,
        _ => Icon.Info
    };

    public WindowsStatusViewModel(IWindowsServicingService service, IDialogService dialogs, IFilePickerService pickers,
        ILocalizationService localization, IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks,
        ScanCancellationNotifications? scanNotifications = null)
        : base(localization, mode, dispatcher, tasks, RepairTitleKey)
    { _service = service; _dialogs = dialogs; _pickers = pickers; _tasks = tasks; _scanNotifications = scanNotifications; }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseWim()
    {
        if (_pickers.PickFile([Text("WindowsStatus_WimFiles"), "*.wim"]) is { } path) SourcePath = path;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseFolder()
    {
        if (_pickers.PickFolder(SourceLabel) is { } path) SourcePath = path;
    }

    /// <summary>Freezes options before confirmation and uses the shared task and shutdown lifetime.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task RunAsync(string actionName) => ExecuteAsync(async () =>
    {
        if (!Enum.TryParse<WindowsServicingAction>(actionName, out var action) || !Enum.IsDefined(action))
            throw new ArgumentException("Unknown Windows servicing action.");
        var request = new WindowsServicingRequest(action);
        if (action == WindowsServicingAction.Repair)
        {
            var isWim = Path.GetExtension(SourcePath.Trim()).Equals(".wim", StringComparison.OrdinalIgnoreCase);
            if (isWim && (!double.IsFinite(WimIndex) || WimIndex < 1 || WimIndex > int.MaxValue || WimIndex != Math.Truncate(WimIndex)))
                throw new ArgumentException(Text("WindowsStatus_InvalidIndex"));
            request = request with { SourcePath = SourcePath.Trim(), WimIndex = isWim ? (int)WimIndex : 1, LimitAccess = LimitAccess };
        }
        if (action == WindowsServicingAction.Cleanup) request = request with { ResetBase = ResetBase };
        if (action is WindowsServicingAction.Repair or WindowsServicingAction.Cleanup)
        {
            var confirmation = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
            {
                Title = Title,
                Message = Text(request.ResetBase ? "WindowsStatus_ConfirmResetBase" : action == WindowsServicingAction.Repair ? "WindowsStatus_ConfirmRepair" : "WindowsStatus_ConfirmCleanup"),
                CheckboxText = request.ResetBase ? Text("WindowsStatus_ResetBaseAcknowledgement") : null,
                CheckboxInitiallyChecked = false,
                ConfirmButtonText = action == WindowsServicingAction.Repair ? RepairLabel : CleanupLabel,
                CancelButtonText = Text("Button_Cancel"),
                Items = action == WindowsServicingAction.Repair ? [request.SourcePath.Length == 0 ? Text("WindowsStatus_DefaultSource") : request.SourcePath,
                    request.LimitAccess ? LimitAccessLabel : Text("WindowsStatus_WindowsUpdateAllowed")] : null
            });
            if (!confirmation.Confirmed || request.ResetBase && !confirmation.CheckboxChecked) return;
        }
        var task = _tasks.CurrentTaskCancellationSource ?? throw new InvalidOperationException("The servicing task was not started.");
        WindowsServicingReport report;
        _activeTask = task;
        ActiveAction = action;
        try
        {
            var progress = new Progress<WindowsServicingProgress>(value =>
            {
                if (ReferenceEquals(_activeTask, task) && !task.IsCancellationRequested) OperationProgress = value;
            });
            report = await _service.RunAsync(request, progress, task.Token);
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested)
        { NotifyCancellation(action); return; }
        finally
        {
            _activeTask = null;
            ActiveAction = null;
            OperationProgress = null;
            IsCancelling = false;
        }
        if (report.State == WindowsServicingState.Cancelled)
        {
            NotifyCancellation(action);
            return;
        }
        Report = report;
    }, write: true, canCancel: true, showInTaskBar: false);

    private void NotifyCancellation(WindowsServicingAction action) =>
        _scanNotifications?.Publish(this, GetActionLabel(action), Text("WindowsStatus_StateCancelled"));

    private bool CanCancelOperation() => IsOperationRunning && !IsCancelling;

    /// <summary>Requests cancellation from the inline progress card for the active servicing command.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
        if (_activeTask is not { } task || !CanCancelOperation()) return;
        IsCancelling = true;
        task.Cancel();
    }

    protected override void NotifyActions()
    {
        RunCommand.NotifyCanExecuteChanged(); BrowseWimCommand.NotifyCanExecuteChanged(); BrowseFolderCommand.NotifyCanExecuteChanged();
    }
}
