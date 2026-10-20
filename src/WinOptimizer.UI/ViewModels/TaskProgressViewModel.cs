using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.ViewModels;

/// <summary>
/// Child ViewModel for task progress display in the main window.
/// Manages progress bar state, queue info, and multi-script slots.
/// </summary>
public partial class TaskProgressViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly ITaskProgressService _taskProgressService;
    private readonly IDispatcherService _dispatcherService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsTaskFailed { get; set; }

    [ObservableProperty] public partial bool IsTaskRunning { get; set; }
    [ObservableProperty] public partial bool CanCancel { get; set; } = true;
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;

    private CancellationTokenSource? _hideDelayCts;

    [ObservableProperty]
    public partial string AppName { get; set; }

    [ObservableProperty]
    public partial string LastTerminalLine { get; set; }

    [ObservableProperty]
    public partial string QueueStatusText { get; set; }

    [ObservableProperty]
    public partial string QueueNextItemName { get; set; }

    [ObservableProperty]
    public partial bool IsQueueVisible { get; set; }

    [ObservableProperty]
    public partial int ActiveScriptCount { get; set; }

    /// <summary>
    /// Event raised when a multi-script progress update is received.
    /// Parameters: (slotIndex, detail).
    /// </summary>
    public event Action<int, TaskProgressDetail>? ScriptProgressReceived;

    public string CancelButtonLabel =>
        _localizationService.GetString("Button_Cancel") ?? "Cancel";

    public string CloseButtonLabel =>
        _localizationService.GetString("Button_Close") ?? "Close";

    public TaskProgressViewModel(
        ITaskProgressService taskProgressService,
        IDispatcherService dispatcherService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        ILogService logService)
    {
        _taskProgressService = taskProgressService;
        _dispatcherService = dispatcherService;
        _dialogService = dialogService;
        _localizationService = localizationService;

        AppName = string.Empty;
        LastTerminalLine = string.Empty;
        QueueStatusText = string.Empty;
        QueueNextItemName = string.Empty;

        _taskProgressService.ProgressUpdated += OnProgressUpdated;
        _localizationService.LanguageChanged += OnLanguageChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _taskProgressService.ProgressUpdated -= OnProgressUpdated;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        _hideDelayCts?.Cancel();
        _hideDelayCts?.Dispose();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CancelButtonLabel));
        OnPropertyChanged(nameof(CloseButtonLabel));
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!CanCancel) return;
        _taskProgressService.CancelCurrentTask();
        if (_taskProgressService.IsTaskRunning)
            LastTerminalLine = _localizationService.GetString("Progress_Cancelling");
    }

    [RelayCommand]
    private void CloseFailedTask()
    {
        IsTaskFailed = false;
        IsLoading = false;
    }

    [RelayCommand]
    private async Task ShowDetailsAsync()
    {
        var terminalLines = _taskProgressService.GetTerminalOutputLines();
        var title = _localizationService.GetString("Dialog_TerminalOutput_Title");
        await _dialogService.ShowTaskOutputDialogAsync(title, terminalLines);

        // After the dialog is closed, dismiss the progress control if the task is no longer running
        if (!_taskProgressService.IsTaskRunning)
        {
            IsTaskFailed = false;
            IsLoading = false;
        }
    }

    private void OnProgressUpdated(object? sender, TaskProgressDetail detail)
    {
        // Capture lifecycle state before completion disposes its token or another task starts.
        var running = _taskProgressService.IsTaskRunning;
        var cancelled = _taskProgressService.CurrentTaskCancellationSource?.IsCancellationRequested == true;
        _dispatcherService.RunOnUIThread(() =>
        {
            CanCancel = detail.CanCancel;
            if (!detail.ShowInTaskBar)
            {
                // The page owns presentation; retain lifecycle state for app-wide operation guards.
                _hideDelayCts?.Cancel();
                _hideDelayCts = null;
                IsLoading = false;
                IsTaskFailed = false;
                IsTaskRunning = running;
                ActiveScriptCount = 0;
                IsQueueVisible = false;
                QueueStatusText = "";
                QueueNextItemName = "";
                AppName = "";
                LastTerminalLine = "";
                return;
            }
            if (detail.ScriptSlotCount > 0)
            {
                // Multi-script mode: update slot count and raise per-slot event
                ActiveScriptCount = detail.ScriptSlotCount;
                ScriptProgressReceived?.Invoke(detail.ScriptSlotIndex, detail);
            }
            else if (ActiveScriptCount > 0 && detail.ScriptSlotIndex == -1)
            {
                // Multi-script task completed (ScriptSlotCount went to 0)
                ActiveScriptCount = 0;
            }
            else
            {
                var wasRunning = IsLoading;
                var wasTaskRunning = IsTaskRunning;
                var isNowRunning = running;
                IsTaskRunning = running;
                if (detail.Progress.HasValue) ProgressValue = detail.Progress.Value;
                IsIndeterminate = detail.IsIndeterminate || !detail.Progress.HasValue;

                if (isNowRunning)
                {
                    // Cancel any pending hide-delay from a previous task
                    _hideDelayCts?.Cancel();
                    _hideDelayCts = null;
                    if (!wasTaskRunning) IsTaskFailed = false;

                    IsLoading = true;
                    if (!string.IsNullOrEmpty(detail.StatusText))
                        AppName = detail.StatusText;
                    LastTerminalLine = detail.TerminalOutput ?? string.Empty;

                    // Zero percent is normal task startup; failures have an explicit severity.
                    if (detail.LogLevel == LogLevel.Error)
                        IsTaskFailed = true;
                }
                else if (wasRunning)
                {
                    // Task just stopped running -- handle completion
                    var wasCancelled = cancelled;

                    if (wasCancelled)
                    {
                        // Cancelled by user: hide the control immediately
                        IsTaskFailed = false;
                        IsLoading = false;
                    }
                    else if (IsTaskFailed)
                    {
                        // Failed: keep the control visible with "click to see details"
                        IsLoading = true;
                        LastTerminalLine = _localizationService.GetString("Progress_ClickToSeeDetails");
                    }
                    else
                    {
                        // Success: show the completion state briefly, then hide after 2 seconds
                        if (!string.IsNullOrEmpty(detail.StatusText))
                            AppName = detail.StatusText;
                        LastTerminalLine = detail.TerminalOutput ?? string.Empty;
                        _ = ScheduleHideProgressAsync();
                    }
                }

                // Queue display
                if (detail.QueueTotal > 1)
                {
                    IsQueueVisible = true;
                    QueueStatusText = $"{detail.QueueCurrent} / {detail.QueueTotal}";
                    QueueNextItemName = !string.IsNullOrEmpty(detail.QueueNextItemName)
                        ? $"Next: {detail.QueueNextItemName}"
                        : string.Empty;
                }
                else
                {
                    IsQueueVisible = false;
                    QueueStatusText = string.Empty;
                    QueueNextItemName = string.Empty;
                }
            }
        });
    }

    /// <summary>
    /// Hides the TaskProgressControl after a 2-second delay, unless a new task starts.
    /// </summary>
    private async Task ScheduleHideProgressAsync()
    {
        _hideDelayCts?.Cancel();
        var cts = new CancellationTokenSource();
        _hideDelayCts = cts;

        try
        {
            await Task.Delay(2000, cts.Token).ConfigureAwait(false);
            _dispatcherService.RunOnUIThread(() =>
            {
                if (!_taskProgressService.IsTaskRunning)
                    IsLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            // A new task started before the delay expired -- do nothing
        }
    }
}
