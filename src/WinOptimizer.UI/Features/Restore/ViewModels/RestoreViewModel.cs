using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Restore.ViewModels;

/// <summary>One restore-point row with a shared, guarded delete command.</summary>
public sealed class RestorePointItem(SystemRestorePoint point, IAsyncRelayCommand<RestorePointItem?> deleteCommand,
    ILocalizationService localization) : ObservableObject
{
    public SystemRestorePoint Point { get; } = point;
    public string Description => Point.Description;
    public string Details => $"{Point.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)} · #{Point.SequenceNumber}";
    public string DeleteLabel => string.Format(localization.GetString("Restore_DeletePointLabel"), Description);
    public IAsyncRelayCommand<RestorePointItem?> DeleteCommand { get; } = deleteCommand;
    public void RefreshText() => OnPropertyChanged(string.Empty);
}

/// <summary>Coordinates configuration recovery and explicit restore-point management.</summary>
public partial class RestoreViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationService _configuration;
    private readonly ISystemBackupService _backup;
    private readonly ISystemRestoreService _restore;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly ITaskProgressService _tasks;
    private readonly IApplicationModeService _mode;
    private readonly ILogService _log;
    private readonly IDispatcherService _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private bool _disposed;

    public ObservableCollection<RestorePointItem> RestorePoints { get; } = [];
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsLoadingPoints { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNoPoints), nameof(PointCount))]
    public partial bool HasLoaded { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; private set; } = "";
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasNoPoints => HasLoaded && RestorePoints.Count == 0;
    public string PointCount => HasLoaded ? string.Format(Text("Restore_PointCount"), RestorePoints.Count) : "";
    public string Title => Text("Nav_Restore");
    public string Description => Text("Restore_Description");
    public string ConfigurationLabel => Text("Category_Configuration");
    public string BackupRestoreHeader => Text("Settings_BackupRestore_Title");
    public string BackupRestoreDescription => Text("Settings_BackupRestore_Description");
    public string ImportLabel => Text("Button_Import");
    public string ExportLabel => Text("Button_Export");
    public string DefaultsTitle => Text("Restore_DefaultsTitle");
    public string DefaultsDescription => Text("Restore_DefaultsDescription");
    public string DefaultsLabel => Text("Restore_DefaultsButton");
    public string SystemProtectionLabel => Text("Category_SystemProtection");
    public string RestorePointHeader => Text("Settings_SystemProtection_Title");
    public string RestorePointDescription => Text("Settings_SystemProtection_Description");
    public string CreateLabel => Text("Settings_CreateRestorePoint_Button");
    public string PointsLabel => Text("Restore_PointsTitle");
    public string RefreshLabel => Text("Restore_Refresh");
    public string EmptyLabel => Text("Restore_NoPoints");
    private string Text(string key) => _localization.GetString(key);
    private bool CanReadOrExport() => !IsBusy && !_tasks.IsTaskRunning;
    private bool CanModify() => CanReadOrExport() && _mode.CurrentMode != WinOptimizerMode.Builder;

    public RestoreViewModel(IConfigurationService configuration, ISystemBackupService backup,
        ISystemRestoreService restore, IDialogService dialogs, ILocalizationService localization,
        ITaskProgressService tasks, IApplicationModeService mode, ILogService log, IDispatcherService dispatcher)
    {
        _configuration = configuration; _backup = backup; _restore = restore; _dialogs = dialogs;
        _localization = localization; _tasks = tasks; _mode = mode; _log = log; _dispatcher = dispatcher;
        _lifetimeToken = _lifetime.Token;
        _localization.LanguageChanged += OnLanguageChanged;
        _mode.ModeChanged += OnModeChanged;
        _tasks.ProgressUpdated += OnTaskProgress;
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    private void RefreshCommands()
    {
        ImportConfigCommand.NotifyCanExecuteChanged(); ExportConfigCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged(); CreateRestorePointCommand.NotifyCanExecuteChanged();
        DeleteRestorePointCommand.NotifyCanExecuteChanged(); RefreshRestorePointsCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart(bool modifiesSystem)
    {
        if (_disposed) return false;
        if (modifiesSystem && _mode.CurrentMode == WinOptimizerMode.Builder)
        { Status = Text("TrialMode_Blocked"); return false; }
        if (!CanReadOrExport()) { Status = Text("Restore_Wait"); return false; }
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task ImportConfigAsync() => RunAsync(_configuration.ImportConfigurationAsync, modifiesSystem: true);
    [RelayCommand(CanExecute = nameof(CanReadOrExport))]
    private Task ExportConfigAsync() => RunAsync(_configuration.ExportConfigurationAsync, modifiesSystem: false);
    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task RestoreDefaultsAsync() => RunAsync(_configuration.RestoreWindowsDefaultsAsync, modifiesSystem: true);

    [RelayCommand(CanExecute = nameof(CanReadOrExport))]
    private Task RefreshRestorePointsAsync() => RunAsync(LoadPointsAsync, modifiesSystem: false);

    /// <summary>Publishes a complete, newest-first inventory after the Windows query succeeds.</summary>
    private async Task LoadPointsAsync()
    {
        _lifetimeToken.ThrowIfCancellationRequested();
        IReadOnlyList<SystemRestorePoint> points;
        IsLoadingPoints = true;
        try { points = await _restore.GetRestorePointsAsync(_lifetimeToken); }
        finally { IsLoadingPoints = false; }
        _lifetimeToken.ThrowIfCancellationRequested();
        RestorePoints.Clear();
        foreach (var point in points.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.SequenceNumber))
            RestorePoints.Add(new(point, DeleteRestorePointCommand, _localization));
        HasLoaded = true;
        OnPropertyChanged(nameof(HasNoPoints)); OnPropertyChanged(nameof(PointCount));
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task CreateRestorePointAsync() => RunAsync(async () =>
    {
        var result = await RunWindowsTaskAsync(ct =>
            _backup.CreateRestorePointAsync(progress: _tasks.CreateDetailedProgress(), cancellationToken: ct));
        if (!result.Success || !result.RestorePointCreated)
        {
            Status = $"{Text("Settings_RestorePoint_Fail")} {result.ErrorMessage}".Trim();
            await _dialogs.ShowWarningAsync(Status);
            return;
        }
        Status = Text("Settings_RestorePoint_Success");
        await LoadPointsAsync();
        await _dialogs.ShowInformationAsync(Status);
    }, modifiesSystem: true);

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task DeleteRestorePointAsync(RestorePointItem? item) => RunAsync(async () =>
    {
        if (item is null || !RestorePoints.Contains(item)) return;
        var confirmation = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = Text("Restore_DeleteTitle"),
            Message = string.Format(Text("Restore_DeleteConfirm"), item.Description, item.Details),
            ConfirmButtonText = Text("Restore_DeleteButton"), CancelButtonText = Text("Button_Cancel")
        });
        if (!confirmation.Confirmed) return;
        var result = await RunWindowsTaskAsync(ct => _restore.DeleteRestorePointAsync(item.Point.SequenceNumber, ct));
        if (!result.Success) throw new InvalidOperationException(result.ErrorMessage);
        RestorePoints.Remove(item);
        OnPropertyChanged(nameof(HasNoPoints)); OnPropertyChanged(nameof(PointCount));
        Status = Text("Restore_Deleted");
    }, modifiesSystem: true);

    /// <summary>Pairs global progress ownership with the lifetime of one Windows operation.</summary>
    private async Task<T> RunWindowsTaskAsync<T>(Func<CancellationToken, Task<T>> operation)
    {
        if (_tasks.IsTaskRunning) throw new InvalidOperationException(Text("Restore_Wait"));
        var cts = _tasks.StartTask(Title, isIndeterminate: true);
        try { return await operation(cts.Token); }
        finally { _tasks.CompleteTask(); }
    }

    /// <summary>Serializes page actions, reserves system writes, and exposes actionable failures.</summary>
    private async Task RunAsync(Func<Task> operation, bool modifiesSystem)
    {
        if (!CanStart(modifiesSystem)) return;
        IsBusy = true;
        Status = "";
        try
        {
            using var reservation = modifiesSystem ? _mode.BeginSystemOperation() : null;
            await operation();
        }
        catch (OperationCanceledException) { Status = Text("Restore_Cancelled"); }
        catch (Exception ex)
        {
            _log.LogError($"Restore: {ex.Message}", ex);
            Status = ex.Message;
            await _dialogs.ShowErrorAsync(ex.Message, Title);
        }
        finally { IsBusy = false; }
    }

    private void OnModeChanged(object? sender, EventArgs e) => RefreshCommands();
    private void OnTaskProgress(object? sender, TaskProgressDetail detail) => _dispatcher.RunOnUIThread(RefreshCommands);
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
        foreach (var point in RestorePoints) point.RefreshText();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _localization.LanguageChanged -= OnLanguageChanged;
        _mode.ModeChanged -= OnModeChanged;
        _tasks.ProgressUpdated -= OnTaskProgress;
        _lifetime.Dispose();
    }
}
