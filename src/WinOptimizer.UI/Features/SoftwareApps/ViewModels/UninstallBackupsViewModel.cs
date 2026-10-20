using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.UI.Features.SoftwareApps.ViewModels;

public sealed record UninstallBackupItem(UninstallCleanupBackup Backup)
{
    public string Title => $"{Backup.CreatedUtc.ToLocalTime():g} — {Backup.Applications} ({Backup.Items:N0})";
}

/// <summary>Offers durable uninstall cleanup recovery after the original review/session has closed.</summary>
public sealed partial class UninstallBackupsViewModel(IUninstallResidueService service, ITaskProgressService tasks,
    ILocalizationService text) : ObservableObject
{
    private CancellationTokenSource? _operation;
    [ObservableProperty] public partial IReadOnlyList<UninstallBackupItem> Items { get; set; } = [];
    [ObservableProperty] public partial UninstallBackupItem? SelectedItem { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsConfirming { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    public bool CanClose => !IsBusy;
    public bool CanRestore => SelectedItem != null && !IsBusy && !IsConfirming;
    public bool CanConfirm => SelectedItem != null && !IsBusy && IsConfirming;
    public string RestoreLabel => text.GetString("UninstallResidue_Restore");
    public string ConfirmLabel => text.GetString("UninstallResidue_Confirm");
    public string CancelLabel => text.GetString("Button_Cancel");
    public string Confirmation => text.GetString("UninstallResidue_ConfirmRestore");
    public string Description => text.GetString("UninstallResidue_BackupDescription");

    partial void OnSelectedItemChanged(UninstallBackupItem? value) { IsConfirming = false; Refresh(); }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanClose)); Refresh(); }
    partial void OnIsConfirmingChanged(bool value) => Refresh();
    private void Refresh() { RequestRestoreCommand.NotifyCanExecuteChanged(); ConfirmCommand.NotifyCanExecuteChanged(); }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Items = (await service.GetBackupsAsync(CancellationToken.None)).Select(backup => new UninstallBackupItem(backup)).ToArray();
            if (Items.Count == 0) Status = text.GetString("UninstallResidue_NoBackups");
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void RequestRestore() => IsConfirming = true;
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) _operation?.Cancel();
        else IsConfirming = false;
    }
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (SelectedItem == null) return;
        IsBusy = true;
        var cancellation = tasks.StartTask(text.GetString("UninstallResidue_Restoring"), true);
        _operation = cancellation;
        try
        {
            var result = await service.RestoreAsync(SelectedItem.Backup.Id, cancellation.Token);
            Status = text.GetString("UninstallResidue_Restored", result.Restored) + "\n" + string.Join("\n", result.Errors);
        }
        catch (OperationCanceledException) { Status = text.GetString("UninstallResidue_Cancelled"); }
        catch (Exception ex) { Status = ex.Message; }
        finally { _operation = null; tasks.CompleteTask(); IsConfirming = false; IsBusy = false; }
    }
}
