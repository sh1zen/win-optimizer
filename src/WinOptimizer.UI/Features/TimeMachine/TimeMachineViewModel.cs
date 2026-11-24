using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Extensions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;

namespace WinOptimizer.UI.Features.TimeMachine;

/// <summary>Presents daily history and saved snapshots with selective recovery.</summary>
public sealed partial class TimeMachineViewModel(TimeMachineService service, ILocalizationService localization,
    IDialogService dialogs, ITaskProgressService tasks, ILogService log) : ObservableObject
{
    private readonly List<ChangeHistoryEntry> _history = [];
    private readonly Dictionary<DateOnly, int> _reviewedDayCounts = [];
    private long? _offset;
    private int _unreadable;
    private CancellationTokenSource? _work;

    public ObservableCollection<TimeMachineDay> Days { get; } = [];
    public ObservableCollection<TimeMachineRow> DayDetails { get; } = [];
    public ObservableCollection<TimeMachineRow> Differences { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNoSnapshots))]
    public partial IReadOnlyList<TimeMachineSnapshotCard> Snapshots { get; private set; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInteract))] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool HasMore { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSnapshotList), nameof(HasOpenSnapshot), nameof(OpenSnapshotLabel), nameof(IsSnapshotComparison))]
    public partial TimeMachineSnapshotCard? OpenSnapshot { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsExploringSnapshot), nameof(HasEmptySnapshot), nameof(IsSnapshotComparison))]
    public partial IReadOnlyList<TimeMachineSnapshotEntry>? SnapshotEntries { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDayList), nameof(HasOpenDay), nameof(OpenDateLabel))]
    public partial TimeMachineDay? OpenDate { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; private set; } = "";

    public bool IsDayList => OpenDate is null;
    public bool HasOpenDay => OpenDate is not null;
    public bool IsSnapshotList => OpenSnapshot is null;
    public bool HasOpenSnapshot => OpenSnapshot is not null;
    public bool IsExploringSnapshot => SnapshotEntries is not null;
    public bool IsSnapshotComparison => HasOpenSnapshot && !IsExploringSnapshot;
    public bool HasEmptySnapshot => SnapshotEntries is { Count: 0 };
    public bool HasNoSnapshots => Snapshots.Count == 0;
    public bool CanInteract => !IsBusy;
    public bool HasStatus => Status.Length > 0;
    public string OpenDateLabel => OpenDate?.Title ?? "";
    public string OpenSnapshotLabel => OpenSnapshot?.Title ?? "";
    public string Title => Text("TimeMachine_Title");
    public string HistoryLabel => Text("Menu_ChangeHistory");
    public string SnapshotsLabel => Text("TimeMachine_Snapshots");
    public string CreateSnapshotLabel => Text("TimeMachine_CreateSnapshot");
    public string ExploreSnapshotLabel => Text("TimeMachine_ExploreSnapshot");
    public string NoSnapshotsLabel => Text("TimeMachine_NoSnapshots");
    public string EmptySnapshotLabel => Text("Logs_Empty");
    public string CompareLabel => Text("TimeMachine_Compare");
    public string RestoreLabel => Text("TimeMachine_RestoreSelected");
    public string RefreshLabel => Text("Button_Refresh");
    public string LoadMoreLabel => Text("Logs_LoadMore");
    public string CloseLabel => Text("Button_Close");
    private string Text(string key) => localization.GetString(key);

    public void Cancel() => _work?.Cancel();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct =>
    {
        await ReloadHistoryAsync(ct);
        await ReloadSnapshotsAsync();
        CloseSnapshotDetails();
    });

    [RelayCommand]
    private Task LoadMoreAsync() => RunAsync(async ct =>
    {
        if (_offset is not null) await AppendHistoryAsync(ct);
        PublishDays();
    });

    [RelayCommand]
    private Task OpenDayAsync(TimeMachineDay day) => RunAsync(ct => LoadDayAsync(day.Date, ct));

    [RelayCommand]
    private void CloseDay()
    {
        if (IsBusy) return;
        OpenDate = null;
        DayDetails.Clear();
        ShowHistoryStatus();
    }

    [RelayCommand]
    private Task CaptureAsync() => RunAsync(async ct =>
    {
        var (snapshot, unavailable) = await service.CaptureAsync(ct);
        await ReloadSnapshotsAsync();
        await ReloadHistoryAsync(ct);
        Status = FormatSummary(snapshot.States.Count, unavailable);
    });

    [RelayCommand]
    private Task ReviewSnapshotAsync(TimeMachineSnapshotCard? snapshot) => RunAsync(async ct =>
    {
        if (snapshot is null) return;
        await PublishComparisonAsync(snapshot.Id, ct);
        SnapshotEntries = null;
        OpenSnapshot = snapshot;
    });

    /// <summary>Opens captured values for inspection without querying or changing live settings.</summary>
    [RelayCommand]
    private Task ExploreSnapshotAsync(TimeMachineSnapshotCard? snapshot) => RunAsync(async ct =>
    {
        if (snapshot is null) return;
        var saved = await service.ReadSnapshotAsync(snapshot.Id, ct);
        ct.ThrowIfCancellationRequested();
        SnapshotEntries = saved.States.Select(state => new TimeMachineSnapshotEntry(
            state.Label, Describe(state.Target), state.Value.Json ?? "—")).ToArray();
        Differences.Clear();
        OpenSnapshot = snapshot;
    });

    [RelayCommand]
    private void CloseSnapshot()
    {
        if (IsBusy) return;
        CloseSnapshotDetails();
        Status = "";
    }

    private void CloseSnapshotDetails()
    {
        OpenSnapshot = null;
        SnapshotEntries = null;
        Differences.Clear();
    }

    /// <summary>Confirms the dated snapshot before removing it from storage and the list.</summary>
    [RelayCommand]
    private Task DeleteSnapshotAsync(TimeMachineSnapshotCard? snapshot) => RunAsync(async ct =>
    {
        if (snapshot is null) return;
        var response = await dialogs.ShowConfirmationAsync(new()
        {
            Title = snapshot.DeleteLabel,
            Message = localization.GetString("TimeMachine_DeleteSnapshotConfirm", snapshot.Title),
            ConfirmButtonText = snapshot.DeleteLabel,
            CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed || ct.IsCancellationRequested) return;
        await service.DeleteSnapshotAsync(snapshot.Id, ct);
        Snapshots = Snapshots.Where(card => card.Id != snapshot.Id).ToArray();
    });

    [RelayCommand]
    private Task RestoreSnapshotAsync() => RunAsync(async ct =>
    {
        if (OpenSnapshot is not { } snapshot || IsExploringSnapshot) return;
        var rows = Differences.Where(x => x.IsSelected && x.CanRestore).ToArray();
        if (!await ConfirmAsync(rows, ct)) return;
        var selected = rows.Select(x => x.Difference!).ToArray();
        var result = await service.RestoreSnapshotAsync(snapshot.Id, selected, ct);
        Differences.Clear();
        await RefreshAfterRestoreAsync(result, () => PublishComparisonAsync(snapshot.Id, ct), ct);
    }, write: true);

    [RelayCommand]
    private Task RestoreHistoryAsync() => RunAsync(async ct =>
    {
        if (OpenDate is not { } open) return;
        var rows = DayDetails.Where(x => x.IsSelected && x.CanRestore).ToArray();
        if (!await ConfirmAsync(rows, ct)) return;
        var result = await service.RestoreHistoryAsync(rows.Select(x => x.RecoveryId!.Value).ToArray(), ct);
        OpenDate = null;
        DayDetails.Clear();
        await RefreshAfterRestoreAsync(result, async () =>
        {
            await ReloadHistoryAsync(ct);
            await LoadDayAsync(open.Date, ct);
        }, ct);
    }, write: true);

    private async Task<bool> ConfirmAsync(TimeMachineRow[] rows, CancellationToken ct)
    {
        if (rows.Length == 0)
        {
            Status = Text("TimeMachine_SelectChanges");
            return false;
        }
        var response = await dialogs.ShowConfirmationAsync(new()
        {
            Title = RestoreLabel,
            Message = Text("TimeMachine_Confirm"),
            Items = rows.Select(x => string.IsNullOrEmpty(x.Resource) ? x.Title : $"{x.Title}\n{x.Resource}").ToArray(),
            ConfirmButtonText = RestoreLabel,
            CancelButtonText = Text("Button_Cancel")
        });
        return response.Confirmed && !ct.IsCancellationRequested;
    }

    private async Task ReloadHistoryAsync(CancellationToken ct)
    {
        var page = await service.ReadHistoryAsync(null, ct);
        _history.Clear();
        _reviewedDayCounts.Clear();
        _unreadable = 0;
        AddPage(page);
        while (_history.Count == 0 && _offset is not null) await AppendHistoryAsync(ct);
        PublishDays();
        OpenDate = null;
        DayDetails.Clear();
        ShowHistoryStatus();
    }

    private async Task AppendHistoryAsync(CancellationToken ct) => AddPage(await service.ReadHistoryAsync(_offset, ct));

    private void AddPage(TimeMachineHistoryPage page)
    {
        _history.AddRange(page.Entries);
        _offset = page.NextOffset;
        _unreadable += page.UnreadableEntries;
        HasMore = _offset is not null;
    }

    private void PublishDays()
    {
        Days.Clear();
        foreach (var group in _history.GroupBy(TimeMachineService.GetHistoryDay).OrderByDescending(x => x.Key))
            Days.Add(DescribeDay(group.Key, _reviewedDayCounts.GetValueOrDefault(group.Key, group.Count())));
    }

    private async Task LoadDayAsync(DateOnly date, CancellationToken ct)
    {
        // Keep the timeline cursor for Load More and collect only the day being reviewed.
        var receipts = _history.Where(entry => TimeMachineService.GetHistoryDay(entry) == date).ToList();
        var unreadable = _unreadable;
        if (_offset is { } offset)
        {
            var older = await service.ReadHistoryDayRemainderAsync(date, offset, ct);
            receipts.AddRange(older.Entries);
            unreadable += older.Unreadable;
        }
        var detail = await service.ReviewHistoryAsync(receipts, ct);
        DayDetails.Clear();
        foreach (var item in detail) DayDetails.Add(HistoryRow(item));
        _reviewedDayCounts[date] = DayDetails.Count;
        PublishDays();
        OpenDate = DescribeDay(date, DayDetails.Count);
        Status = unreadable == 0 ? "" : FormatSummary(DayDetails.Count, unreadable);
    }

    /// <summary>Formats a history card's activity count in the app's language.</summary>
    private TimeMachineDay DescribeDay(DateOnly date, int count) =>
        new(date, localization.GetString("Logs_GroupCount", count.ToString("N0", CultureInfo.CurrentCulture)));

    /// <summary>Publishes dated cards in the snapshot store's newest-first order.</summary>
    private async Task ReloadSnapshotsAsync()
    {
        var snapshots = await service.ListSnapshotsAsync();
        var exploreLabel = ExploreSnapshotLabel;
        var restoreLabel = Text("Nav_Restore");
        var deleteLabel = Text("Button_Delete");
        Snapshots = snapshots.Select(snapshot => new TimeMachineSnapshotCard(snapshot.Id,
            snapshot.CreatedUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture),
            restoreLabel, deleteLabel, ReviewSnapshotCommand, DeleteSnapshotCommand)
        {
            ExploreLabel = exploreLabel,
            ExploreCommand = ExploreSnapshotCommand
        }).ToArray();
    }

    private async Task PublishComparisonAsync(Guid id, CancellationToken ct)
    {
        var comparison = await service.CompareAsync(id, ct);
        Differences.Clear();
        foreach (var change in comparison.Differences)
            Differences.Add(new(change.Baseline.Label, Describe(change.Baseline.Target), Values(change.Baseline.Value, change.Current), "", true)
                { Difference = change });
        Status = FormatSummary(Differences.Count, comparison.Unavailable);
    }

    private TimeMachineRow HistoryRow(TimeMachineHistoryItem item)
    {
        var receipt = item.Receipt;
        var target = item.Undo?.Before.Target;
        var values = item.Undo is { } undo && target?.Area is not (TimeMachineArea.Driver or TimeMachineArea.CleanedRegistry)
            ? Values(undo.Before.Value, undo.After)
            : receipt.Before is not null ? $"{Text("Logs_Before")} {receipt.Before}   →   {Text("Logs_After")} {receipt.After}" : "";
        var detail = string.Join(" · ", new[] { receipt.Timestamp?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            receipt.ActionLabel, receipt.Group, receipt.Batch, item.Notice,
            receipt.RecoveryId is not null && !item.CanRestore ? Text("Utilities_Unknown") : null }.Where(x => !string.IsNullOrEmpty(x)));
        return new(receipt.Name, target is null ? "" : Describe(target), values, detail, item.CanRestore)
            { RecoveryId = receipt.RecoveryId };
    }

    private string Values(TimeMachineValue before, TimeMachineValue after) =>
        $"{Text("Logs_Before")} {FormatValue(before)}   →   {Text("Logs_After")} {FormatValue(after)}";

    private string FormatValue(TimeMachineValue value) => value.Json switch
    {
        null => "—",
        { Length: > 240 } json => json[..240] + "…",
        { } json => json
    };

    private static string Describe(TimeMachineTarget target) =>
        target.Name is null ? target.Key : $"{target.Key}\\{target.Name}";

    private string FormatSummary(int count, int unavailable) =>
        string.Format(CultureInfo.CurrentCulture, Text("TimeMachine_Result"), count, unavailable);

    private void ShowHistoryStatus() => Status = _history.Count == 0 ? Text("Logs_Empty")
        : _unreadable > 0 ? FormatSummary(_history.Count, _unreadable) : "";

    /// <summary>Refreshes the review when possible while keeping completed writes visible.</summary>
    private async Task RefreshAfterRestoreAsync(TimeMachineRestoreBatch batch, Func<Task> refresh, CancellationToken ct)
    {
        List<string> messages = [FormatSummary(batch.Results.Count(x => x.Error is null), batch.Results.Count(x => x.Error is not null))];
        if (batch.Cancelled) messages.Add(Text("Restore_Cancelled"));
        messages.AddRange(batch.Results.Select(x => x.Error is null ? x.Label : $"{x.Label}: {x.Error}"));
        try
        {
            if (!ct.IsCancellationRequested) await refresh();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsWorkError(ex))
        {
            messages.Add(ex.Message);
            log.LogError($"TimeMachine: {ex.Message}");
        }
        Status = string.Join(Environment.NewLine, messages);
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool write = false)
    {
        if (IsBusy) return;
        if (write && tasks.IsTaskRunning)
        {
            Status = Text("Cleaner_Wait");
            return;
        }
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? owner = null;
        _work = lifetime;
        IsBusy = true;
        Status = "";
        try
        {
            if (write)
            {
                owner = tasks.StartTask(RestoreLabel, isIndeterminate: true);
                _work = owner;
            }
            await work(_work.Token);
        }
        catch (OperationCanceledException) { Status = Text("Button_Cancel"); }
        catch (Exception ex) when (IsWorkError(ex))
        {
            Status = ex.Message;
            log.LogError($"TimeMachine: {ex.Message}");
        }
        finally
        {
            _work = null;
            tasks.CompleteOwnedTask(owner);
            IsBusy = false;
        }
    }

    private static bool IsWorkError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException
        or System.Text.Json.JsonException or System.Security.SecurityException or System.ComponentModel.Win32Exception;
}

