using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Management.ViewModels;

/// <summary>Owns explicit servicing requests, repair options and retained operation reports.</summary>
public partial class WindowsStatusViewModel : UtilityViewModel
{
    private readonly IWindowsServicingService _service;
    private readonly IDialogService _dialogs;
    private readonly IFilePickerService _pickers;
    private readonly ITaskProgressService _tasks;
    protected override string TitleKey => "WindowsStatus_Title";
    [ObservableProperty] public partial string SourcePath { get; set; } = "";
    [ObservableProperty] public partial double WimIndex { get; set; } = 1;
    [ObservableProperty] public partial bool LimitAccess { get; set; }
    [ObservableProperty] public partial bool ResetBase { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport), nameof(ReportSummary), nameof(ReportSeverity), nameof(HealthSummary))]
    [NotifyPropertyChangedFor(nameof(NeedsRepair), nameof(PrimaryAction), nameof(PrimaryActionLabel), nameof(IntroTitle), nameof(IntroDescription))]
    public partial WindowsServicingReport? Report { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<WindowsServicingStepViewModel> Steps { get; private set; } = [];
    public bool HasReport => Report != null;
    public string Hint => Text("WindowsStatus_Hint");
    public string QuickCheckLabel => Text("WindowsStatus_QuickCheck");
    public string DiagnosisLabel => Text("WindowsStatus_Diagnosis");
    public string AnalyzeLabel => Text("WindowsStatus_AnalyzeStore");
    public string RepairLabel => Text("WindowsStatus_Repair");
    public string CleanupLabel => Text("WindowsStatus_Cleanup");
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
    public string PrimaryAction => NeedsRepair ? "Repair" : "Diagnosis";
    public string PrimaryActionLabel => Text(NeedsRepair ? "WindowsStatus_RepairShort" : "WindowsStatus_DiagnosisShort");
    public string IntroTitle => Text(NeedsRepair ? "WindowsStatus_RepairTitle" : "WindowsStatus_ScanTitle");
    public string IntroDescription => Text(NeedsRepair ? "WindowsStatus_RepairDescription" : "WindowsStatus_ScanDescription");

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
        _ => Text("WindowsStatus_State" + Report.State)
    };
    public string HealthSummary => Report == null ? "" : string.Format(Text("WindowsStatus_HealthSummary"),
        Report.CompletedAt.ToLocalTime().ToString("g"), Text("WindowsStatus_Health" + Report.ImageHealth));
    public InfoBarSeverity ReportSeverity => Report?.State switch
    {
        WindowsServicingState.Failed => InfoBarSeverity.Error,
        WindowsServicingState.NeedsAttention or WindowsServicingState.RestartRequired => InfoBarSeverity.Warning,
        WindowsServicingState.Completed => InfoBarSeverity.Success,
        _ => InfoBarSeverity.Informational
    };

    public WindowsStatusViewModel(IWindowsServicingService service, IDialogService dialogs, IFilePickerService pickers,
        ILocalizationService localization, IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks, "Nav_Management")
    { _service = service; _dialogs = dialogs; _pickers = pickers; _tasks = tasks; }

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
        var progress = new Progress<WindowsServicingProgress>(value =>
        {
            if (ReferenceEquals(_tasks.CurrentTaskCancellationSource, task))
                _tasks.UpdateDetailedProgress(new TaskProgressDetail { StatusText = Text("WindowsStatus_Step" + value.Step), IsIndeterminate = true });
        });
        try { Report = await _service.RunAsync(request, progress, task.Token); }
        catch (OperationCanceledException) when (task.IsCancellationRequested)
        { Status = Text("WindowsStatus_StateCancelled"); return; }
        Steps = Report.Steps.Select(step => new WindowsServicingStepViewModel(step, Text("WindowsStatus_Step" + step.Step),
            Text(step.Succeeded ? "WindowsStatus_CommandCompleted" : "WindowsStatus_CommandFailed"))).ToArray();
    }, write: true, canCancel: true);

    protected override void NotifyActions()
    {
        RunCommand.NotifyCanExecuteChanged(); BrowseWimCommand.NotifyCanExecuteChanged(); BrowseFolderCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>Formats an original phase receipt without inferring SFC integrity from its exit code.</summary>
public sealed class WindowsServicingStepViewModel(WindowsServicingStepResult result, string title, string status)
{
    public WindowsServicingStepResult Result { get; } = result;
    public string Title => title;
    public string Output => Result.Output;
    public string Status => Result.ExitCode is { } code ? $"{status} · {code} (0x{code:X8})" : status;
}
