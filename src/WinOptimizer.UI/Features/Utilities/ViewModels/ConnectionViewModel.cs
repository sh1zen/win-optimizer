using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>A localized metric displayed alongside the live traffic graph.</summary>
public sealed record ConnectionTrafficStat(string Label, string Value);
public sealed record ConnectionVpnCard(string Name, string Details);

/// <summary>Presents Windows daily history and live traffic; DNS writes use the existing setting owner.</summary>
public partial class ConnectionViewModel : UtilityViewModel
{
    private const string DnsSettingId = "gaming-dns-server";
    private readonly IConnectionService _service;
    private readonly IConnectionTrafficService _traffic;
    private readonly ICompatibleSettingsRegistry _settings;
    private readonly ISettingApplicationService _application;
    private readonly ILocalizationService _localization;
    private ConnectionSnapshot? _snapshot;
    private ConnectionUsageHistory? _history;
    private ConnectionLiveTraffic? _live;
    private IReadOnlyList<ComboBoxOption> _dnsOptions = [];
    protected override string TitleKey => "Utilities_Connection";
    [ObservableProperty] public partial int SelectedDnsIndex { get; set; } = -1;
    public bool HasSnapshot => _snapshot != null;
    public string HistoryTitle => Text("Connection_HistoryTitle");
    public string HistoryHint => Text("Connection_HistoryHint");
    public string LiveTitle => Text("Connection_LiveTitle");
    public string LiveHint => Text("Connection_LiveHint");
    public string SentLabel => Text("Connection_SentBytes");
    public string ReceivedLabel => Text("Connection_ReceivedBytes");
    public string NowLabel => Text("Connection_Now");
    public string FifteenMinutesAgoLabel => Text("Connection_FifteenMinutesAgo");
    public string LiveDiagnostic { get; private set; } = "";
    public bool HasLiveDiagnostic => LiveDiagnostic.Length > 0;
    public string HistoryDiagnostic => _history?.Diagnostic ?? "";
    public bool HasHistoryDiagnostic => HistoryDiagnostic.Length > 0;
    public IReadOnlyList<ConnectionDailyUsage> HistoryDays => _history?.Days ?? [];
    public IReadOnlyList<ConnectionTrafficInterval> LiveIntervals => _live?.Intervals ?? [];
    public DateTimeOffset? LiveCapturedAt => _live?.CapturedAt;
    public string LiveScale => FormatRate(LiveIntervals.SelectMany(interval => new[]
        { interval.UploadBytesPerSecond, interval.DownloadBytesPerSecond }).Max() ?? 0);
    /// <summary>The largest daily total sets the shared scale for stacked upload/download segments.</summary>
    public decimal HistoryMaximumBytes => HistoryDays.Select(day => (day.BytesSent ?? 0) + (day.BytesReceived ?? 0)).DefaultIfEmpty().Max();
    public string HistoryScale => FormatBytes(HistoryMaximumBytes);
    public string HistoryStart => HistoryDays.FirstOrDefault()?.Date.ToString("d MMM", Culture) ?? "";
    public string HistoryEnd => HistoryDays.LastOrDefault()?.Date.ToString("d MMM", Culture) ?? "";
    public string HistoryTotal => HistoryDays.Any(day => day.BytesSent.HasValue)
        ? FormatBytes(HistoryDays.Sum(day => (day.BytesSent ?? 0) + (day.BytesReceived ?? 0))) : Text("Utilities_Unknown");
    public string HistorySummary => $"{SentLabel}: {FormatHistoryTotal(sent: true)} · {ReceivedLabel}: {FormatHistoryTotal(sent: false)}";
    public string LiveCoverage => _live?.Intervals.LastOrDefault() is { } latest
        ? string.Format(Culture, Text("Connection_Coverage"), latest.MeasuredAdapters, latest.TotalAdapters) : Text("Connection_Waiting");
    public IReadOnlyList<ConnectionTrafficStat> LiveStats
    {
        get
        {
            var latest = LiveIntervals.LastOrDefault();
            var measured = LiveIntervals.Select(interval => interval.Totals).OfType<ConnectionTrafficTotals>().ToArray();
            return
            [
                new(Text("Connection_DownloadRate"), FormatRate(latest?.DownloadBytesPerSecond)),
                new(Text("Connection_UploadRate"), FormatRate(latest?.UploadBytesPerSecond)),
                new(Text("Connection_ReceivedWindow"), measured.Length == 0 ? Text("Utilities_Unknown") : FormatBytes(measured.Sum(totals => totals.BytesReceived))),
                new(Text("Connection_SentWindow"), measured.Length == 0 ? Text("Utilities_Unknown") : FormatBytes(measured.Sum(totals => totals.BytesSent)))
            ];
        }
    }
    public string DnsTitle => Text("Connection_Dns");
    public string DnsHint => Text("Connection_DnsHint");
    public string DnsApplyLabel => Text("Connection_ApplyDns");
    public string VpnTitle => Text("Connection_Vpn");
    public string SavedWifiLabel => Text("Connection_SavedWifi");
    public string VpnHint => Text("Connection_VpnHint");
    public string ManageVpnLabel => Text("Connection_ManageVpn");
    public string EmptyVpn => _snapshot is { VpnProfiles.Count: 0, VpnDiagnostic.Length: 0 } ? Text("Connection_NoVpn") : "";
    public string VpnDiagnostic => _snapshot?.VpnDiagnostic ?? "";
    public bool HasVpnDiagnostic => VpnDiagnostic.Length > 0;
    public bool ShowEmptyVpn => EmptyVpn.Length > 0;
    public bool ShowDnsUnavailable => DnsUnavailable.Length > 0;
    public string CapturedAt => _snapshot == null ? "" : string.Format(Culture, Text("Connection_CapturedAt"),
        _snapshot.CapturedAt.ToLocalTime().ToString("G", Culture));
    public IReadOnlyList<string> DnsChoices => _dnsOptions.Select(option => Text(option.DisplayName)).ToArray();
    public string DnsUnavailable => HasSnapshot && _dnsOptions.Count == 0 ? Text("Connection_DnsUnavailable") : "";
    public IReadOnlyList<ConnectionVpnCard> VpnProfiles => _snapshot?.VpnProfiles.Select(profile => new ConnectionVpnCard(
        profile.Name, $"{profile.Server} · {VpnStatus(profile.Status)} · {Text(profile.AllUsers ? "Connection_AllUsers" : "Connection_CurrentUser")} · " +
        Text(profile.SplitTunneling ? "Connection_SplitTunnel" : "Connection_FullTunnel"))).ToArray() ?? [];
    private CultureInfo Culture => CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(_localization.CurrentLanguage)
        ? CultureInfo.CurrentCulture.Name : _localization.CurrentLanguage);

    public ConnectionViewModel(IConnectionService service, IConnectionTrafficService traffic, ICompatibleSettingsRegistry settings,
        ISettingApplicationService application, ILocalizationService localization, IApplicationModeService mode,
        IDispatcherService dispatcher, ITaskProgressService tasks) : base(localization, mode, dispatcher, tasks, "Nav_Management")
    { _service = service; _traffic = traffic; _settings = settings; _application = application; _localization = localization; }

    public Task LoadAsync() => _snapshot == null ? RefreshAsync() : Task.CompletedTask;
    partial void OnSelectedDnsIndexChanged(int value) => ApplyDnsCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => ExecuteAsync(async () =>
    {
        if (!_settings.IsInitialized) await _settings.InitializeAsync();
        _dnsOptions = _settings.GetById(DnsSettingId)?.ComboBox?.Options ?? [];
        var snapshot = _service.ReadAsync();
        var history = _traffic.ReadHistoryAsync(CancellationToken.None);
        await Task.WhenAll(snapshot, history);
        _history = await history;
        Publish(await snapshot);
    });

    private bool CanApplyDns() => CanWrite && SelectedDnsIndex >= 0 && SelectedDnsIndex < _dnsOptions.Count;
    [RelayCommand(CanExecute = nameof(CanApplyDns))]
    private Task ApplyDnsAsync() => ExecuteAsync(async () =>
    {
        var result = await _application.ApplySettingAsync(new ApplySettingRequest
            { SettingId = DnsSettingId, Enable = true, Value = SelectedDnsIndex });
        if (!result.Success) throw new UtilityOperationException(result.ErrorMessage ?? Text("Connection_DnsFailed"));
        Publish(await _service.ReadAsync());
        Status = Text("Connection_DnsApplied");
    }, write: true);

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task ManageVpnAsync() => ExecuteAsync(_service.OpenVpnSettingsAsync, write: true);

    private void Publish(ConnectionSnapshot snapshot)
    { _snapshot = snapshot; OnPropertyChanged(string.Empty); NotifyActions(); }

    protected override void NotifyActions()
    {
        RefreshCommand.NotifyCanExecuteChanged(); ApplyDnsCommand.NotifyCanExecuteChanged();
        ManageVpnCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Publishes lightweight samples without making DNS/VPN controls busy every two seconds.</summary>
    public async Task SampleTrafficAsync(CancellationToken cancellationToken)
    {
        try
        {
            var live = await _traffic.SampleAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _live = live;
            LiveDiagnostic = "";
        }
        catch (Exception ex) when (ex is UtilityOperationException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { cancellationToken.ThrowIfCancellationRequested(); LiveDiagnostic = ex.Message; _live = null; }
        foreach (var property in new[] { nameof(LiveIntervals), nameof(LiveStats), nameof(LiveCoverage),
            nameof(LiveScale), nameof(LiveDiagnostic), nameof(HasLiveDiagnostic), nameof(LiveCapturedAt) }) OnPropertyChanged(property);
    }

    public string DayDetails(ConnectionDailyUsage day) => day.Date.ToString("D", Culture) + "\n" +
        $"{SentLabel}: {FormatOptionalBytes(day.BytesSent)}\n{ReceivedLabel}: {FormatOptionalBytes(day.BytesReceived)}\n" +
        string.Format(Culture, Text("Connection_ProfileCoverage"), day.ReportingProfiles, day.TotalProfiles);

    private string FormatHistoryTotal(bool sent) => HistoryDays.Any(day => day.BytesSent.HasValue)
        ? FormatBytes(HistoryDays.Sum(day => (sent ? day.BytesSent : day.BytesReceived) ?? 0)) : Text("Utilities_Unknown");
    private string FormatOptionalBytes(decimal? value) => value.HasValue ? FormatBytes(value.Value) : Text("Utilities_Unknown");
    private string FormatRate(decimal? value) => value.HasValue ? FormatBytes(value.Value) + "/s" : Text("Connection_Waiting");

    private string FormatBytes(decimal value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "N2", Culture) + " " + units[unit];
    }

    private string VpnStatus(string status) => status switch
    {
        "Connected" => Text("Connection_Connected"), "Disconnected" => Text("Connection_Disconnected"),
        _ => status
    };
}
