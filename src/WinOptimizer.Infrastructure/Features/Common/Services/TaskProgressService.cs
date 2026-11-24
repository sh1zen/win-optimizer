using System;
using System.Collections.Generic;
using System.Threading;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Helpers;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>
/// Service that manages task progress reporting across the application.
/// </summary>
public class TaskProgressService : ITaskProgressService
{
    private const int MaxTerminalOutputLines = 10_000;

    private readonly ILogService _logService;
    private int _currentProgress;
    private string _currentStatusText;
    private bool _isTaskRunning;
    private bool _isIndeterminate;
    private bool _showInTaskBar = true;
    private bool _canCancel = true;
    private List<string> _terminalOutputLines = new List<string>();
    private bool _lastTerminalLineWasProgress;
    private CancellationTokenSource? _cancellationSource;

    // Queue sticky state
    private int _queueTotal;
    private int _queueCurrent;
    private string? _queueNextItemName;

    // Skip-next flag
    private volatile bool _skipNextRequested;

    /// <summary>
    /// Gets whether a task is currently running.
    /// </summary>
    public bool IsTaskRunning => _isTaskRunning;

    /// <summary>
    /// Gets the current progress value (0-100).
    /// </summary>
    public int CurrentProgress => _currentProgress;

    /// <summary>
    /// Gets the current status text.
    /// </summary>
    public string CurrentStatusText => _currentStatusText;

    /// <summary>
    /// Gets whether the current task is in indeterminate mode.
    /// </summary>
    public bool IsIndeterminate => _isIndeterminate;

    /// <summary>
    /// Gets the cancellation token source for the current task.
    /// </summary>
    public CancellationTokenSource? CurrentTaskCancellationSource => _cancellationSource;

    /// <summary>
    /// Event raised when progress changes.
    /// </summary>
    public event EventHandler<TaskProgressDetail>? ProgressUpdated;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskProgressService"/> class.
    /// </summary>
    /// <param name="logService">The log service.</param>
    public TaskProgressService(ILogService logService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _currentProgress = 0;
        _currentStatusText = string.Empty;
        _isTaskRunning = false;
        _isIndeterminate = false;
    }

    /// <summary>
    /// Starts a new task with the specified name.
    /// </summary>
    /// <param name="taskName">The name of the task.</param>
    /// <param name="isIndeterminate">Whether the task progress is indeterminate.</param>
    /// <returns>A cancellation token source for the task.</returns>
    public CancellationTokenSource StartTask(string taskName, bool isIndeterminate = false) =>
        StartTask(taskName, isIndeterminate, showInTaskBar: true);

    /// <summary>Owns task lifecycle and carries its presentation choice through updates and completion.</summary>
    public CancellationTokenSource StartTask(string taskName, bool isIndeterminate, bool showInTaskBar) =>
        StartTask(taskName, isIndeterminate, showInTaskBar, canCancel: true);

    /// <summary>Starts one task and retains its presentation and cancellation capabilities through completion.</summary>
    public CancellationTokenSource StartTask(string taskName, bool isIndeterminate, bool showInTaskBar, bool canCancel)
    {
        // Cancel any existing task
        CancelCurrentTask();

        if (string.IsNullOrEmpty(taskName))
        {
            throw new ArgumentException("Task name cannot be null or empty.", nameof(taskName));
        }

        _cancellationSource = new CancellationTokenSource();
        _currentProgress = 0;
        _currentStatusText = taskName;
        _isTaskRunning = true;
        _isIndeterminate = isIndeterminate;
        _showInTaskBar = showInTaskBar;
        _canCancel = canCancel;
        _terminalOutputLines.Clear();
        _lastTerminalLineWasProgress = false;
        _queueTotal = 0;
        _queueCurrent = 0;
        _queueNextItemName = null;
        _skipNextRequested = false;

        _logService.Log(LogLevel.Info, $"[TASKPROGRESSSERVICE] Task started: {taskName}"); // Corrected Log call
        OnProgressChanged(
            new TaskProgressDetail
            {
                Progress = 0,
                StatusText = taskName,
                IsIndeterminate = isIndeterminate,
            }
        );

        return _cancellationSource;
    }

