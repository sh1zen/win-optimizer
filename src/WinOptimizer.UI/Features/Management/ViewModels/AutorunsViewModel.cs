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

/// <summary>Loads configured points for the selected mode and confirms individual startup changes.</summary>
public partial class AutorunsViewModel : UtilityViewModel
{
    private readonly IAutorunService _service;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private AutorunInventory? _inventory;
    private AutorunRow[] _allRows = [];
    private bool _loaded;
    protected override string TitleKey => "Autoruns_Title";
    public ObservableCollection<AutorunRow> Rows { get; } = [];
    public ObservableCollection<AutorunGroup> Groups { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];

    [ObservableProperty] public partial bool Advanced { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshCommand))] public partial bool LicenseAccepted { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string? SelectedCategory { get; set; }
    [ObservableProperty] public partial int StateFilter { get; set; }
    [ObservableProperty] public partial bool HideVerifiedMicrosoft { get; set; }
    [ObservableProperty] public partial AutorunRow? SelectedRow { get; set; }

    public string AdvancedLabel => Text("Autoruns_Advanced");
    public string LicenseLabel => Text("Autoruns_License");
    public string ScanLabel => Text(Advanced ? "Autoruns_ScanAdvanced" : "Utilities_Refresh");
    public string SearchLabel => Text("Autoruns_Search");
    public string CategoryLabel => Text("Autoruns_Category");
    public string AllLabel => Text("Autoruns_All");
    public string AllStatesLabel => Text("Autoruns_AllStates");
    public string ClearFiltersLabel => Text("Autoruns_ClearFilters");
    public string EnabledLabel => Text("Autoruns_Enabled");
    public string DisabledLabel => Text("Autoruns_Disabled");
    public string UnknownLabel => Text("Autoruns_Unknown");
    public string HideMicrosoftLabel => Text("Autoruns_HideMicrosoft");
    public string Hint => Text(Advanced ? "Autoruns_AdvancedHint" : "Autoruns_Hint");
    public string WindowsSettingsLabel => Text("Autoruns_WindowsSettings");
    public string EnableLabel => Text("Autoruns_Enable");
    public string DisableLabel => Text("Autoruns_Disable");
    public string WarningLabel => Text("Autoruns_Warning");
    public string LocationLabel => Text("Autoruns_Location");
    public string CommandLabel => Text("Autoruns_Command");
    public string PublisherLabel => Text("Autoruns_Publisher");
    public string ProfileLabel => Text("Autoruns_Profile");
    public string SignerLabel => Text("Autoruns_Signer");
    public string EmptyLabel => Text(_inventory == null
        ? Advanced && !LicenseAccepted ? "Autoruns_ConsentRequired" : "Autoruns_NotScanned"
        : "Autoruns_Empty");
    public bool IsEmpty => !IsBusy && Rows.Count == 0;
    public string CountSummary => string.Format(Text("Autoruns_Count"), Rows.Count, _allRows.Length);
    public string SnapshotSummary => _inventory == null ? "" : $"{_inventory.CapturedAt:g} · {_inventory.Coverage}";
    public string Warnings => _inventory == null ? "" : string.Join("\n", _inventory.Warnings);
    public bool HasWarnings => !string.IsNullOrEmpty(Warnings);
    public bool HasSelection => SelectedRow != null;
    public string SelectedName => SelectedRow?.Entry.Name ?? Text("Autoruns_SelectEntry");
    public string SelectedLocation => SelectedRow?.Entry.Location ?? "";
    public string SelectedCommand => SelectedRow?.Entry.Command ?? "";
    public string SelectedImage => SelectedRow?.Entry.ImagePath ?? "";
    public string SelectedPublisher => SelectedRow?.Entry.Publisher ?? "";
    public string SelectedSigner => SelectedRow?.Entry.Signer ?? "";
    public string SelectedProfile => SelectedRow?.Entry.Profile ?? "";
    public string SelectedDetails => SelectedRow?.Entry.Details ?? "";
    public string SelectedState => Text(SelectedRow?.Entry.Enabled switch { true => "Autoruns_Enabled", false => "Autoruns_Disabled", _ => "Autoruns_Unknown" });
    public string ControlHint => Text(SelectedRow?.Entry.CanToggle == true && !Advanced ? "Autoruns_ControlHint" : "Autoruns_ReadOnly");
    public bool CanScan => IsIdle && (!Advanced || LicenseAccepted);
    public bool CanEnable => CanWrite && !Advanced && SelectedRow?.Entry is { CanToggle: true, Enabled: false };
    public bool CanDisable => CanWrite && !Advanced && SelectedRow?.Entry is { CanToggle: true, Enabled: true };

    public AutorunsViewModel(IAutorunService service, IDialogService dialogs, ILocalizationService localization,
        IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks, "Nav_Management")
    { _service = service; _dialogs = dialogs; _localization = localization; }

    /// <summary>First entry loads native startup sources; mode changes and consent load through the refresh command.</summary>
    public Task LoadAsync() => _loaded || Advanced ? Task.CompletedTask : RefreshAsync();

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task RefreshAsync() => CanScan ? ExecuteAsync(ReadAsync) : Task.CompletedTask;

    private async Task ReadAsync()
    {
        ClearInventory();
        var inventory = await _service.ReadAsync(Advanced ? AutorunView.Advanced : AutorunView.Startup, LicenseAccepted, CancellationToken.None);
        _inventory = inventory; _loaded = true;
        _allRows = inventory.Entries.OrderBy(entry => entry.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Location, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).Select(entry => new AutorunRow(entry)).ToArray();
        Categories.Clear();
        foreach (var category in _allRows.Select(row => row.Entry.Category).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)) Categories.Add(category);
        SelectedCategory = null;
        ApplyFilter(); OnPropertyChanged(string.Empty);
    }

    private void ClearInventory()
    { _inventory = null; _allRows = []; Rows.Clear(); Groups.Clear(); Categories.Clear(); SelectedRow = null; OnPropertyChanged(string.Empty); }

    partial void OnAdvancedChanged(bool value)
    {
        ClearInventory(); _loaded = false; NotifyActions();
        if (CanScan) _ = RefreshCommand.ExecuteAsync(null);
    }
    // First consent starts the pending advanced scan through the same guarded operation as Refresh.
    partial void OnLicenseAcceptedChanged(bool value)
    {
        OnPropertyChanged(nameof(EmptyLabel)); OnPropertyChanged(nameof(CanScan));
        if (value && Advanced && _inventory == null && CanScan) _ = RefreshCommand.ExecuteAsync(null);
    }
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string? value) => ApplyFilter();
    partial void OnStateFilterChanged(int value) => ApplyFilter();
    partial void OnHideVerifiedMicrosoftChanged(bool value) => ApplyFilter();
    partial void OnSelectedRowChanged(AutorunRow? value) { OnPropertyChanged(string.Empty); NotifyActions(); }

    /// <summary>Filters the retained snapshot; changing filters never starts another OS scan.</summary>
    private void ApplyFilter()
    {
        var selected = SelectedRow;
        Rows.Clear();
        Groups.Clear();
        var grouped = new Dictionary<AutorunScope, List<AutorunRow>>();
        foreach (var row in _allRows)
        {
            var entry = row.Entry;
            if (SelectedCategory != null && entry.Category != SelectedCategory) continue;
            if (StateFilter == 1 && entry.Enabled != true || StateFilter == 2 && entry.Enabled != false || StateFilter == 3 && entry.Enabled.HasValue) continue;
            if (Advanced && HideVerifiedMicrosoft && row.IsVerifiedMicrosoft) continue;
            if (!string.IsNullOrWhiteSpace(SearchText) && !row.SearchContent.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase)) continue;
            Rows.Add(row);
            if (!grouped.TryGetValue(row.Scope, out var group)) grouped.Add(row.Scope, group = []);
            group.Add(row);
        }
        foreach (var scope in Enum.GetValues<AutorunScope>())
            if (grouped.TryGetValue(scope, out var group)) Groups.Add(new(scope, group, _localization));
        SelectedRow = selected != null && Rows.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(CountSummary)); OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void ClearFilters() { SearchText = ""; SelectedCategory = null; StateFilter = 0; HideVerifiedMicrosoft = false; }

    [RelayCommand(CanExecute = nameof(CanEnable))] private Task EnableAsync() => CanEnable ? ChangeAsync(true) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(CanDisable))] private Task DisableAsync() => CanDisable ? ChangeAsync(false) : Task.CompletedTask;

    private Task ChangeAsync(bool enabled) => ExecuteAsync(async () =>
    {
        var entry = SelectedRow?.Entry ?? throw new InvalidOperationException("Select a startup entry first.");
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = enabled ? EnableLabel : DisableLabel, Message = Text("Autoruns_Confirm"),
            Items = [entry.Name, entry.Location, entry.Command],
            ConfirmButtonText = enabled ? EnableLabel : DisableLabel, CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await _service.SetEnabledAsync(entry.Id, enabled, CancellationToken.None);
        await ReadAsync();
        Status = Text("Autoruns_Changed");
    }, write: true);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task OpenWindowsSettingsAsync() => ExecuteAsync(_service.OpenStartupSettingsAsync);

    protected override void NotifyActions()
    {
        RefreshCommand.NotifyCanExecuteChanged(); EnableCommand.NotifyCanExecuteChanged(); DisableCommand.NotifyCanExecuteChanged();
        OpenWindowsSettingsCommand.NotifyCanExecuteChanged(); ClearFiltersCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEnable)); OnPropertyChanged(nameof(CanDisable)); OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Refreshes group labels in place when the application language changes.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is "" or null)
            foreach (var group in Groups) group.RefreshText();
    }
}

