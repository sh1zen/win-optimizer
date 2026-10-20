using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Customize.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Customize.ViewModels;

/// <summary>Retains the discovered registration; UI state changes only after verified registry readback.</summary>
public partial class ContextMenuEntryViewModel(ContextMenuEntry entry, Func<string, string> text) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Location), nameof(IsEnabled))]
    public partial ContextMenuEntry Entry { get; set; } = entry;
    [ObservableProperty] public partial bool CanWrite { get; set; }
    public string Name => Entry.Name;
    public string Location => Entry.Location;
    public bool IsEnabled => Entry.IsEnabled;
    public string ContextLabel
    {
        get
        {
            var target = Entry.TargetType.ToUpperInvariant() switch
            {
                "*" => text("ContextMenu_TargetFiles"),
                "ALLFILESYSTEMOBJECTS" => text("ContextMenu_TargetFilesAndFolders"),
                "DIRECTORY" or "FOLDER" => text("ContextMenu_TargetFolders"),
                @"DIRECTORY\BACKGROUND" => text("ContextMenu_TargetFolderBackground"),
                "DESKTOPBACKGROUND" => text("ContextMenu_TargetDesktop"),
                "DRIVE" => text("ContextMenu_TargetDrives"),
                _ => text("ContextMenu_TargetFileType") + (Entry.TargetType.Length == 0 ? ""
                    : " (" + Entry.TargetType.Replace(@"SystemFileAssociations\", "", StringComparison.OrdinalIgnoreCase) + ")"),
            };
            return target + " · " + text(Entry.IsMachineWide ? "ContextMenu_ScopeMachine" : "ContextMenu_ScopeUser");
        }
    }
    public string Details => Name + "\n" + Location;
}

/// <summary>Shares verified state and live counts between the program toggle and its individual registrations.</summary>
public sealed partial class ContextMenuProgramViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, string> _text;
    public string Name { get; }
    public IReadOnlyList<ContextMenuEntryViewModel> Entries { get; }
    public bool IsSingle => Entries.Count == 1;
    public bool IsMultiple => !IsSingle;
    public ContextMenuEntryViewModel First => Entries[0];
    public int EnabledCount => Entries.Count(entry => entry.IsEnabled);
    public bool HasEnabledEntries => Entries.Any(entry => entry.IsEnabled);
    public bool CanWrite => Entries.All(entry => entry.CanWrite);
    public string Summary => string.Format(_text("ContextMenu_EntrySummary"), Entries.Count, EnabledCount);
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon), nameof(ShowFallbackIcon))]
    public partial ImageSource? Icon { get; set; }
    public bool HasIcon => Icon != null;
    public bool ShowFallbackIcon => !HasIcon;

    public ContextMenuProgramViewModel(string name, IReadOnlyList<ContextMenuEntryViewModel> entries, Func<string, string> text)
    {
        Name = name; Entries = entries; _text = text;
        foreach (var entry in Entries) entry.PropertyChanged += OnEntryChanged;
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContextMenuEntryViewModel.CanWrite)) OnPropertyChanged(nameof(CanWrite));
        if (e.PropertyName != nameof(ContextMenuEntryViewModel.IsEnabled)) return;
        OnPropertyChanged(nameof(EnabledCount)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(HasEnabledEntries));
    }

    public void Dispose()
    {
        foreach (var entry in Entries) entry.PropertyChanged -= OnEntryChanged;
    }
}

/// <summary>Owns on-demand discovery, program grouping, filtering and verified immediate toggle operations.</summary>
public partial class ContextMenuViewModel : UtilityViewModel
{
    private readonly IContextMenuService _service;
    private readonly IProgramIconSource _icons;
    private IReadOnlyList<ContextMenuEntryViewModel> _entries = [];
    private IReadOnlyList<ContextMenuProgramViewModel> _allPrograms = [];
    private bool _hasLoaded;
    protected override string TitleKey => "ContextMenu_Title";
    public ObservableCollection<ContextMenuProgramViewModel> Programs { get; } = [];
    [ObservableProperty] public partial string Query { get; set; } = "";
    public string SearchLabel => Text("ContextMenu_Search");
    public string Hint => Text("ContextMenu_Hint");
    public string ShortHint => Text("ContextMenu_ShortHint");
    public string ResultSummary => string.Format(Text("ContextMenu_ResultSummary"), Programs.Count, _allPrograms.Count,
        Programs.Sum(program => program.Entries.Count));
    public string EmptyTitle => Text("ContextMenu_EmptyTitle");
    public string EmptyLabel => Text("ContextMenu_Empty");
    public bool ShowEmpty => _hasLoaded && !IsBusy && Programs.Count == 0;

