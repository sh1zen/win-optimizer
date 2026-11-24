using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinOptimizer.UI.Features.Common.Controls;
using WinOptimizer.UI.ViewModels;

namespace WinOptimizer.UI.Helpers;

/// <summary>Projects task progress state onto the main window footer.</summary>
internal sealed class TaskProgressCoordinator
{
    private readonly TaskProgressControl _control;
    private readonly DispatcherQueue _dispatcherQueue;

    public TaskProgressCoordinator(TaskProgressControl control, DispatcherQueue dispatcherQueue)
    {
        _control = control;
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// Handles TaskProgressViewModel property changes, updating the task progress footer.
    /// </summary>
    public void HandlePropertyChanged(TaskProgressViewModel tp, string? propertyName)
    {
        if (propertyName is nameof(TaskProgressViewModel.IsLoading) or nameof(TaskProgressViewModel.IsTaskRunning) or nameof(TaskProgressViewModel.IsTaskFailed) or nameof(TaskProgressViewModel.CanCancel))
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                var isActuallyRunning = tp.IsTaskRunning;

                if (tp.IsLoading)
                {
                    _control.IsProgressVisible = Visibility.Visible;
                    if (isActuallyRunning)
                    {
                        _control.CanCancel = tp.CanCancel ? Visibility.Visible : Visibility.Collapsed;
                        _control.IsTaskRunning = true;
                        _control.CancelCommand = tp.CancelCommand;
                        _control.CancelText = tp.CancelButtonLabel;
                    }
                    else if (tp.IsTaskFailed)
                    {
                        // Failure state: show Close button to dismiss the bar
                        _control.CanCancel = Visibility.Visible;
                        _control.IsTaskRunning = true;
                        _control.CancelCommand = tp.CloseFailedTaskCommand;
                        _control.CancelText = tp.CloseButtonLabel;
                    }
                    else
                    {
                        _control.CanCancel = Visibility.Collapsed;
                        _control.IsTaskRunning = false;
                    }
                }
                else
                {
                    _control.IsProgressVisible = Visibility.Collapsed;
                    _control.CanCancel = Visibility.Collapsed;
                    _control.IsTaskRunning = false;
                }
            });
        }
        else if (propertyName is nameof(TaskProgressViewModel.ProgressValue) or nameof(TaskProgressViewModel.IsIndeterminate))
        {
            _dispatcherQueue.TryEnqueue(() => { _control.ProgressValue = tp.ProgressValue; _control.IsIndeterminate = tp.IsIndeterminate; });
        }
        else if (propertyName == nameof(TaskProgressViewModel.AppName))
        {
            _dispatcherQueue.TryEnqueue(() => _control.AppName = tp.AppName);
        }
        else if (propertyName == nameof(TaskProgressViewModel.LastTerminalLine))
        {
            _dispatcherQueue.TryEnqueue(() => _control.LastTerminalLine = tp.LastTerminalLine);
        }
        else if (propertyName == nameof(TaskProgressViewModel.CancelButtonLabel))
        {
            _dispatcherQueue.TryEnqueue(() => _control.CancelText = tp.CancelButtonLabel);
        }
        else if (propertyName == nameof(TaskProgressViewModel.QueueStatusText))
        {
            _dispatcherQueue.TryEnqueue(() => _control.QueueStatusText = tp.QueueStatusText);
        }
        else if (propertyName == nameof(TaskProgressViewModel.QueueNextItemName))
        {
            _dispatcherQueue.TryEnqueue(() => _control.QueueNextItemName = tp.QueueNextItemName);
        }
        else if (propertyName == nameof(TaskProgressViewModel.IsQueueVisible))
        {
            _dispatcherQueue.TryEnqueue(() =>
                _control.IsQueueInfoVisible = tp.IsQueueVisible ? Visibility.Visible : Visibility.Collapsed);
        }
    }
}