/// <summary>Separates personal, shared and built-in system scopes without resolving or guessing account identities.</summary>
public enum AutorunScope { User, AllUsers, System, Unspecified }

/// <summary>Groups references to the filtered rows and presents their localized scope and count.</summary>
public sealed class AutorunGroup(AutorunScope scope, IReadOnlyList<AutorunRow> rows, ILocalizationService localization) : ObservableObject
{
    public AutorunScope Scope { get; } = scope;
    public IReadOnlyList<AutorunRow> Rows { get; } = rows;
    public string Header => $"{localization.GetString($"Autoruns_Scope_{Scope}")} ({Rows.Count})";
    public void RefreshText() => OnPropertyChanged(nameof(Header));
}

/// <summary>Retains the authoritative registration and builds searchable display text once per scan.</summary>
public sealed class AutorunRow(AutorunEntry entry)
{
    public AutorunEntry Entry { get; } = entry;
    public AutorunScope Scope { get; } = ClassifyScope(entry);
    public string StateGlyph => Entry.Enabled switch { true => "✓", false => "−", _ => "?" };
    public string Subtitle => Entry.Category + " · " + Entry.Profile;
    /// <summary>Matches Microsoft's reported signer names, never an arbitrary company-name substring.</summary>
    public bool IsVerifiedMicrosoft
    {
        get
        {
            var signer = Entry.Signer.Trim();
            return signer.Equals("(Verified) Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ||
                signer.Equals("(Verified) Microsoft Windows", StringComparison.OrdinalIgnoreCase) ||
                signer.Equals("(Verified) Microsoft Windows Publisher", StringComparison.OrdinalIgnoreCase) ||
                signer.Equals("(Verified) Microsoft Windows Hardware Compatibility Publisher", StringComparison.OrdinalIgnoreCase);
        }
    }
    public string SearchContent { get; } = string.Join("\n", entry.Name, entry.Category, entry.Location, entry.Command, entry.ImagePath, entry.Publisher, entry.Signer, entry.Profile, entry.Details);

    /// <summary>Uses reported account/scope aliases first, falling back only to an explicit registry hive.</summary>
    private static AutorunScope ClassifyScope(AutorunEntry entry)
    {
        var profile = entry.Profile.Trim().Replace(" ", "").ToUpperInvariant();
        if (profile is "SYSTEM" or "LOCALSYSTEM" or ".DEFAULT" or "S-1-5-18" or "S-1-5-19" or "S-1-5-20" or
            "LOCALSERVICE" or "NETWORKSERVICE" or "NTAUTHORITY\\SYSTEM" or "NTAUTHORITY\\LOCALSERVICE" or "NTAUTHORITY\\NETWORKSERVICE")
            return AutorunScope.System;
        if (profile is "LOCALMACHINE" or "ALLUSERS") return AutorunScope.AllUsers;
        if (profile.Length > 0) return AutorunScope.User;
        if (entry.Location.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
            entry.Location.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)) return AutorunScope.AllUsers;
        if (entry.Location.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
            entry.Location.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)) return AutorunScope.User;
        return AutorunScope.Unspecified;
    }
}
