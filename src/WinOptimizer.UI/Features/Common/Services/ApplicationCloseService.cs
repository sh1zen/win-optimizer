using System;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.UI.Features.Common.Services;

/// <summary>Confirms active operations and completes application shutdown.</summary>
public class ApplicationCloseService : IApplicationCloseService
{
    private readonly ILogService _logService;
    private readonly ITaskProgressService _taskProgressService;
    private readonly IDialogService _dialogService;

    public Func<Task>? BeforeShutdown { get; set; }

    // Terminates the process. Replaced in tests so the test host survives.
    public Action ShutdownAction { get; set; } = DefaultShutdown;
    /// <summary>Exits for installation even when normal closing retains a desktop widget.</summary>
    public Action RestartShutdownAction { get; set; } = DefaultShutdown;
    private Task<OperationResult>? _pendingClose;

    private static void DefaultShutdown()
    {
        try
        {
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    public ApplicationCloseService(
        ILogService logService,
        ITaskProgressService taskProgressService,
        IDialogService dialogService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _taskProgressService = taskProgressService ?? throw new ArgumentNullException(nameof(taskProgressService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public Task<OperationResult> CheckOperationsAndCloseAsync() => StartCloseAsync(null);

    public Task<OperationResult> CheckOperationsAndRestartAsync(Action launchInstaller)
    {
        ArgumentNullException.ThrowIfNull(launchInstaller);
        return StartCloseAsync(launchInstaller);
    }

    private Task<OperationResult> StartCloseAsync(Action? launchInstaller) =>
        _pendingClose is { IsCompleted: false } pending ? pending : _pendingClose = CloseCoreAsync(launchInstaller);

    /// <summary>Obtains consent before cleanup and waits for the captured operation to release its resources.</summary>
    private async Task<OperationResult> CloseCoreAsync(Action? launchInstaller)
    {
        try
        {
            CancellationTokenSource? operation = null;
            if (_taskProgressService.IsTaskRunning)
            {
                operation = _taskProgressService.CurrentTaskCancellationSource;
                string currentOperation = _taskProgressService.CurrentStatusText ?? "an operation";

                _logService.LogInformation($"Close requested while operation in progress: {currentOperation}");

                var confirmed = (await _dialogService.ShowConfirmationAsync(new ConfirmationRequest
                {
                    Message = $"The following operation is still running:\n\n{currentOperation}\n\n" +
                              $"The app will wait for cleanup and pending saves to finish.\n\n" +
                              $"Cancel this operation and close win-optimizer?",
                    Title = "Warning: Operation in Progress",
                    ConfirmButtonText = "Yes, Close",
                    CancelButtonText = "Cancel",
                })).Confirmed;

                if (!confirmed)
                {
                    _logService.LogInformation("User cancelled application close due to running operation");
                    return OperationResult.Failed("User cancelled application close");
                }

                _logService.LogInformation("User confirmed close, cancelling operation...");
                // A prompt can overlap a task transition; cancellation never targets the replacement.
                if (_taskProgressService.IsTaskRunning)
                {
                    if (ReferenceEquals(operation, _taskProgressService.CurrentTaskCancellationSource))
                        _taskProgressService.CancelCurrentTask();
                    else return OperationResult.Failed("The active operation changed. Try closing again.");
                }
            }

            if (BeforeShutdown is not null) await BeforeShutdown.Invoke();
            if (operation is not null) await WaitForOperationAsync(operation);
            if (_taskProgressService.IsTaskRunning && !ReferenceEquals(operation, _taskProgressService.CurrentTaskCancellationSource))
                return OperationResult.Failed("Another operation started. Try closing again.");
            launchInstaller?.Invoke();
            CloseApplication(launchInstaller is null ? ShutdownAction : RestartShutdownAction);
            return OperationResult.Succeeded();
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error in CheckOperationsAndCloseAsync: {ex.Message}", ex);

            return OperationResult.Failed(ex.Message, ex);
        }
    }

    /// <summary>Observes completion without polling or cancelling work that replaces the captured task.</summary>
    private async Task WaitForOperationAsync(CancellationTokenSource owner)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, TaskProgressDetail detail)
        {
            if (!_taskProgressService.IsTaskRunning || !ReferenceEquals(owner, _taskProgressService.CurrentTaskCancellationSource))
                completion.TrySetResult();
        }
        _taskProgressService.ProgressUpdated += Check;
        try
        {
            Check(null, new TaskProgressDetail());
            await completion.Task;
        }
        finally { _taskProgressService.ProgressUpdated -= Check; }
    }

    /// <summary>Invokes the selected desktop lifetime strategy after successful cleanup.</summary>
    private void CloseApplication(Action shutdown)
    {
        _logService.LogInformation("Shutting down application");
        shutdown();
    }
}
