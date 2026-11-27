using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Disks;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Disks;

/// <summary>Presents the form for one disk tool selected in the shell's navigation journal.</summary>
public sealed partial class DiskToolPage : Page
{
    private readonly ILocalizationService _localization = App.Services.GetRequiredService<ILocalizationService>();
    private readonly IApplicationModeService _mode = App.Services.GetRequiredService<IApplicationModeService>();
    private readonly IDiskToolsService _tools = App.Services.GetRequiredService<IDiskToolsService>();
    private readonly IFilePickerService _picker = App.Services.GetRequiredService<IFilePickerService>();
    private readonly IDialogService _dialogs = App.Services.GetRequiredService<IDialogService>();

    private DiskTool _tool;
    public DiskToolsViewModel ViewModel { get; private set; } = null!;
    public string ToolTitle => T(_tool.TitleKey());
    public string ToolDescription => T(_tool.DescriptionKey());
    public Icon ToolIcon => _tool.GetIcon();

    public DiskToolPage()
    {
        InitializeComponent();
        PageScrollHelper.Attach(this, PageScroll);
        TargetPath.TextChanged += TargetPath_Changed;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    public string T(string key) => _localization.GetString(key);
    private string T(string key, params object[] values) => _localization.GetString(key, values);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not DiskTool tool)
            throw new ArgumentException("A disk tool is required.", nameof(e));

