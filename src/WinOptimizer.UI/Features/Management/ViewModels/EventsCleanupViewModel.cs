using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Management.ViewModels;

/// <summary>Owns explicit channel selection, confirmation and truthful per-channel cleanup results.</summary>
public partial class EventsCleanupViewModel : UtilityViewModel
{
    private readonly IEventLogCleanupService _service;
    private readonly IDialogService _dialogs;
    private readonly ITaskProgressService _tasks;
    private bool _loaded;
    private bool _updatingSelection;
    protected override string TitleKey => "EventsCleanup_Title";
    public ObservableCollection<EventLogRow> Channels { get; } = [];
    public string ClearLabel => Text("EventsCleanup_Clear");
    public string SelectAllLabel => Text("EventsCleanup_SelectAll");
    public string SelectNoneLabel => Text("EventsCleanup_SelectNone");
    public string Hint => Text("EventsCleanup_Hint");
    public string CountLabel => Text("EventsCleanup_Count");
    public string SizeLabel => Text("EventsCleanup_Size");
    public string EmptyLabel => _loaded && Channels.Count == 0 ? Text("EventsCleanup_Empty") : "";
    public string SelectionLabel => string.Format(Text("EventsCleanup_Selection"), Channels.Count(row => row.IsSelected), Channels.Count);
    /// <summary>Totals current selected file sizes, keeping unavailable measurements explicit.</summary>
    public string SelectionSizeLabel
    {
        get
        {
            long bytes = 0;
            int unknown = 0;
            foreach (var row in Channels)
            {
                if (!row.IsSelected) continue;
                if (row.FileSize is { } size) bytes += size;
                else unknown++;
            }
            var formatted = bytes switch
            {
                >= 1L << 40 => $"{bytes / (double)(1L << 40):N2} TiB",
                >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GiB",
                >= 1L << 20 => $"{bytes / (double)(1L << 20):N2} MiB",
                >= 1L << 10 => $"{bytes / 1024d:N2} KiB",
                _ => $"{bytes:N0} B"
            };
            return unknown == 0
                ? string.Format(Text("EventsCleanup_SelectionSize"), formatted)
                : string.Format(Text("EventsCleanup_SelectionSizePartial"), formatted, unknown);
        }
    }
    public bool CanClear => CanWrite && Channels.Any(row => row.IsSelected);
    public bool HasChannels => IsIdle && Channels.Count != 0;

    public EventsCleanupViewModel(IEventLogCleanupService service, IDialogService dialogs, ILocalizationService localization,
        IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks, "Nav_Cleaner")
    { _service = service; _dialogs = dialogs; _tasks = tasks; }

    public Task LoadAsync() => _loaded ? Task.CompletedTask : RefreshAsync();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => ExecuteAsync(async () =>
    {
        var channels = await _service.ReadAsync(CancellationToken.None);
        foreach (var row in Channels) row.PropertyChanged -= OnRowChanged;
        Channels.Clear();
        foreach (var channel in channels)
        {
            var row = new EventLogRow(channel);
            row.PropertyChanged += OnRowChanged;
            Channels.Add(row);
        }
        _loaded = true;
        OnPropertyChanged(nameof(EmptyLabel));
        NotifySelection();
    });

    [RelayCommand(CanExecute = nameof(HasChannels))]
    private void SelectAll() => SetSelection(true);

    [RelayCommand(CanExecute = nameof(HasChannels))]
    private void SelectNone() => SetSelection(false);

    /// <summary>Updates a whole selection in one pass without recounting the inventory for each row.</summary>
    private void SetSelection(bool selected)
    {
        _updatingSelection = true;
        try { foreach (var row in Channels) row.IsSelected = selected && row.CanSelect; }
        finally { _updatingSelection = false; NotifySelection(); }
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private Task ClearAsync() => ExecuteAsync(async () =>
    {
        // Freeze the actual names before opening the dialog; selection stays disabled while awaiting it.
        var names = Channels.Where(row => row.IsSelected).Select(row => row.Name).ToArray();
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = Title, Message = Text("EventsCleanup_Confirm"), Items = names,
            ConfirmButtonText = ClearLabel, CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await ExecuteCleanupAsync(names);
    }, write: true, canCancel: true);

    /// <summary>Retains partial receipts and treats incomplete cleanup as normal unless all logs fail or an archive has errors.</summary>
    private async Task ExecuteCleanupAsync(string[] names)
    {
        var task = _tasks.CurrentTaskCancellationSource ?? throw new InvalidOperationException("The cleanup task was not started.");
        var progress = new Progress<EventLogCleanupProgress>(value =>
        {
            if (ReferenceEquals(_tasks.CurrentTaskCancellationSource, task))
                _tasks.UpdateDetailedProgress(new TaskProgressDetail
                {
                    Progress = value.Total == 0 ? 0 : value.Completed * 100 / value.Total,
                    StatusText = value.LogName,
                    IsIndeterminate = false
                });
        });
        try
        {
            var result = await _service.ClearAsync(names, progress, task.Token);
            var rows = Channels.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
            _updatingSelection = true;
            try
            {
                foreach (var outcome in result.Outcomes)
                    rows[outcome.LogName].Apply(outcome, Text(outcome.Cleared ? "EventsCleanup_Cleared" : "EventsCleanup_Failed"));
            }
            finally { _updatingSelection = false; NotifySelection(); }
            var cleared = result.Outcomes.Count(outcome => outcome.Cleared);
            Status = string.Format(Text(result.Cancelled ? "EventsCleanup_Cancelled" : "EventsCleanup_Summary"), cleared, names.Length);
            IsError = result.Outcomes.Any(outcome => outcome.Error.Length != 0 && (cleared == 0 || outcome.Cleared));
        }
        catch (OperationCanceledException) when (task.IsCancellationRequested)
        { Status = string.Format(Text("EventsCleanup_Cancelled"), 0, names.Length); }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updatingSelection && e.PropertyName is nameof(EventLogRow.IsSelected) or nameof(EventLogRow.FileSize) or "" or null)
            NotifySelection();
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(SelectionSizeLabel));
        OnPropertyChanged(nameof(CanClear));
        ClearCommand.NotifyCanExecuteChanged();
    }

    protected override void NotifyActions()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        SelectNoneCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasChannels));
        NotifySelection();
    }
}

/// <summary>Retains the inventory record, explicit selection and the latest action receipt for one channel.</summary>
public partial class EventLogRow(EventLogChannel channel) : ObservableObject
{
    private EventLogChannel _channel = channel;
    public string Name => _channel.Name;
    public long? FileSize => _channel.FileSize;
    public string CountText => _channel.RecordCount?.ToString("N0") ?? "—";
    public string SizeText => _channel.FileSize is { } size ? $"{size / 1024d:N0} KiB" : "—";
    public bool CanSelect => _channel.Diagnostic.Length == 0;
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string ResultText { get; private set; } = channel.Diagnostic;

    /// <summary>Keeps native failures and archive paths visible; new post-clear events retain their real counts.</summary>
    public void Apply(EventLogCleanupOutcome outcome, string label)
    {
        if (outcome.Current != null) _channel = outcome.Current;
        if (outcome.Cleared) IsSelected = false;
        ResultText = string.Join(Environment.NewLine, new[] { label, outcome.Error, outcome.BackupPath, _channel.Diagnostic }.Where(value => value.Length != 0));
        OnPropertyChanged(string.Empty);
    }
}