    /// <summary>
    /// Updates the progress of the current task.
    /// </summary>
    /// <param name="progressPercentage">The progress percentage (0-100).</param>
    /// <param name="statusText">The status text.</param>
    public void UpdateProgress(int progressPercentage, string? statusText = null)
    {
        if (!_isTaskRunning)
        {
            return;
        }

        if (progressPercentage < 0 || progressPercentage > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(progressPercentage),
                "Progress must be between 0 and 100."
            );
        }

        _currentProgress = progressPercentage;
        if (!string.IsNullOrEmpty(statusText))
        {
            _currentStatusText = statusText;
            _logService.Log(
                LogLevel.Info,
                $"Task progress ({progressPercentage}%): {statusText}"
            ); // Corrected Log call
        }
        else
        {
            _logService.Log(LogLevel.Info, $"Task progress: {progressPercentage}%"); // Corrected Log call
        }
        OnProgressChanged(
            new TaskProgressDetail
            {
                Progress = progressPercentage,
                StatusText = _currentStatusText,
                LogLevel = progressPercentage == 0 ? LogLevel.Error : LogLevel.Info,
            }
        );
    }

    /// <summary>
    /// Updates the progress with detailed information.
    /// </summary>
    /// <param name="detail">The detailed progress information.</param>
    public void UpdateDetailedProgress(TaskProgressDetail detail)
    {
        if (!_isTaskRunning)
        {
            return;
        }

        if (detail.Progress.HasValue)
        {
            if (detail.Progress.Value < 0 || detail.Progress.Value > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(detail.Progress),
                    "Progress must be between 0 and 100."
                );
            }

            _currentProgress = (int)detail.Progress.Value;
        }

        if (!string.IsNullOrEmpty(detail.StatusText))
        {
            _currentStatusText = detail.StatusText;
        }

        _isIndeterminate = detail.IsIndeterminate;

        // Accumulate terminal output lines for the details dialog,
        // filtering out noise (blank lines).
        // Always remove the last progress line
        // before adding ANY new line. This handles:
        //   progress → progress: replacement (progress bar filling)
        //   progress → permanent: cleanup (stale progress/spinner removed)
        //   permanent → permanent: normal append
        //   permanent → progress: normal append
        if (!string.IsNullOrEmpty(detail.TerminalOutput))
        {
            if (IsTerminalNoise(detail.TerminalOutput))
            {
                detail.TerminalOutput = null; // Suppress noise from event subscribers
            }
            else
            {
                if (_lastTerminalLineWasProgress && _terminalOutputLines.Count > 0)
                {
                    _terminalOutputLines.RemoveAt(_terminalOutputLines.Count - 1);
                }
                else if (detail.IsProgressIndicator && _terminalOutputLines.Count > 0)
                {
                    // The first progress bar sometimes arrives as a permanent line
                    // (winget's initial render uses \n before switching to \r).
                    // Detect and remove it so it doesn't duplicate.
                    var lastLine = _terminalOutputLines[_terminalOutputLines.Count - 1];
                    if (TerminalProgressBar.IsProgressLine(lastLine))
                    {
                        _terminalOutputLines.RemoveAt(_terminalOutputLines.Count - 1);
                    }
                }
                AddTerminalOutputLine(detail.TerminalOutput);
                _lastTerminalLineWasProgress = detail.IsProgressIndicator;
            }
        }

        if (!string.IsNullOrEmpty(detail.DetailedMessage))
        {
            _logService.Log(detail.LogLevel, detail.DetailedMessage); // Corrected Log call
        }
        OnProgressChanged(detail);
    }

    /// <summary>
    /// Completes the current task.
    /// </summary>
    public void CompleteTask()
    {
        if (!_isTaskRunning)
        {
            return;
        }

        _currentProgress = 100;

        _isTaskRunning = false;
        _isIndeterminate = false;
        _queueTotal = 0;
        _queueCurrent = 0;
        _queueNextItemName = null;
        _skipNextRequested = false;

        _logService.Log(LogLevel.Info, $"Task completed: {_currentStatusText}"); // Corrected Log call

        OnProgressChanged(
            new TaskProgressDetail
            {
                Progress = 100,
                StatusText = _currentStatusText,
                DetailedMessage = "Task completed",
                IsCompletion = true,
            }
        );

        // Dispose cancellation token source
        _cancellationSource?.Dispose();
        _cancellationSource = null;
    }

    /// <summary>
    /// Gets a snapshot of all terminal output lines accumulated during the current (or last) task.
    /// These are the raw output lines from winget/process stdout.
    /// </summary>
    public IReadOnlyList<string> GetTerminalOutputLines() => _terminalOutputLines.AsReadOnly();

    /// <summary>
    /// Cancels the current task.
    /// </summary>
    public void CancelCurrentTask()
    {
        if (_cancellationSource != null && !_cancellationSource.IsCancellationRequested)
        {
            _cancellationSource.Cancel();
        }
    }

    /// <summary>
    /// Creates a progress reporter for detailed progress.
    /// </summary>
    /// <returns>The progress reporter.</returns>
    public IProgress<TaskProgressDetail> CreateDetailedProgress()
    {
        var owner = _cancellationSource;
        return new Progress<TaskProgressDetail>(detail =>
        {
            // Check on dispatch, including callbacks queued before completion or replacement.
            if (owner is not null && ReferenceEquals(owner, _cancellationSource)) UpdateDetailedProgress(detail);
        });
    }

    /// <summary>
    /// Creates a progress reporter for PowerShell progress.
    /// </summary>
    /// <returns>The progress reporter.</returns>
    public IProgress<TaskProgressDetail> CreatePowerShellProgress() => CreateDetailedProgress();

    /// <summary>
    /// Checks and clears the skip-next flag (atomic check-and-clear).
    /// </summary>
    /// <returns>True if a skip was requested since the last call.</returns>
    public bool ConsumeSkipNextRequest()
    {
        if (!_skipNextRequested) return false;
        _skipNextRequested = false;
        return true;
    }

    /// <summary>
    /// Raises the ProgressUpdated event, applying sticky queue state.
    /// </summary>
    protected virtual void OnProgressChanged(TaskProgressDetail detail)
    {
        detail.ShowInTaskBar = _showInTaskBar;
        detail.CanCancel = _canCancel;

        // Update sticky queue state if incoming detail has queue info
        if (detail.QueueTotal > 0)
        {
            _queueTotal = detail.QueueTotal;
            _queueCurrent = detail.QueueCurrent;
            _queueNextItemName = detail.QueueNextItemName;
        }

        // Always populate queue info from sticky state if we're in a queue
        if (_queueTotal > 1)
        {
            detail.QueueTotal = _queueTotal;
            detail.QueueCurrent = _queueCurrent;
            detail.QueueNextItemName = _queueNextItemName;
        }

        ProgressUpdated?.Invoke(this, detail);
    }

    /// <summary>
    /// Returns true if the line is noise that doesn't add value
    /// (e.g. blank/whitespace-only lines).
    /// Spinner characters (-, \, |, /) are NOT filtered — they are
    /// delivered as IsProgressIndicator=true and animate in-place via
    /// the removal pattern in the live terminal dialog.
    /// </summary>
    private static bool IsTerminalNoise(string line)
    {
        var trimmed = line.Trim();
        return string.IsNullOrEmpty(trimmed);
    }

    /// <summary>
    /// Adds a terminal output line, enforcing a maximum capacity to prevent unbounded growth.
    /// When the limit is reached, the oldest 10% of lines are discarded to make room.
    /// </summary>
    private void AddTerminalOutputLine(string line)
    {
        if (_terminalOutputLines.Count >= MaxTerminalOutputLines)
        {
            var removeCount = MaxTerminalOutputLines / 10;
            _terminalOutputLines.RemoveRange(0, removeCount);
        }
        _terminalOutputLines.Add(line);
    }
}
