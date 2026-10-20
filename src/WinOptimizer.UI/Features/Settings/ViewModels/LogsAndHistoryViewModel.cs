using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>Queries all saved activity by subject and kind, retaining typed records for grouped presentation.</summary>
public partial class LogsAndHistoryViewModel(IActivityJournalService journal, ILogService logs,
    ILocalizationService localization, IDialogService dialogs) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _readCancellation;
    private IActivityJournalSession? _session;
    private bool _disposed;
    private bool _isClearing;
    private bool _hasIncompleteSources;
    private int _unreadable;
    private int _topicIndex;
    private int _filterIndex;
    public ObservableCollection<LogDisplayEntry> Entries { get; } = [];
    public ObservableCollection<LogTopicGroup> Groups { get; } = [];
    public Task PendingRead { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<JournalOption> Topics { get; } = new[] { new JournalOption("Logs_AllTopics", localization) }
        .Concat(Enum.GetValues<LogTopic>().Select(topic => new JournalOption("Logs_Topic_" + topic, localization))).ToArray();
    public IReadOnlyList<JournalOption> Filters { get; } = Enum.GetValues<JournalFilter>()
        .Select(filter => new JournalOption("Logs_Filter_" + filter, localization)).ToArray();

    // Native selectors temporarily report -1 while their item source is attached.
    public int TopicIndex
    {
        get => _topicIndex;
        set { if (value >= 0 && value < Topics.Count && SetProperty(ref _topicIndex, value)) QueueQuery(); }
    }
    public int FilterIndex
    {
        get => _filterIndex;
        set { if (value >= 0 && value < Filters.Count && SetProperty(ref _filterIndex, value)) QueueQuery(); }
    }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate), nameof(CanClear), nameof(CanLoadMore), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(LoadMoreCommand), nameof(ClearAppLogsCommand))]
    public partial bool IsBusy { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore), nameof(EmptyLabel))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] public partial string Error { get; private set; } = "";
    [ObservableProperty] public partial string Notice { get; private set; } = "";
    public bool CanOperate => !IsBusy;
    public bool CanClear => CanOperate;
    public bool CanLoadMore => HasMore && CanOperate;
    public bool IsEmpty => !IsBusy && Entries.Count == 0 && Error.Length == 0 && _unreadable == 0 && !_hasIncompleteSources;
    public string TopicLabel => localization.GetString("Logs_Topic");
    public string FilterLabel => localization.GetString("Logs_Filter");
    public string SearchPlaceholder => localization.GetString("Logs_Search");
    public string RefreshLabel => localization.GetString("Restore_Refresh");
    public string LoadMoreLabel => localization.GetString("Logs_LoadMore");
    public string ClearLabel => localization.GetString("Logs_Clear");
    public string EmptyLabel => localization.GetString(HasMore ? "Logs_NoMatchesYet" : "Logs_Empty");
    public string Summary => localization.GetString("Logs_Summary", Entries.Count, Groups.Count);

    partial void OnSearchTextChanged(string value) => QueueQuery(true);

    /// <summary>Replaces an obsolete query, debouncing only typed search text.</summary>
    private void QueueQuery(bool debounce = false)
    {
        if (!_disposed && !_isClearing) PendingRead = ReadCoreAsync(false, debounce);
    }

    /// <summary>Updates existing option and group objects without changing the active filters.</summary>
    public void RefreshLabels()
    {
        foreach (var option in Topics.Concat(Filters)) option.Refresh();
        foreach (var entry in Entries) entry.RefreshLabels();
        foreach (var group in Groups) group.RefreshLabels();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task RefreshAsync() => PendingRead = ReadCoreAsync(false, false);

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => PendingRead = ReadCoreAsync(true, false);

    /// <summary>Stops query publication on category exit; returning explicitly refreshes its session.</summary>
    public void CancelRead()
    {
        _readCancellation?.Cancel();
        _readCancellation = null;
        IsBusy = _isClearing;
    }

    /// <summary>Consumes bounded merged pages; no filename selection or message parsing belongs in presentation.</summary>
    private async Task ReadCoreAsync(bool append, bool debounce)
    {
        if (_disposed) return;
        CancelRead();
        using var cancellation = new CancellationTokenSource();
        _readCancellation = cancellation;
        var token = cancellation.Token;
        var query = new JournalQuery(TopicIndex == 0 ? null : Enum.GetValues<LogTopic>()[TopicIndex - 1],
            (JournalFilter)FilterIndex, SearchText);
        IsBusy = true; Error = ""; Notice = "";
        if (!append)
        {
            Entries.Clear(); Groups.Clear(); _session = null; _unreadable = 0; _hasIncompleteSources = false; HasMore = false;
            OnPropertyChanged(nameof(Summary));
        }
        try
        {
            if (debounce) await Task.Delay(250, token);
            var session = _session ?? await journal.OpenAsync(query, token);
            token.ThrowIfCancellationRequested();
            _session = session;
            var page = await session.ReadNextAsync(token);
            token.ThrowIfCancellationRequested();
            foreach (var entry in page.Entries) Entries.Add(new LogDisplayEntry(entry, localization));
            HasMore = page.HasMore; _unreadable += page.UnreadableEntries;
            _hasIncompleteSources = page.Notices.Count > 0;
            RebuildGroups();
            Notice = string.Join(Environment.NewLine, new[]
            {
                _unreadable > 0 ? localization.GetString("Logs_Unreadable", _unreadable) : "",
                page.Notices.Count > 0 ? localization.GetString("Logs_Incomplete") + Environment.NewLine + string.Join(Environment.NewLine, page.Notices) : ""
            }.Where(text => text.Length > 0));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) { Error = ex.Message; HasMore = false; _session = null; }
        }
        finally
        {
            if (ReferenceEquals(_readCancellation, cancellation)) { _readCancellation = null; IsBusy = _isClearing; }
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>Builds topic projections over the same rows and preserves expansion when loading another page.</summary>
    private void RebuildGroups()
    {
        var expanded = Groups.ToDictionary(group => group.Topic, group => group.IsExpanded);
        Groups.Clear();
        foreach (var group in Entries.GroupBy(entry => entry.Record.Topic))
        {
            var rows = group.ToArray();
            Groups.Add(new LogTopicGroup(group.Key, rows, localization)
            { IsExpanded = expanded.GetValueOrDefault(group.Key,
                TopicIndex != 0 || FilterIndex == (int)JournalFilter.Problems || !string.IsNullOrWhiteSpace(SearchText)) });
        }
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAppLogsAsync()
    {
        if (!CanClear || _disposed) return;
        _isClearing = true; IsBusy = true; Error = "";
        try
        {
            var response = await dialogs.ShowConfirmationAsync(new ConfirmationRequest
            {
                Title = ClearLabel, Message = localization.GetString("Logs_ClearConfirm"),
                ConfirmButtonText = ClearLabel, CancelButtonText = localization.GetString("Button_Cancel")
            });
            if (!response.Confirmed || _disposed) return;
            await logs.ClearAsync();
            if (_disposed) return;
            await ReadCoreAsync(false, false);
            if (Error.Length == 0) Notice = string.Join(Environment.NewLine,
                new[] { localization.GetString("Logs_Cleared"), Notice }.Where(text => text.Length > 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (!_disposed) Error = ex.Message; }
        finally { _isClearing = false; if (!_disposed) IsBusy = _readCancellation is not null; }
    }

    public void Dispose() { _disposed = true; CancelRead(); }
}
