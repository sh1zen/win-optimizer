using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>Edits raw variables in the selected scope without changing them until Save/Delete is invoked.</summary>
public partial class EnvironmentEditorViewModel : UtilityViewModel
{
    private readonly IEnvironmentEditorService _service;
    private readonly IDialogService _dialogs;
    private IReadOnlyList<EnvironmentVariableEntry> _all = [];
    private EnvironmentScope? _loadedScope;
    private Task? _loading;
    private bool _updatingPathValue;
    private EnvironmentScope Scope => ScopeIndex switch { 0 => EnvironmentScope.User, 1 => EnvironmentScope.System,
        _ => throw new ArgumentOutOfRangeException(nameof(ScopeIndex)) };
    protected override string TitleKey => "Utilities_Environment";
    public ObservableCollection<EnvironmentVariableEntry> Variables { get; } = [];
    public ObservableCollection<EnvironmentPathEntryViewModel> PathEntries { get; } = [];
    [ObservableProperty] public partial int ScopeIndex { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanChangeName))] public partial EnvironmentVariableEntry? SelectedVariable { get; set; }
    [ObservableProperty] public partial string VariableName { get; set; } = "";
    [ObservableProperty] public partial string VariableValue { get; set; } = "";
    [ObservableProperty] public partial bool Expandable { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPathEntries), nameof(ShowRawValue))]
    public partial bool EditPathAsText { get; set; }
    [ObservableProperty] public partial EnvironmentPathEntryViewModel? SelectedPathEntry { get; set; }
    public bool IsPath => VariableName.Equals("PATH", StringComparison.OrdinalIgnoreCase);
    public bool ShowPathEntries => IsPath && !EditPathAsText;
    public bool ShowRawValue => !ShowPathEntries;
    public bool HasChanges => SelectedVariable is { } original
        ? VariableValue != original.Value || Expandable != original.Expandable
        : VariableName.Length != 0 || VariableValue.Length != 0;
    public bool ShowPathEmpty => PathEntries.Count == 0;
    public string PathCountLabel => string.Format(Text("Utilities_PathCount"), PathEntries.Count);
    public string PathLabel => Text("Utilities_PathEntries");
    public string PathHint => Text("Utilities_PathHint");
    public string PathEmptyLabel => Text("Utilities_PathEmpty");
    public string RawTextLabel => Text("Utilities_PathRawText");
    public string AddPathLabel => Text("Utilities_PathAdd");
    public string BrowsePathLabel => Text("Utilities_PathBrowse");
    public string RemovePathLabel => Text("Utilities_PathRemove");
    public string MoveUpLabel => Text("Utilities_PathMoveUp");
    public string MoveDownLabel => Text("Utilities_PathMoveDown");
    public string UnsavedLabel => Text("Utilities_UnsavedChanges");
    public string ResetLabel => Text("Utilities_ResetDraft");
    public bool CanChangeName => CanWrite && SelectedVariable == null;
    public string UserLabel => Text("Utilities_UserVariables");
    public string SystemLabel => Text("Utilities_SystemVariables");
    public string SearchLabel => Text("Utilities_SearchVariables");
    public string NameLabel => Text("Utilities_VariableName");
    public string ValueLabel => Text("Utilities_VariableValue");
    public string ExpandLabel => Text("Utilities_ExpandVariables");
    public string NewLabel => Text("Utilities_NewVariable");
    public string DeleteLabel => Text("Utilities_DeleteVariable");
    public string Hint => Text("Utilities_EnvironmentHint");

    public EnvironmentEditorViewModel(IEnvironmentEditorService service, IDialogService dialogs,
        ILocalizationService localization, IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks) { _service = service; _dialogs = dialogs; }

    public Task LoadAsync() => _loadedScope == Scope ? Task.CompletedTask :
        _loading is { IsCompleted: false } ? _loading : _loading = ReloadAsync();
    partial void OnScopeIndexChanged(int value) { if (value is 0 or 1) _ = LoadAsync(); }
    partial void OnQueryChanged(string value) => Filter();
    partial void OnVariableNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsPath)); OnPropertyChanged(nameof(ShowPathEntries)); OnPropertyChanged(nameof(ShowRawValue));
        SynchronizePathEntries();
        NotifyDraftChanged();
    }
    partial void OnVariableValueChanged(string value)
    {
        if (!_updatingPathValue) SynchronizePathEntries();
        NotifyDraftChanged();
    }
    partial void OnExpandableChanged(bool value) => NotifyDraftChanged();
    partial void OnSelectedPathEntryChanged(EnvironmentPathEntryViewModel? value) => NotifyPathActions();
    partial void OnSelectedVariableChanged(EnvironmentVariableEntry? value)
    {
        EditPathAsText = false;
        if (value != null) { VariableName = value.Name; VariableValue = value.Value; Expandable = value.Expandable; }
        DeleteCommand.NotifyCanExecuteChanged();
        NotifyDraftChanged();
    }

    /// <summary>Builds editable rows from the raw draft without normalizing or dropping empty segments.</summary>
    private void SynchronizePathEntries()
    {
        foreach (var entry in PathEntries) entry.PropertyChanged -= OnPathEntryChanged;
        PathEntries.Clear(); SelectedPathEntry = null;
        if (IsPath && VariableValue.Length != 0)
            foreach (var value in VariableValue.Split(';'))
            {
                var entry = new EnvironmentPathEntryViewModel(value);
                entry.PropertyChanged += OnPathEntryChanged;
                PathEntries.Add(entry);
            }
        NotifyPathActions();
    }

    private void OnPathEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EnvironmentPathEntryViewModel.Value)) UpdatePathValue();
    }

    /// <summary>Serializes row edits into the same raw value used by the existing save boundary.</summary>
    private void UpdatePathValue()
    {
        _updatingPathValue = true;
        try { VariableValue = string.Join(';', PathEntries.Select(entry => entry.Value)); }
        finally { _updatingPathValue = false; }
        NotifyPathActions();
    }

    private void NotifyDraftChanged()
    {
        OnPropertyChanged(nameof(HasChanges));
        SaveCommand.NotifyCanExecuteChanged(); ResetDraftCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPathActions()
    {
        OnPropertyChanged(nameof(PathCountLabel)); OnPropertyChanged(nameof(ShowPathEmpty));
        AddPathEntryCommand.NotifyCanExecuteChanged(); RemovePathEntryCommand.NotifyCanExecuteChanged();
        MovePathEntryUpCommand.NotifyCanExecuteChanged(); MovePathEntryDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditPath))]
    private void AddPathEntry(string? value)
    {
        // A pasted semicolon-separated value becomes consecutive entries, preserving empty segments.
        foreach (var segment in (value ?? "").Split(';'))
        {
            var entry = new EnvironmentPathEntryViewModel(segment);
            entry.PropertyChanged += OnPathEntryChanged;
            PathEntries.Add(entry); SelectedPathEntry = entry;
        }
        UpdatePathValue();
    }

    [RelayCommand(CanExecute = nameof(CanRemovePathEntry))]
    private void RemovePathEntry()
    {
        var index = PathEntries.IndexOf(SelectedPathEntry!);
        SelectedPathEntry!.PropertyChanged -= OnPathEntryChanged;
        PathEntries.RemoveAt(index);
        SelectedPathEntry = PathEntries.Count == 0 ? null : PathEntries[Math.Min(index, PathEntries.Count - 1)];
        UpdatePathValue();
    }

    [RelayCommand(CanExecute = nameof(CanMovePathEntryUp))]
    private void MovePathEntryUp() => MovePathEntry(-1);
    [RelayCommand(CanExecute = nameof(CanMovePathEntryDown))]
    private void MovePathEntryDown() => MovePathEntry(1);

    private void MovePathEntry(int offset)
    {
        var entry = SelectedPathEntry!;
        var index = PathEntries.IndexOf(entry);
        PathEntries.Move(index, index + offset); SelectedPathEntry = entry;
        UpdatePathValue();
    }

    private bool CanEditPath() => CanWrite && ShowPathEntries;
    private bool CanRemovePathEntry() => CanEditPath() && SelectedPathEntry != null && PathEntries.Contains(SelectedPathEntry);
    private bool CanMovePathEntryUp() => CanRemovePathEntry() && PathEntries.IndexOf(SelectedPathEntry!) > 0;
    private bool CanMovePathEntryDown() => CanRemovePathEntry() && PathEntries.IndexOf(SelectedPathEntry!) < PathEntries.Count - 1;

    partial void OnEditPathAsTextChanged(bool value)
    {
        if (!value) SynchronizePathEntries();
        NotifyPathActions();
    }

    [RelayCommand(CanExecute = nameof(CanResetDraft))]
    private void ResetDraft()
    {
        if (SelectedVariable is { } original)
        { VariableValue = original.Value; Expandable = original.Expandable; SynchronizePathEntries(); }
        else New();
    }
    private bool CanResetDraft() => CanWrite && HasChanges;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => ReloadAsync();
    private Task ReloadAsync() => ExecuteAsync(async () => { await ReadValuesAsync(); New(); });
    private async Task ReadValuesAsync()
    {
        var scope = Scope;
        _all = await _service.ReadAsync(scope);
        _loadedScope = scope;
        Filter();
    }
    private void Filter()
    {
        Variables.Clear();
        foreach (var variable in _all.Where(v => string.IsNullOrWhiteSpace(Query) || v.Name.Contains(Query, StringComparison.OrdinalIgnoreCase)))
            Variables.Add(variable);
        OnPropertyChanged(nameof(SelectedVariable));
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private void New() { SelectedVariable = null; VariableName = ""; VariableValue = ""; Expandable = true; EditPathAsText = false; }
    private bool CanSave() => CanWrite && HasChanges && !string.IsNullOrWhiteSpace(VariableName) && _loadedScope == Scope;
    private bool CanDelete() => CanWrite && SelectedVariable != null && _loadedScope == Scope;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        var value = new EnvironmentVariableEntry(VariableName, VariableValue, Expandable);
        var scope = Scope;
        var original = SelectedVariable;
        return ExecuteAsync(async () =>
        {
            var result = await _service.SaveAsync(scope, original, value);
            await ReadValuesAsync();
            SelectedVariable = _all.Single(v => v.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase));
            Status = Text(result.NotificationDelivered ? "Utilities_SavedVariables" : "Utilities_SavedVariablesNotifyFailed");
        }, write: true);
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        var variable = SelectedVariable;
        var scope = Scope;
        if (variable == null || !CanDelete()) return;
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = DeleteLabel, Message = string.Format(Text("Utilities_DeleteConfirm"), variable.Name),
            ConfirmButtonText = DeleteLabel, CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await ExecuteAsync(async () =>
        {
            var result = await _service.DeleteAsync(scope, variable);
            await ReadValuesAsync(); New();
            Status = Text(result.NotificationDelivered ? "Utilities_SavedVariables" : "Utilities_SavedVariablesNotifyFailed");
        }, write: true);
    }

    protected override void NotifyActions()
    {
        RefreshCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
        NewCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanChangeName));
        ResetDraftCommand.NotifyCanExecuteChanged(); NotifyPathActions();
    }
}
