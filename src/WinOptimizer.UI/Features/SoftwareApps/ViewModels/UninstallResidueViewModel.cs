using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

public enum UninstallReviewStep { Scan, Files, Registry }

/// <summary>Owns explicit residue selection, inline confirmation and awaited backup/cleanup/recovery.</summary>
public sealed partial class UninstallResidueViewModel : ObservableObject, IDisposable
{
    private readonly IUninstallResidueSession _session;
    private readonly IUninstallResidueService _service;
    private readonly ITaskProgressService _tasks;
    private readonly ILocalizationService _text;
    private readonly List<UninstallResidue> _findings;
    private Guid? _lastBackup;
    private bool _restoring;
    private CancellationTokenSource? _operation;

    public UninstallResidueViewModel(IUninstallResidueSession session, AppUninstallResult verification,
        IUninstallResidueService service, ITaskProgressService tasks, ILocalizationService text)
    {
        _session = session; _service = service; _tasks = tasks; _text = text;
        _findings = [];
        Summary = ApplicationSummary(verification.Applications);
        Notices = string.Join("\n", verification.Notices);
    }

    private string ApplicationSummary(IReadOnlyList<UninstallVerification> applications) =>
        string.Join("\n", applications.Select(app => $"{app.AppName}: {_text.GetString("UninstallResidue_State_" + app.State)} — {app.Detail}"));

    private void ApplyScan(UninstallResidueScan scan)
    {
        _findings.Clear();
        _findings.AddRange(scan.Findings);
        Summary = ApplicationSummary(scan.Applications)
            + "\n" + _text.GetString("UninstallResidue_Observed", scan.ObservedChanges)
            + "\n" + _text.GetString(scan.Findings.Count == 0 ? "UninstallResidue_None" : "UninstallResidue_ReviewHint");
        Notices = string.Join("\n", scan.Notices);
        Rebuild();
        Step = UninstallReviewStep.Files;
    }

