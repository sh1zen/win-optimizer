using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;

namespace WinOptimizer.UI.Features.SystemInformation.ViewModels;

/// <summary>Owns the inventory snapshot, category navigation and collection lifetime.</summary>
public partial class SystemInformationViewModel : ObservableObject, IDisposable
{
    private readonly ISystemInformationService _service;
    private readonly ILocalizationService _localization;
    private readonly List<SystemInformationCategoryViewModel> _sections = [];
    private CancellationTokenSource? _operation;
    private SystemInformationSnapshot? _snapshot;
    private bool _disposed;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string Status { get; private set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsDetail), nameof(HasSelection), nameof(SelectedTitle), nameof(SelectedIcon),
        nameof(SelectedCount), nameof(SelectedItems), nameof(HasEmptyCategory), nameof(EmptyMessage))]
    public partial SystemInformationCategoryViewModel? SelectedCategory { get; set; }

    public ObservableCollection<SystemInformationCategoryViewModel> Categories { get; } = [];
    public IReadOnlyList<SystemInformationCategoryViewModel> Overview => _sections;
    public bool IsIdle => !IsBusy;
    public bool HasSelection => SelectedCategory is not null;
    public bool IsOverview => SelectedCategory?.IsOverview == true;
    public bool IsDetail => SelectedCategory?.IsOverview == false;
    public string SelectedTitle => SelectedCategory?.Title ?? Heading;
    public FluentIcons.Common.Icon SelectedIcon => SelectedCategory?.Icon ?? FluentIcons.Common.Icon.Info;
    public string SelectedCount => SelectedCategory?.CountText ?? "";
    public IReadOnlyList<SystemInformationDeviceViewModel> SelectedItems => SelectedCategory?.Items ?? [];
    public bool HasEmptyCategory => SelectedCategory is { IsOverview: false, Items.Count: 0 };
    public string EmptyMessage => SelectedCategory?.EmptyMessage ?? "";
    public string Heading => Text("Nav_SystemInformation");
    public string Description => Text("SystemInfo_Description");
    public string RefreshLabel => Text("Restore_Refresh");
    public string CancelLabel => Text("Button_Cancel");
    public string CategoriesLabel => Text("SystemInfo_Categories");
    public bool HasNotices => _sections.Any(section => section.Section?.Error is not null);
    public string CollectedAt => _snapshot is null ? "" : string.Format(Text("SystemInfo_CollectedAt"),
        _snapshot.CollectedAt.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture));
    public string Notices => string.Join(Environment.NewLine, _sections.Where(section => section.Section?.Error is not null)
        .Select(section => $"{section.Title}: {section.Section!.Error}"));

    public SystemInformationViewModel(ISystemInformationService service, ILocalizationService localization)
    {
        _service = service;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Reuses the session's inventory on navigation; the Refresh command always recollects.</summary>
    public Task InitializeAsync()
    {
        if (_disposed || _snapshot is not null) return Task.CompletedTask;
        if (_service.CachedSnapshot is not { } cached) return RefreshAsync();
        PublishSnapshot(cached);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_disposed || IsBusy) return;
        IsBusy = true;
        using var owner = new CancellationTokenSource();
        _operation = owner;
        Status = Text("SystemInfo_Loading");
        try
        {
            var snapshot = await _service.ReadAsync(owner.Token);
            owner.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            PublishSnapshot(snapshot);
        }
        catch (OperationCanceledException) { if (!_disposed) Status = Text("SystemInfo_Cancelled"); }
        catch (SystemInformationException ex) { if (!_disposed) Status = ex.Message; }
        finally { _operation = null; IsBusy = false; }
    }

    /// <summary>Builds localized presentation from the same cached or newly collected typed records.</summary>
    private void PublishSnapshot(SystemInformationSnapshot snapshot)
    {
        var selectedCategory = SelectedCategory?.Section?.Category;
        _snapshot = snapshot;
        _sections.Clear();
        foreach (var section in snapshot.Sections)
            _sections.Add(new(section, _localization));
        Categories.Clear();
        Categories.Add(new(null, _localization));
        foreach (var section in _sections) Categories.Add(section);
        SelectedCategory = Categories.FirstOrDefault(category => category.Section?.Category == selectedCategory) ?? Categories[0];
        var failed = snapshot.Sections.Count(section => section.Error is not null);
        Status = Text(failed == 0 ? "SystemInfo_Complete" : failed == snapshot.Sections.Count ? "SystemInfo_UnavailableAll" : "SystemInfo_Partial");
        OnPropertyChanged(nameof(Overview));
        OnPropertyChanged(nameof(CollectedAt));
        OnPropertyChanged(nameof(Notices));
        OnPropertyChanged(nameof(HasNotices));
    }

    [RelayCommand]
    private void Cancel() { _operation?.Cancel(); if (IsBusy) Status = Text("SystemInfo_Cancelling"); }

    private string Text(string key) => _localization.GetString(key);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var category in Categories) category.RefreshText();
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        _operation?.Cancel();
        Categories.Clear();
        SelectedCategory = null;
        _sections.Clear();
        _snapshot = null;
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