public sealed record TimeMachineDay(DateOnly Date, string CountText)
{
    public string Title => Date.ToString("D", CultureInfo.CurrentCulture);
}

/// <summary>Displays a saved snapshot and binds its actions to that snapshot's identity.</summary>
public sealed record TimeMachineSnapshotCard(Guid Id, string Title, string RestoreLabel, string DeleteLabel,
    IAsyncRelayCommand<TimeMachineSnapshotCard?> ReviewCommand, IAsyncRelayCommand<TimeMachineSnapshotCard?> DeleteCommand)
{
    public string ExploreLabel { get; init; } = "";
    public IAsyncRelayCommand<TimeMachineSnapshotCard?>? ExploreCommand { get; init; }
}

/// <summary>A saved setting displayed in a snapshot's read-only view.</summary>
public sealed record TimeMachineSnapshotEntry(string Title, string Resource, string Value);

public sealed partial class TimeMachineRow(string title, string resource, string values, string detail, bool canRestore) : ObservableObject
{
    public string Title { get; } = title;
    public string Resource { get; } = resource;
    public string Values { get; } = values;
    public string Detail { get; } = detail;
    public bool CanRestore { get; } = canRestore;
    public bool HasResource => Resource.Length > 0;
    public bool HasValues => Values.Length > 0;
    public bool HasDetail => Detail.Length > 0;
    public Guid? RecoveryId { get; init; }
    public TimeMachineDifference? Difference { get; init; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
}
