using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Disks;

/// <summary>Coordinates one disk operation, its cancellation and the app's system write reservation.</summary>
public sealed class DiskToolsViewModel(ILocalizationService localization, IApplicationModeService mode,
    ITaskProgressService tasks, ILogService log, string titleKey) : ObservableObject
{
    private CancellationTokenSource? _running;
    private Task _pendingOperation = Task.CompletedTask;
    private bool _writing;
    private bool _cancellable;
    private string _result = "";
    private InfoBarSeverity _severity;

    public bool IsBusy => _running != null;
    public bool CanInteract => !IsBusy;
    public bool CanWrite => CanInteract && mode.CurrentMode == WinOptimizerMode.Normal;
    public bool CanCancel => IsBusy && _cancellable && !_running!.IsCancellationRequested;
    public bool HasResult => Result.Length != 0;
    public string Result => _result;
    public InfoBarSeverity Severity => _severity;
    public Task WaitForOperationAsync() => _pendingOperation;
    public Task WaitForWriteAsync() => _writing ? _pendingOperation : Task.CompletedTask;

    /// <summary>Executes an action and reports its outcome inline.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> action, bool write = false, bool cancellable = true, bool clearResult = true)
    {
        if (IsBusy || (write && !CanWrite)) return;
        if (write && tasks.IsTaskRunning)
        {
            Report(localization.GetString("Disks_Wait"), InfoBarSeverity.Warning);
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingOperation = completion.Task;
        _writing = write;
        try
        {
            using var reservation = write ? mode.BeginNormalModeSystemOperation() : null;
            _running = write
                ? tasks.StartTask(localization.GetString(titleKey), isIndeterminate: true, showInTaskBar: true, canCancel: cancellable)
                : new CancellationTokenSource();
            _cancellable = cancellable;
            if (clearResult) Report("");
            RefreshAvailability();
            await action(cancellable ? _running.Token : CancellationToken.None);
        }
        catch (OperationCanceledException) when (_running?.IsCancellationRequested == true)
        {
            Report(localization.GetString("Disks_Cancelled"));
        }
        catch (Exception error)
        {
            log.LogError("Disk operation failed.", error);
            Report(error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (write) tasks.CompleteOwnedTask(_running);
            else _running?.Dispose();
            _running = null;
            _writing = false;
            RefreshAvailability();
            completion.TrySetResult();
        }
    }

    public void Cancel()
    {
        if (!CanCancel) return;
        _running!.Cancel();
        OnPropertyChanged(nameof(CanCancel));
    }

    public void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _result = message;
        _severity = severity;
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(HasResult));
    }

    public void RefreshAvailability()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanWrite));
        OnPropertyChanged(nameof(CanCancel));
    }
}
