using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>Retains a hosts draft across navigation and offers a backed-up save and session undo.</summary>
public partial class HostsEditorViewModel : UtilityViewModel
{
    private readonly IHostsEditorService _service;
    private readonly IDialogService _dialogs;
    private HostsDocument? _document;
    private string? _undo;
    private bool _updatingRows;
    protected override string TitleKey => "Utilities_Hosts";
    public ObservableCollection<HostsEntryViewModel> Entries { get; } = [];
    [ObservableProperty] public partial string Content { get; set; } = "";
    [ObservableProperty] public partial string FilePath { get; private set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowEntries))] public partial bool EditAsText { get; set; }
    [ObservableProperty] public partial bool HasInvalidLines { get; private set; }
    public bool ShowEntries => !EditAsText;
    public bool HasChanges => _document != null && Content != _document.Content;
    public bool IsEmpty => Entries.Count == 0;
    public bool CanEditEntries => CanWrite && _document != null;
    public string Hint => Text(EditAsText ? "Utilities_HostsHint" : "Utilities_HostsStructuredHint");
    public string UndoLabel => Text("Utilities_UndoHosts");
    public string NewEntryLabel => Text("Utilities_HostsNewEntry");
    public string EditEntryLabel => Text("Utilities_HostsEditEntry");
    public string AddressLabel => Text("Utilities_HostsAddress");
    public string HostNamesLabel => Text("Utilities_HostsNames");
    public string CommentLabel => Text("Utilities_HostsComment");
    public string EnabledLabel => Text("Utilities_HostsEnabled");
    public string RawTextLabel => Text("Utilities_HostsRawText");
    public string EmptyLabel => Text("Utilities_HostsEmpty");
    public string InvalidLinesLabel => Text("Utilities_HostsInvalidLines");
    public string EntryCountLabel => string.Format(Text("Utilities_HostsEntryCount"), Entries.Count);
    public string UnsavedLabel => Text("Utilities_UnsavedChanges");
    public string CancelLabel => Text("Button_Cancel");

    public HostsEditorViewModel(IHostsEditorService service, IDialogService dialogs, ILocalizationService localization,
        IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks)
    {
        _service = service; _dialogs = dialogs;
        PropertyChanged += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName))
                foreach (var entry in Entries) entry.Update(entry.Mapping, CanEditEntries);
        };
    }

    public Task LoadAsync() => _document == null ? ReadAsync() : Task.CompletedTask;
    partial void OnContentChanged(string value)
    {
        var parsed = HostsText.Parse(value);
        _updatingRows = true;
        try
        {
            for (var index = Entries.Count - 1; index >= parsed.Entries.Count; index--)
            { Entries[index].PropertyChanged -= OnEntryChanged; Entries.RemoveAt(index); }
            for (var index = 0; index < parsed.Entries.Count; index++)
            {
                if (index < Entries.Count) Entries[index].Update(parsed.Entries[index], CanEditEntries);
                else
                {
                    var entry = new HostsEntryViewModel(parsed.Entries[index], CanEditEntries, Text);
                    entry.PropertyChanged += OnEntryChanged; Entries.Add(entry);
                }
            }
        }
        finally { _updatingRows = false; }
        HasInvalidLines = parsed.HasInvalidLines;
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EntryCountLabel));
        OnPropertyChanged(nameof(HasChanges)); SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditAsTextChanged(bool value) => OnPropertyChanged(nameof(Hint));

    /// <summary>Ignores WinUI's display-only line-ending normalization when presenting the raw draft.</summary>
    public void UpdateContentFromEditor(string text)
    {
        if (!CanWrite || !EditAsText) return;
        static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
        if (Normalize(text) != Normalize(Content)) Content = text;
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_updatingRows || args.PropertyName != nameof(HostsEntryViewModel.IsEnabled) || sender is not HostsEntryViewModel entry) return;
        if (CanEditEntries && Entries.Contains(entry)) Content = HostsText.SetEnabled(Content, entry.Mapping, entry.IsEnabled);
        else entry.Update(entry.Mapping, CanEditEntries);
    }

    /// <summary>Validates and changes only the draft; persistence still requires the page's Save command.</summary>
    public void ApplyEntry(HostsEntryViewModel? entry, string address, string hostNames, string comment, bool enabled)
    {
        if (!CanEditEntries) throw new InvalidOperationException(Text("Utilities_ModeNotice"));
        if (entry != null && !Entries.Contains(entry)) throw new InvalidOperationException(Text("Utilities_HostsEntryChanged"));
        Content = entry == null ? HostsText.Add(Content, address, hostNames, comment, enabled)
            : HostsText.Edit(Content, entry.Mapping, address, hostNames, comment, enabled);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveEntry))]
    private void RemoveEntry(HostsEntryViewModel? entry)
    {
        if (CanRemoveEntry(entry)) Content = HostsText.Remove(Content, entry!.Mapping);
    }
    private bool CanRemoveEntry(HostsEntryViewModel? entry) => CanEditEntries && entry != null && Entries.Contains(entry);
    private bool CanSave() => CanWrite && _document != null && Content != _document.Content;
    private bool CanUndo() => CanWrite && _document != null && _undo != null;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        if (_document != null && Content != _document.Content)
        {
            var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
            { Title = RefreshLabel, Message = Text("Utilities_DiscardHosts"), ConfirmButtonText = RefreshLabel, CancelButtonText = Text("Button_Cancel") });
            if (!response.Confirmed) return;
        }
        await ReadAsync();
    }
    private Task ReadAsync() => ExecuteAsync(async () =>
    {
        _document = await _service.ReadAsync(); Content = _document.Content; FilePath = _document.FilePath; _undo = null;
        OnPropertyChanged(nameof(HasChanges));
    });

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => SaveContentAsync(Content);
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task UndoAsync() => SaveContentAsync(_undo!);
    private Task SaveContentAsync(string content) => ExecuteAsync(async () =>
    {
        var previous = _document!.Content;
        var result = await _service.SaveAsync(_document, content);
        _document = result.Document; Content = _document.Content; _undo = previous;
        OnPropertyChanged(nameof(HasChanges));
        Status = string.Format(Text("Utilities_HostsSaved"), result.BackupPath);
    }, write: true);

    protected override void NotifyActions()
    {
        RefreshCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); UndoCommand.NotifyCanExecuteChanged();
        RemoveEntryCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanEditEntries));
        foreach (var entry in Entries) entry.CanEdit = CanEditEntries;
    }
}
