using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Utils;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Utilities.ViewModels;

namespace WinOptimizer.UI.Features.Optimize.ViewModels;

/// <summary>Shows the latest and previous successful readings for one speed test metric.</summary>
public sealed record InternetSpeedMetric(Icon Icon, string Label, string Value, string Unit, string Previous)
{
    public string UnitSuffix => Unit.Length == 0 ? "" : " " + Unit;
    public bool HasPrevious => Previous.Length > 0;
}

/// <summary>Coordinates Internet measurements, connection settings and individually confirmed Windows network edits.</summary>
public partial class NetworkOptimizationViewModel : UtilityViewModel
{
    private readonly IOptimizationToolsService _service;
    private readonly IInternetSpeedTest _speedTest;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly NetworkTuningChoice _autoTuning;
    private readonly NetworkTuningChoice _rss;
    private InternetSpeedTestResult? _previousMeasurement;
    protected override string TitleKey => "OptimizeNetwork_Title";
    public IReadOnlyList<NetworkTuningChoice> Choices { get; }
    public IReadOnlyList<NetworkTuningGroup> Groups { get; }
    public string SettingsTitle => Text("OptimizeNetwork_SettingsTitle");
    public string SettingsSearchLabel => Text("Settings_Search");
    public string NoSettingsMatchesLabel => Text("Settings_NoMatches");
    [ObservableProperty] public partial string SettingsSearchText { get; set; } = "";
    [ObservableProperty] public partial bool HasNoSettingsMatches { get; private set; }
    [ObservableProperty] public partial bool ShowDnsSettings { get; private set; } = true;
    [ObservableProperty] public partial bool ShowVpnSettings { get; private set; } = true;

    /// <summary>Builds stable presentation choices without querying Windows until page entry.</summary>
    public NetworkOptimizationViewModel(IOptimizationToolsService service, IInternetSpeedTest speedTest, IDialogService dialogs,
        IConnectionService connection, ICompatibleSettingsRegistry settings, ISettingApplicationService application,
        ILocalizationService localization, IApplicationModeService mode, IDispatcherService dispatcher, ITaskProgressService tasks)
        : base(localization, mode, dispatcher, tasks, "Nav_Internet")
    {
        _service = service;
        _speedTest = speedTest;
        _dialogs = dialogs;
        _connection = connection;
        _settings = settings;
        _application = application;
        _localization = localization;
        Choices = NetworkTuningCatalog.All.Select(item => new NetworkTuningChoice(item, localization)).ToArray();
        _autoTuning = Choices.Single(choice => choice.Definition.Id == NetworkTuningCatalog.AutoTuningId);
        _rss = Choices.Single(choice => choice.Definition.Id == NetworkTuningCatalog.RssId);
        Groups = Choices.GroupBy(choice => choice.Definition.Owner)
            .OrderBy(group => group.Key != NetworkTuningOwner.DeliveryOptimization)
            .Select(group => new NetworkTuningGroup(group.Key, group.ToArray(), localization)).ToArray();
    }

    public string SpeedTestTitle => Text("SpeedTest_Title");
    public string SpeedTestDescription => Text("SpeedTest_Description");
    public string RunSpeedTestLabel => Text("SpeedTest_Start");
    public string CancelLabel => Text("Button_Cancel");

    partial void OnSettingsSearchTextChanged(string value) => ApplySettingsFilter();

