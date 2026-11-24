using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;
using static WinOptimizer.UI.Features.Common.Utilities.ByteSizeFormatter;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

/// <summary>A localized metric displayed alongside the live traffic graph.</summary>
public sealed record ConnectionTrafficStat(string Label, string Value);

/// <summary>Presents Windows daily data usage and live connection traffic.</summary>
public partial class ConnectionViewModel : UtilityViewModel
{
    private readonly IConnectionTrafficService _traffic;
    private readonly ILocalizationService _localization;
    private ConnectionUsageHistory? _history;
    private ConnectionLiveTraffic? _live;
    private LiveSummary _liveSummary;
    protected override string TitleKey => "Utilities_Connection";
    [ObservableProperty] public partial bool IsHistoryLoading { get; private set; }
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
    public string HistoryDiagnostic { get; private set; } = "";
    public bool HasHistoryDiagnostic => HistoryDiagnostic.Length > 0;
    public IReadOnlyList<ConnectionDailyUsage> HistoryDays => _history?.Days ?? [];
    public IReadOnlyList<ConnectionTrafficInterval> LiveIntervals => _live?.Intervals ?? [];
    public DateTimeOffset? LiveCapturedAt => _live?.CapturedAt;
    public decimal LiveMaximumRate => _liveSummary.MaximumRate;
    public string LiveScale => FormatRate(LiveMaximumRate);
    /// <summary>The largest daily total sets the shared scale for stacked upload/download segments.</summary>
    public decimal HistoryMaximumBytes => HistoryDays.Select(day => (day.BytesSent ?? 0) + (day.BytesReceived ?? 0)).DefaultIfEmpty().Max();
    public string HistoryScale => FormatBytes(HistoryMaximumBytes, Culture);
    public string HistoryStart => HistoryDays.FirstOrDefault()?.Date.ToString("d MMM", Culture) ?? "";
    public string HistoryEnd => HistoryDays.LastOrDefault()?.Date.ToString("d MMM", Culture) ?? "";
    public string HistoryTotal => HistoryDays.Any(day => day.BytesSent.HasValue)
        ? FormatBytes(HistoryDays.Sum(day => (day.BytesSent ?? 0) + (day.BytesReceived ?? 0)), Culture) : Text("Utilities_Unknown");
    public string HistorySummary => $"{SentLabel}: {FormatHistoryTotal(sent: true)} · {ReceivedLabel}: {FormatHistoryTotal(sent: false)}";
    public string LiveCoverage => _live?.Intervals.LastOrDefault() is { } latest
        ? string.Format(Culture, Text("Connection_Coverage"), latest.MeasuredAdapters, latest.TotalAdapters) : Text("Connection_Waiting");
    public IReadOnlyList<ConnectionTrafficStat> LiveStats
    {
        get
        {
            var latest = LiveIntervals.LastOrDefault();
            return
            [
                new(Text("Connection_DownloadRate"), FormatRate(latest?.DownloadBytesPerSecond)),
                new(Text("Connection_UploadRate"), FormatRate(latest?.UploadBytesPerSecond)),
                new(Text("Connection_ReceivedWindow"), FormatOptionalBytes(_liveSummary.Received)),
                new(Text("Connection_SentWindow"), FormatOptionalBytes(_liveSummary.Sent))
            ];
        }
    }
    public string SavedWifiLabel => Text("Connection_SavedWifi");
    private CultureInfo Culture => CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(_localization.CurrentLanguage)
        ? CultureInfo.CurrentCulture.Name : _localization.CurrentLanguage);

    public ConnectionViewModel(IConnectionTrafficService traffic, ILocalizationService localization, IApplicationModeService mode,
        IDispatcherService dispatcher, ITaskProgressService tasks) : base(localization, mode, dispatcher, tasks, "Nav_Management")
    { _traffic = traffic; _localization = localization; }

    /// <summary>Reuses successful history on navigation and retries when a read failed.</summary>
    public Task LoadAsync()
    {
        if (RefreshCommand.ExecutionTask is { IsCompleted: false } loading) return loading;
        return _history == null && RefreshCommand.CanExecute(null)
            ? RefreshCommand.ExecuteAsync(null) : Task.CompletedTask;
    }
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => ExecuteAsync(RefreshHistoryAsync);

    /// <summary>Publishes the daily chart and its diagnostic independently of live samples.</summary>
    private async Task RefreshHistoryAsync()
    {
        IsHistoryLoading = true;
        try
        {
            _history = await _traffic.ReadHistoryAsync(CancellationToken.None);
            HistoryDiagnostic = _history.Diagnostic;
            OnPropertyChanged(nameof(HistoryDays));
            OnPropertyChanged(nameof(HistoryTotal));
            OnPropertyChanged(nameof(HistorySummary));
            OnPropertyChanged(nameof(HistoryMaximumBytes));
            OnPropertyChanged(nameof(HistoryScale));
            OnPropertyChanged(nameof(HistoryStart));
            OnPropertyChanged(nameof(HistoryEnd));
        }
        catch (Exception ex) when (ex is UtilityOperationException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { HistoryDiagnostic = ex.Message; }
        finally
        {
            OnPropertyChanged(nameof(HistoryDiagnostic));
            OnPropertyChanged(nameof(HasHistoryDiagnostic));
            IsHistoryLoading = false;
        }
    }

    protected override void NotifyActions() => RefreshCommand.NotifyCanExecuteChanged();

    /// <summary>Publishes lightweight samples for the live traffic chart.</summary>
    public async Task SampleTrafficAsync(CancellationToken cancellationToken)
    {
        try
        {
            var live = await _traffic.SampleAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _live = live;
            _liveSummary = SummarizeLive(live.Intervals);
            LiveDiagnostic = "";
        }
        catch (Exception ex) when (ex is UtilityOperationException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { cancellationToken.ThrowIfCancellationRequested(); LiveDiagnostic = ex.Message; _live = null; _liveSummary = default; }
        foreach (var property in new[] { nameof(LiveIntervals), nameof(LiveStats), nameof(LiveCoverage),
            nameof(LiveMaximumRate), nameof(LiveScale), nameof(LiveDiagnostic), nameof(HasLiveDiagnostic), nameof(LiveCapturedAt) })
            OnPropertyChanged(property);
    }

    /// <summary>Calculates the shared chart scale and measured byte totals once per sample.</summary>
    private static LiveSummary SummarizeLive(IReadOnlyList<ConnectionTrafficInterval> intervals)
    {
        decimal maximum = 0, sent = 0, received = 0;
        var measured = false;
        foreach (var interval in intervals)
        {
            if (interval.Totals is not { } totals) continue;
            measured = true;
            sent += totals.BytesSent;
            received += totals.BytesReceived;
            maximum = Math.Max(maximum, Math.Max(interval.UploadBytesPerSecond ?? 0, interval.DownloadBytesPerSecond ?? 0));
        }
        return new(maximum, measured ? sent : null, measured ? received : null);
    }

    private readonly record struct LiveSummary(decimal MaximumRate, decimal? Sent, decimal? Received);

    public string DayDetails(ConnectionDailyUsage day) => day.Date.ToString("D", Culture) + "\n" +
        $"{SentLabel}: {FormatOptionalBytes(day.BytesSent)}\n{ReceivedLabel}: {FormatOptionalBytes(day.BytesReceived)}\n" +
        string.Format(Culture, Text("Connection_ProfileCoverage"), day.ReportingProfiles, day.TotalProfiles);

    private string FormatHistoryTotal(bool sent) => HistoryDays.Any(day => day.BytesSent.HasValue)
        ? FormatBytes(HistoryDays.Sum(day => (sent ? day.BytesSent : day.BytesReceived) ?? 0), Culture) : Text("Utilities_Unknown");
    private string FormatOptionalBytes(decimal? value) => value.HasValue ? FormatBytes(value.Value, Culture) : Text("Utilities_Unknown");
    private string FormatRate(decimal? value) => value.HasValue ? FormatBytes(value.Value, Culture) + "/s" : Text("Connection_Waiting");
}