    public ContextMenuViewModel(IContextMenuService service, ILocalizationService localization,
        IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks, IProgramIconSource icons)
        : base(localization, mode, dispatcher, tasks, "Nav_Customize") { _service = service; _icons = icons; }

    /// <summary>Page entry and Refresh share the same serialized discovery path.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    public Task RefreshAsync() => ExecuteAsync(async () =>
    {
        var entries = await _service.ReadAsync();
        foreach (var program in _allPrograms) program.Dispose();
        _entries = entries.Select(entry => new ContextMenuEntryViewModel(entry, Text)).ToArray();
        _allPrograms = _entries.GroupBy(entry => entry.Entry.ProgramName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ContextMenuProgramViewModel(group.Key, group.ToArray(), Text)).ToArray();
        _hasLoaded = true;
        Filter();
        await Task.WhenAll(_allPrograms.Select(LoadIconAsync));
    });

    /// <summary>Tries each distinct registered binary once per program, preferring executables to COM libraries.</summary>
    private async Task LoadIconAsync(ContextMenuProgramViewModel program)
    {
        var paths = program.Entries.Select(entry => entry.Entry.ProgramPath).OfType<string>()
            .Where(path => path.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        foreach (var path in paths)
        {
            var icon = await _icons.LoadAsync(path);
            if (icon == null) continue;
            program.Icon = icon;
            return;
        }
    }

    public Task SetEnabledAsync(ContextMenuEntryViewModel entry, bool enabled) => ExecuteAsync(async () =>
    {
        entry.Entry = await _service.SetEnabledAsync(entry.Entry, enabled);
        Status = Text("ContextMenu_Changed");
    }, write: true);

    /// <summary>Applies the requested state to the full program, retaining verified successes and reporting individual failures.</summary>
    public Task SetProgramEnabledAsync(ContextMenuProgramViewModel program, bool enabled) => ExecuteAsync(async () =>
    {
        var failures = new List<string>();
        foreach (var entry in program.Entries)
        {
            if (entry.IsEnabled == enabled) continue;
            try { entry.Entry = await _service.SetEnabledAsync(entry.Entry, enabled); }
            catch (Exception ex) when (ex is UtilityOperationException or IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
            { failures.Add(entry.Name + " (" + entry.Location + "): " + ex.Message); }
        }
        IsError = failures.Count > 0;
        Status = IsError ? string.Format(Text("ContextMenu_ProgramChangeFailed"), program.Name, string.Join(Environment.NewLine, failures))
            : Text("ContextMenu_Changed");
    }, write: true);

    partial void OnQueryChanged(string value) => Filter();

    /// <summary>Filters matching programs/registrations while retaining the program's original single/expander shape.</summary>
    private void Filter()
    {
        Programs.Clear();
        foreach (var program in _allPrograms)
        {
            bool matchesProgram = program.Name.Contains(Query, StringComparison.OrdinalIgnoreCase);
            bool matchesEntry = program.Entries.Any(entry => entry.Name.Contains(Query, StringComparison.OrdinalIgnoreCase)
                || entry.Location.Contains(Query, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(Query) && !matchesProgram && !matchesEntry) continue;
            if (!string.IsNullOrWhiteSpace(Query) && !matchesProgram && matchesEntry) program.IsExpanded = true;
            Programs.Add(program);
        }
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ResultSummary));
    }

    protected override void NotifyActions()
    {
        foreach (var entry in _entries) entry.CanWrite = CanWrite;
        RefreshCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowEmpty));
    }
}