    /// <summary>Filters the existing controls and keeps a group visible when it contains a match.</summary>
    private void ApplySettingsFilter()
    {
        var query = SearchHelper.Prepare(SettingsSearchText);
        ShowDnsSettings = query.Matches([DnsTitle, DnsHint, DnsSettingId]);
        ShowVpnSettings = query.Matches([VpnTitle, VpnHint]);
        var hasMatches = ShowDnsSettings || ShowVpnSettings;
        foreach (var group in Groups)
        {
            var groupTitle = group.Title;
            var groupHasMatches = false;
            foreach (var choice in group.Choices)
            {
                var definition = choice.Definition;
                choice.IsVisible = query.Matches([groupTitle, choice.Title, choice.Description, definition.Id, definition.Property, definition.Parameter]);
                groupHasMatches |= choice.IsVisible;
            }
            group.IsVisible = groupHasMatches;
            hasMatches |= groupHasMatches;
        }
        HasNoSettingsMatches = !hasMatches;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdatingNetwork), nameof(ShowStartSpeedTest))]
    public partial bool IsMeasuring { get; private set; }
    public bool ShowStartSpeedTest => !IsMeasuring;
    public bool IsUpdatingNetwork => IsBusy && !IsMeasuring;

    public InternetSpeedTestResult? Measurement { get; private set; }

    public InternetSpeedMetric DownloadMetric => Metric(Icon.ArrowDown, "Connection_DownloadRate", Measurement?.DownloadMbps, _previousMeasurement?.DownloadMbps, "Mbps");
    public InternetSpeedMetric UploadMetric => Metric(Icon.ArrowUp, "Connection_UploadRate", Measurement?.UploadMbps, _previousMeasurement?.UploadMbps, "Mbps");
    public InternetSpeedMetric LatencyMetric => Metric(Icon.Clock, "SpeedTest_Latency", Measurement?.LatencyMilliseconds, _previousMeasurement?.LatencyMilliseconds, "ms");

    private InternetSpeedMetric Metric(Icon icon, string key, double? latest, double? previous, string unit)
    {
        var culture = CultureInfo.GetCultureInfo(_localization.CurrentLanguage ?? "");
        string Format(double value) => value.ToString("0.##", culture);
        return new(icon, Text(key), latest is double value ? Format(value) : "—", latest.HasValue ? unit : "",
            previous is double prior ? string.Format(culture, Text("SpeedTest_Previous"), $"{Format(prior)} {unit}") : "");
    }

    /// <summary>Retains the last two successful results while a new test runs or is cancelled.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle), IncludeCancelCommand = true)]
    private Task RunSpeedTestAsync(CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        IsMeasuring = true;
        try
        {
            var result = await _speedTest.MeasureAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _previousMeasurement = Measurement;
            Measurement = result;
            OnPropertyChanged(nameof(Measurement));
            OnPropertyChanged(nameof(DownloadMetric));
            OnPropertyChanged(nameof(UploadMetric));
            OnPropertyChanged(nameof(LatencyMetric));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidDataException)
        {
            Status = Text("SpeedTest_Failed");
            IsError = true;
        }
        finally { IsMeasuring = false; }
    });

    public string ControlsLabel => string.Format(Text("OptimizeNetwork_Controls"), Choices.Count);
    public string EnhanceDescription => Text("OptimizeNetwork_EnhanceDescription");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTuningValue), nameof(RssValue), nameof(IsRssEnabled), nameof(CanEnhance))]
    public partial NetworkPerformanceState? State { get; private set; }
    public string EnhanceLabel => Text("OptimizeNetwork_Enhance");
    public string NetworkResultsHint => Text("OptimizeNetwork_ResultsHint");
    public string AutoTuningLabel => Text("OptimizeNetwork_AutoTuningLabel");
    public string RssLabel => Text("OptimizeNetwork_RssLabel");
    public string AutoTuningValue => State is { } state ? _autoTuning.FormatValue(state.AutoTuningLevel) : Text("Utilities_Unknown");
    public string RssValue => Text(State is { } state
        ? state.ReceiveSideScaling ? "Common_On" : "Common_Off" : "Utilities_Unknown");
    public bool IsRssEnabled => State?.ReceiveSideScaling == true;
    public bool CanEnhance => CanWrite && State != null;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public Task RefreshAsync() => ExecuteAsync(() => Task.WhenAll(RefreshTuningsAsync(), RefreshConnectionSettingsAsync()));

    /// <summary>Reads the current native values for the individual network tuning controls.</summary>
    private async Task RefreshTuningsAsync()
    {
        State = null;
        foreach (var choice in Choices) choice.Update(choice.State with { IsAvailable = false });
        ApplyStates(await _service.ReadNetworkTuningsAsync());
    }

    /// <summary>Confirms the precise before/after value and changes only the selected control.</summary>
    public async Task SetAsync(NetworkTuningChoice choice, string? value)
    {
        if (!CanWrite || !choice.IsAvailable || choice.State.Value == value) return;
        var expected = choice.State.Value;
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = choice.Title,
            Message = choice.Description + "\n\n" + string.Format(Text("OptimizeNetwork_ConfirmChange"), choice.FormatValue(expected), choice.FormatValue(value)),
            ConfirmButtonText = Text("Button_Continue"), CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await ExecuteAsync(async () =>
        {
            var completed = false;
            try
            {
                ApplyStates(await _service.SetNetworkTuningAsync(choice.Definition.Id, value, expected));
                completed = true;
                if (!IsError) Status = Text("OptimizeNetwork_SettingApplied");
            }
            finally
            {
                // Failed writes may be partial; require a fresh read before another edit.
                if (!completed) choice.Update(choice.State with { IsAvailable = false });
            }
        }, write: true);
    }

    private void ApplyStates(IReadOnlyList<NetworkTuningState> states)
    {
        foreach (var choice in Choices)
            choice.Update(states.Single(state => state.Id == choice.Definition.Id));
        State = _autoTuning.IsAvailable && _rss.IsAvailable && _autoTuning.State.Value != null
            ? new(_autoTuning.State.Value, _rss.IsOn) : null;
        var unavailable = Choices.Count(choice => !choice.IsAvailable);
        if (unavailable > 0)
        {
            IsError = true;
            Status = string.Format(Text("OptimizeNetwork_UnavailableCount"), unavailable);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnhance))]
    private async Task EnhanceAsync()
    {
        if (!CanEnhance) return;
        var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
        {
            Title = EnhanceLabel, Message = EnhanceDescription,
            ConfirmButtonText = Text("Button_Continue"), CancelButtonText = Text("Button_Cancel")
        });
        if (!response.Confirmed) return;
        await ExecuteAsync(async () =>
        {
            State = await _service.EnhanceNetworkAsync();
            ApplyStates(await _service.ReadNetworkTuningsAsync());
            if (!IsError) Status = Text("OptimizeNetwork_Applied");
        }, write: true);
    }

    protected override void NotifyActions()
    {
        OnPropertyChanged(nameof(CanEnhance));
        OnPropertyChanged(nameof(IsUpdatingNetwork));
        RefreshCommand.NotifyCanExecuteChanged(); EnhanceCommand.NotifyCanExecuteChanged(); RunSpeedTestCommand.NotifyCanExecuteChanged();
        ApplyDnsCommand.NotifyCanExecuteChanged(); ManageVpnCommand.NotifyCanExecuteChanged();
    }
    partial void OnStateChanged(NetworkPerformanceState? value) => EnhanceCommand.NotifyCanExecuteChanged();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            foreach (var choice in Choices) choice.RefreshText();
            foreach (var group in Groups) group.RefreshText();
            if (!string.IsNullOrWhiteSpace(SettingsSearchText)) ApplySettingsFilter();
        }
    }
}