    [ObservableProperty] public partial IReadOnlyList<UninstallResidueNode> RegistryRoots { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<UninstallResidueNode> FileRoots { get; set; } = [];
    [ObservableProperty] public partial UninstallReviewStep Step { get; set; } = UninstallReviewStep.Scan;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsConfirming { get; set; }
    [ObservableProperty] public partial string Confirmation { get; set; } = string.Empty;
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    [ObservableProperty] public partial string Detail { get; set; } = string.Empty;
    [ObservableProperty] public partial string Summary { get; set; } = string.Empty;
    [ObservableProperty] public partial string Notices { get; set; } = string.Empty;
    public bool CanClose => !IsBusy;
    public bool IsScanStep => Step == UninstallReviewStep.Scan;
    public bool HasResults => !IsScanStep;
    public bool IsCurrentEmpty => HasResults && CurrentRoots.Count == 0;
    public string EmptyLabel => _text.GetString("Cleaner_NoResults");
    public bool CanScan => !IsBusy && !IsConfirming;
    public bool CanSelect => HasResults && CanScan;
    public bool CanNext => CanSelect && Step == UninstallReviewStep.Files;
    public bool CanPrevious => CanSelect && Step == UninstallReviewStep.Registry;
    public IReadOnlyList<UninstallResidueNode> CurrentRoots => Step == UninstallReviewStep.Files ? FileRoots : RegistryRoots;
    public string StepLabel => $"{(Step == UninstallReviewStep.Files ? 1 : 2)} / 2 · {(Step == UninstallReviewStep.Files ? FilesLabel : RegistryLabel)}";
    public string NextLabel => RegistryLabel + " →";
    public string PreviousLabel => "← " + FilesLabel;
    public string ScanHint => _text.GetString("UninstallResidue_ScanHint");
    public string ScanLabel => _text.GetString(IsScanStep ? "UninstallResidue_Scan" : "UninstallResidue_Rescan");
    public bool CanDelete => CanSelect && SelectedIds().Count > 0;
    public bool CanRestore => CanScan && _lastBackup.HasValue;
    public string SelectionSummary => _text.GetString("UninstallResidue_Selected", SelectedIds().Count,
        Step == UninstallReviewStep.Registry ? _findings.Count(item => item.Kind is UninstallArtifactKind.RegistryKey or UninstallArtifactKind.RegistryValue) : 0,
        Step == UninstallReviewStep.Files ? _findings.Count(item => item.Kind is UninstallArtifactKind.File or UninstallArtifactKind.Directory) : 0);
    public string RegistryLabel => _text.GetString("UninstallResidue_Registry");
    public string Title => _text.GetString("UninstallResidue_Title");
    public string FinishLabel => _text.GetString("UninstallResidue_Finish");
    public string FilesLabel => _text.GetString("UninstallResidue_Files");
    public string SelectAllLabel => _text.GetString("Common_SelectAll");
    public string SelectNoneLabel => _text.GetString("UninstallResidue_SelectNone");
    public string DeleteLabel => _text.GetString("UninstallResidue_Delete");
    public string ConfirmLabel => _text.GetString("UninstallResidue_Confirm");
    public string CancelLabel => _text.GetString("Button_Cancel");
    public string RestoreLabel => _text.GetString("UninstallResidue_RestoreLast");

    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnIsConfirmingChanged(bool value) => RefreshCommands();
    partial void OnStepChanged(UninstallReviewStep value)
    {
        Detail = string.Empty;
        RefreshCommands();
    }

    private IReadOnlyCollection<Guid> SelectedIds() => CurrentRoots.SelectMany(root => root.SelectedIds()).Distinct().ToArray();

    private void Rebuild(bool preserveSelection = false)
    {
        var selected = preserveSelection ? RegistryRoots.Concat(FileRoots).SelectMany(root => root.SelectedIds()).ToHashSet() : null;
        RegistryRoots = UninstallResidueNode.Build(_findings.Where(item => item.Kind is UninstallArtifactKind.RegistryKey or UninstallArtifactKind.RegistryValue), true,
            _text.GetString("UninstallResidue_View32"), _text.GetString("UninstallResidue_View64"), OnSelectionChanged, selected);
        FileRoots = UninstallResidueNode.Build(_findings.Where(item => item.Kind is UninstallArtifactKind.File or UninstallArtifactKind.Directory), false,
            string.Empty, string.Empty, OnSelectionChanged, selected);
        RefreshCommands();
    }

    private void OnSelectionChanged(UninstallResidueNode node) { Detail = node.Detail; RefreshCommands(); }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanSelect)); OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanClose)); OnPropertyChanged(nameof(CanConfirm)); OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(IsScanStep)); OnPropertyChanged(nameof(HasResults)); OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CurrentRoots)); OnPropertyChanged(nameof(StepLabel)); OnPropertyChanged(nameof(ScanLabel));
        OnPropertyChanged(nameof(IsCurrentEmpty));
        OnPropertyChanged(nameof(CanNext)); OnPropertyChanged(nameof(CanPrevious));
        RequestDeleteCommand.NotifyCanExecuteChanged(); RequestRestoreCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged(); SelectNoneCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged(); PreviousCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void SelectAll() { foreach (var root in CurrentRoots) root.IsSelected = true; }
    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void SelectNone() { foreach (var root in CurrentRoots) root.IsSelected = false; }
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() => Step = UninstallReviewStep.Registry;
    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous() => Step = UninstallReviewStep.Files;
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void RequestDelete()
    {
        _restoring = false;
        Confirmation = _text.GetString("UninstallResidue_ConfirmDelete", SelectedIds().Count);
        IsConfirming = true;
    }
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void RequestRestore()
    {
        _restoring = true;
        Confirmation = _text.GetString("UninstallResidue_ConfirmRestore");
        IsConfirming = true;
    }
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) _operation?.Cancel();
        else IsConfirming = false;
    }
    public bool CanConfirm => IsConfirming && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RunOperationAsync(async token =>
    {
        _findings.Clear();
        Rebuild();
        Step = UninstallReviewStep.Scan;
        ApplyScan(await _session.ScanAsync(null, token));
        Status = _text.GetString("UninstallResidue_ScanComplete");
    }, "UninstallResidue_Scan");

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        await RunOperationAsync(async token =>
        {
            if (_restoring && _lastBackup.HasValue)
            {
                var restored = await _service.RestoreAsync(_lastBackup.Value, token);
                Status = _text.GetString("UninstallResidue_Restored", restored.Restored) + "\n" + string.Join("\n", restored.Errors);
                // Old scan evidence must not be reused after recovery.
                _findings.Clear();
            }
            else
            {
                var result = await _session.CleanAsync(SelectedIds(), new Progress<UninstallResidueProgress>(progress =>
                    _tasks.UpdateProgress(progress.Total > 0 ? (int)(100d * progress.Completed / progress.Total) : 0, progress.Message)), token);
                _lastBackup = result.BackupId ?? _lastBackup;
                _findings.RemoveAll(finding => result.Removed.Contains(finding.Id));
                Status = _text.GetString("UninstallResidue_Removed", result.Removed.Count) + "\n" + string.Join("\n", result.Errors);
            }
            Rebuild(preserveSelection: true);
        }, _restoring ? "UninstallResidue_Restoring" : "UninstallResidue_Cleaning");
    }

    /// <summary>Keeps cancellation and page lifetime tied to the awaited operation.</summary>
    private async Task RunOperationAsync(Func<CancellationToken, Task> action, string title)
    {
        IsBusy = true;
        _operation = _tasks.StartTask(_text.GetString(title), true);
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Status = _text.GetString("UninstallResidue_Cancelled"); }
        catch (Exception ex) { Status = ex.Message; }
        finally
        {
            _operation = null;
            _tasks.CompleteTask();
            IsConfirming = false;
            IsBusy = false;
        }
    }

    public void Dispose() => _operation?.Cancel();
}