        _tool = tool;
        ViewModel = App.Services.GetRequiredService<DiskToolSessions>().Get(tool);
        ShowToolForm();
        base.OnNavigatedTo(e);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged += Language_Changed;
        _mode.ModeChanged += Mode_Changed;
        Bindings.Update();
        ApplyMode();
        await ViewModel.WaitForOperationAsync();
        if (IsLoaded) await RefreshInventoryAsync(preserveResult: true);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged -= Language_Changed;
        _mode.ModeChanged -= Mode_Changed;
    }

    private void Language_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => Bindings.Update());
    private void Mode_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyMode);

    private void ApplyMode()
    {
        ViewModel.RefreshAvailability();
        ModeMessage.Visibility = Visible(_tool != DiskTool.Hash && _mode.CurrentMode != WinOptimizerMode.Normal);
    }

    private void ShowToolForm()
    {
        var tool = _tool;
        PathPanel.Visibility = Visible(tool is DiskTool.Delete or DiskTool.Permissions or DiskTool.Shadow or DiskTool.Hash);
        FolderPicker.Visibility = Visible(tool != DiskTool.Delete);
        DeletePanel.Visibility = Visible(tool == DiskTool.Delete);
        PermissionsPanel.Visibility = Visible(tool == DiskTool.Permissions);
        ShadowPanel.Visibility = Visible(tool == DiskTool.Shadow);
        HashPanel.Visibility = Visible(tool == DiskTool.Hash);
        UsbPanel.Visibility = Visible(tool == DiskTool.Usb);
        RecoveryPanel.Visibility = Visible(tool == DiskTool.Recovery);
        OptimizePanel.Visibility = Visible(tool == DiskTool.Optimize);
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        if (_picker.PickFile([T("Disks_AllFiles"), "*.*"]) is { } path) TargetPath.Text = path;
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_picker.PickFolder(T("Disks_SelectFolder")) is { } path) TargetPath.Text = path;
    }

    private void TargetPath_Changed(object sender, TextChangedEventArgs e)
    {
        VersionSelector.ItemsSource = null;
        ViewModel.Report("");
    }

    private async void ListLocks_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePath(out var path)) return;
        await ViewModel.RunAsync(async ct =>
        {
            var locks = await _tools.GetFileLocksAsync(path, ct);
            ViewModel.Report(locks.Count == 0 ? T("Disks_NoLocks") : T("Disks_LockResult", FormatProcesses(locks)));
        });
    }

    private async void ForceDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePath(out var path) || !await ConfirmAsync("Disks_ForceDelete", "Disks_DeleteConfirm", path)) return;
        await ViewModel.RunAsync(async ct =>
        {
            await _tools.DeleteFileAsync(path, ct);
            ViewModel.Report(T("Disks_FileDeleted", path), InfoBarSeverity.Success);
        }, write: true, cancellable: false);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePath(out var path) || !await ConfirmAsync("Disks_Unlock", "Disks_UnlockConfirm", path)) return;
        await ViewModel.RunAsync(async ct =>
        {
            var unlocked = await _tools.UnlockFileAsync(path, ct);
            ViewModel.Report(unlocked ? T("Disks_Unlocked", path) : T("Disks_NoLocks"),
                unlocked ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }, write: true, cancellable: false);
    }

    private async void RepairPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePath(out var path) || !await ConfirmAsync("Disks_ResetPermissions", "Disks_PermissionsConfirm", path)) return;
        await ViewModel.RunAsync(async ct =>
        {
            await _tools.RepairPermissionsAsync(path, ct);
            ViewModel.Report(T("Disks_PermissionsReset", path), InfoBarSeverity.Success);
        }, write: true, cancellable: false);
    }

    private async void FindVersions_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePath(out var path)) return;
        await ViewModel.RunAsync(async ct =>
        {
            VersionSelector.ItemsSource = null;
            var versions = await _tools.GetShadowVersionsAsync(path, ct);
            VersionSelector.ItemsSource = versions.Select(version => new VersionChoice(version,
                T(version.IsDirectory ? "Disks_FolderVersion" : "Disks_FileVersion", version.Created))).ToArray();
            VersionSelector.SelectedIndex = versions.Count > 0 ? 0 : -1;
            if (versions.Count == 0) ViewModel.Report(T("Disks_NoVersions"));
        });
    }

    private async void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePath(out var path)) return;
        if (VersionSelector.SelectedItem is not VersionChoice choice)
        {
            ViewModel.Report(T("Disks_SelectVersion"), InfoBarSeverity.Warning);
            return;
        }
        if (_picker.PickFolder(T("Disks_RestoreDestination")) is not { } folder) return;
        await ViewModel.RunAsync(async ct =>
        {
            var restored = await _tools.RestoreShadowVersionAsync(path, choice.Version, folder, ct);
            ViewModel.Report(T("Disks_Restored", restored), InfoBarSeverity.Success);
        }, write: true);
    }

    private async void CalculateHash_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePath(out var path)) return;
        var expected = ExpectedHash.Text;
        await ViewModel.RunAsync(async ct =>
        {
            var digest = await _tools.HashAsync(path, expected, ct);
            var message = T("Disks_HashResult", digest.Value);
            if (digest.Matches is { } matches)
                message += Environment.NewLine + T(matches ? "Disks_HashMatch" : "Disks_HashMismatch");
            ViewModel.Report(message, digest.Matches switch
            {
                true => InfoBarSeverity.Success,
                false => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational
            });
        });
    }

    private async void Eject_Click(object sender, RoutedEventArgs e)
    {
        if (UsbSelector.SelectedItem is not UsbDisk disk)
        {
            ViewModel.Report(T("Disks_UsbDevice"), InfoBarSeverity.Warning);
            return;
        }
        await ViewModel.RunAsync(async ct =>
        {
            var result = await _tools.EjectUsbAsync(disk, ct);
            if (result.Ejected)
            {
                UsbSelector.ItemsSource = ((IEnumerable<UsbDisk>)UsbSelector.ItemsSource).Where(item => item != disk).ToArray();
                UsbSelector.SelectedIndex = UsbSelector.Items.Count > 0 ? 0 : -1;
                ViewModel.Report(T("Disks_Ejected", disk), InfoBarSeverity.Success);
                return;
            }
            var message = T("Disks_EjectFailed", $"0x{result.WindowsCode:X}", result.VetoType);
            if (result.Blockers.Count > 0)
                message += Environment.NewLine + FormatProcesses(result.Blockers);
            else if (result.VetoName is { } veto)
                message += Environment.NewLine + veto;
            else
                message += Environment.NewLine + T("Disks_NoUsbProcess");
            ViewModel.Report(message, InfoBarSeverity.Warning);
        }, write: true, cancellable: false);
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (RecoverySource.SelectedItem is not string source || RecoveryDestination.SelectedItem is not string destination ||
            string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.Report(T("Disks_SelectRecoveryVolumes"), InfoBarSeverity.Warning);
            return;
        }
        var mode = RecoveryDepth.SelectedIndex == 1 ? DiskRecoveryMode.Extensive : DiskRecoveryMode.Regular;
        var filter = RecoveryFilter.Text;
        await ViewModel.RunAsync(async ct =>
        {
            ViewModel.Report(T("Disks_RecoveryInConsole"));
            var results = await _tools.RecoverDeletedFilesAsync(source, destination, mode, filter, ct);
            ViewModel.Report(results.Count == 0 ? T("Disks_NoRecoveryFolders") :
                T("Disks_RecoveryFinished", string.Join(Environment.NewLine, results)));
        }, write: true, cancellable: false);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await RunOptimizationAsync(analyzeOnly: true);
    private async void Optimize_Click(object sender, RoutedEventArgs e) => await RunOptimizationAsync(analyzeOnly: false);

    private async Task RunOptimizationAsync(bool analyzeOnly)
    {
        if (OptimizeVolume.SelectedItem is not string volume)
        {
            ViewModel.Report(T("Disks_SelectVolume"), InfoBarSeverity.Warning);
            return;
        }
        await ViewModel.RunAsync(async ct =>
        {
            var output = await _tools.OptimizeAsync(volume, analyzeOnly, ct);
            ViewModel.Report(string.IsNullOrWhiteSpace(output) ? T("Disks_OptimizationFinished", volume) : output.Trim(),
                InfoBarSeverity.Success);
        }, write: !analyzeOnly);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshInventoryAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private Task RefreshInventoryAsync(bool preserveResult = false) => _tool switch
    {
        DiskTool.Usb => ViewModel.RunAsync(async ct =>
        {
            var disks = await _tools.GetUsbDisksAsync(ct);
            Fill(UsbSelector, disks);
            if (disks.Count == 0 && (!preserveResult || !ViewModel.HasResult)) ViewModel.Report(T("Disks_NoUsb"));
        }, clearResult: !preserveResult),
        DiskTool.Recovery => ViewModel.RunAsync(async ct =>
        {
            var volumes = await _tools.GetVolumesAsync(fixedOnly: false, ct);
            Fill(RecoverySource, volumes);
            Fill(RecoveryDestination, volumes, defaultIndex: -1);
        }, clearResult: !preserveResult),
        DiskTool.Optimize => ViewModel.RunAsync(async ct => Fill(OptimizeVolume, await _tools.GetVolumesAsync(fixedOnly: true, ct)),
            clearResult: !preserveResult),
        _ => Task.CompletedTask
    };

    private static void Fill<T>(ComboBox combo, IReadOnlyList<T> items, int defaultIndex = 0)
    {
        var selected = combo.SelectedItem;
        combo.ItemsSource = items;
        if (selected is T item && items.Contains(item)) combo.SelectedItem = item;
        else combo.SelectedIndex = items.Count == 0 ? -1 : defaultIndex;
    }

    private bool RequirePath(out string path)
    {
        path = TargetPath.Text;
        if (!string.IsNullOrWhiteSpace(path)) return true;
        ViewModel.Report(T("Disks_EnterPath"), InfoBarSeverity.Warning);
        return false;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string target) =>
        (await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = T(title),
            Message = T(message),
            Items = [target],
            ConfirmButtonText = T(title),
            CancelButtonText = T("Button_Cancel")
        })).Confirmed;

    private static string FormatProcesses(IEnumerable<DiskProcess> processes) =>
        string.Join(Environment.NewLine, processes.Select(process => $"{process.Name} (PID {process.Id})"));

    private sealed record VersionChoice(DiskShadowVersion Version, string Label)
    {
        public override string ToString() => Label;
    }
}
