using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>Localized presentation of a profile; password values are never included in string diagnostics.</summary>
public sealed class SavedWifiCard(SavedWifiProfile profile, string passwordLabel, string password)
{
    public string Name => profile.Name;
    public string Details => string.Join(" · ", new[] { profile.Adapter, profile.Authentication, profile.Encryption }
        .Where(value => value.Length > 0));
    public string PasswordLabel { get; } = passwordLabel;
    public string Password { get; } = password;
}

/// <summary>Reads only on page entry/refresh and releases credential references when leaving the page.</summary>
public partial class SavedWifiViewModel(ISavedWifiService service, ILocalizationService localization,
    IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
    : UtilityViewModel(localization, mode, dispatcher, tasks, "Nav_Management")
{
    private SavedWifiSnapshot? _snapshot;
    private CancellationToken _session;
    private int _generation;
    protected override string TitleKey => "Connection_SavedWifi";
    public string BackLabel => Text("Utilities_Connection");
    public string EmptyLabel => Text("Connection_NoSavedWifi");
    public bool ShowEmpty => _snapshot is { Profiles.Count: 0, Diagnostic.Length: 0 };
    public string Diagnostic => _snapshot?.Diagnostic ?? "";
    public bool HasDiagnostic => Diagnostic.Length > 0;
    public IReadOnlyList<SavedWifiCard> Profiles => _snapshot?.Profiles.Select(profile => new SavedWifiCard(profile,
        Text("Connection_WifiPassword"), profile.PasswordState == WifiPasswordState.Available && profile.Password is { } password ? password :
        Text(profile.PasswordState switch
        {
            WifiPasswordState.OpenNetwork => "Connection_WifiOpen",
            WifiPasswordState.NotStored => "Connection_WifiNotStored",
            _ => "Connection_WifiUnavailable"
        }))).ToArray() ?? [];

    /// <summary>Binds all reads to the visible page's cancellation scope.</summary>
    public Task LoadAsync(CancellationToken cancellationToken)
    { _session = cancellationToken; return RefreshAsync(); }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => ExecuteAsync(async () =>
    {
        var generation = _generation;
        _snapshot = null;
        OnPropertyChanged(string.Empty);
        try
        {
            var snapshot = await service.ReadAsync(_session);
            if (_session.IsCancellationRequested || generation != _generation) return;
            _snapshot = snapshot;
            OnPropertyChanged(string.Empty);
        }
        catch (OperationCanceledException) when (_session.IsCancellationRequested) { }
        catch (UtilityOperationException) when (_session.IsCancellationRequested || generation != _generation) { }
    });

    /// <summary>Rejects late reads and removes the page's in-memory profile snapshot.</summary>
    public void Clear()
    {
        _generation++;
        _snapshot = null;
        Status = "";
        OnPropertyChanged(string.Empty);
    }

    protected override void NotifyActions() => RefreshCommand.NotifyCanExecuteChanged();
}
