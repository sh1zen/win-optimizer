using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Utilities.ViewModels;

public sealed record ChargeLimitChoice(int Percentage)
{
    public string Label => Percentage + "%";
}

/// <summary>A restart setting shown as a fixed level, relative offset, soonest supported level, or standard charging.</summary>
public sealed record ChargeResumeChoice(int? Percentage, int Offset, string Label)
{
    public bool IsSoonest { get; init; }
    public bool Matches(ChargeResumeChoice other) => IsSoonest == other.IsSoonest && Offset == other.Offset &&
        (IsSoonest || Offset > 0 || Percentage == other.Percentage);
}

/// <summary>A localized, selectable sensor label/value row.</summary>
public sealed record BatteryReading(string Label, string Value);
/// <summary>Groups one battery's readings for the responsive details and consumption panels.</summary>
public sealed record BatteryTelemetryCard(string Name, string DetailsTitle, string ConsumptionTitle,
    IReadOnlyList<BatteryReading> Details, IReadOnlyList<BatteryReading> Consumption);

/// <summary>Shows detected firmware support and starts at an 80% target without applying it automatically.</summary>
public partial class BatteryChargeViewModel : UtilityViewModel
{
    private static readonly int[] FixedResumePercentages = [60, 70, 80];
    private static readonly int[] ResumeOffsets = [10, 20];
    private readonly IBatteryChargeService _service;
    private readonly ILocalizationService _localization;
    private BatteryChargeState? _state;
    private bool _publishingLimits;
    protected override string TitleKey => "Utilities_Battery";
    public ObservableCollection<ChargeLimitChoice> Limits { get; } = [];
    public ObservableCollection<ChargeResumeChoice> ResumeChoices { get; } = [];
    [ObservableProperty] public partial int SelectedIndex { get; set; } = -1;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial int SelectedResumeIndex { get; set; } = -1;
    public bool HasState => _state != null;
    public string Device => _state == null ? "" : $"{_state.Manufacturer} {_state.Model}".Trim();
    public string Charge => _state?.ChargePercent is { } value ? value + "%" : Text("Utilities_Unknown");
    public string ActiveLimit => _state?.ActiveLimit is { } value ? value + "%" : Text("Utilities_Unknown");
    public bool HasActiveResumeThreshold => _state?.ActiveResumeThreshold.HasValue == true;
    public string ActiveResumeThreshold => _state?.ActiveResumeThreshold is { } value
        ? string.Format(Culture, Text("Utilities_BatteryResumeThreshold"), value) : "";
    public string Provider => _state?.Provider ?? "";
    public bool HasProvider => !string.IsNullOrWhiteSpace(Provider);
    public Uri HelpUri => _state?.HelpUri ?? new Uri("https://support.microsoft.com/windows");
    public string SupportStatus => Text(_state is { HasBattery: false } ? "Utilities_NoBattery" :
        _state is { CanApply: true } ? "Utilities_BatterySupported" : "Utilities_BatteryUnsupported");
    public string Diagnostic => _state?.Diagnostic ?? "";
    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    public string ChargeLabel => Text("Utilities_CurrentCharge");
    public string LimitLabel => Text("Utilities_ChargeLimit");
    public string ActiveLabel => Text("Utilities_ActiveLimit");
    public bool IsChargingAboveLimit => _state?.IsChargingAboveLimit == true;
    public string ChargingAboveLimitMessage => IsChargingAboveLimit
        ? string.Format(Culture, Text("Utilities_BatteryChargingAboveLimit"), _state?.ChargePercent, _state?.ActiveLimit) : "";
    public string ApplyLabel => Text("Utilities_ApplyLimit");
    public string HelpLabel => Text("Utilities_OemHelp");
    public string Hint => Text("Utilities_BatteryHint");
    public string Coverage => Text("Utilities_BatteryCoverage");
    public bool ShowTelemetry => _state?.HasBattery == true;
    public string TelemetryHint => Text("Utilities_BatteryTelemetryHint");
    public string TelemetryUnavailable => _state?.Telemetry?.Batteries.Count is > 0 ? "" : Text("Utilities_BatterySensorsUnavailable");
    public bool HasTelemetryUnavailable => _state?.Telemetry?.Batteries.Count is not > 0;
    public string TelemetryDiagnostics => string.Join(Environment.NewLine, _state?.Telemetry?.Diagnostics ?? []);
    public bool HasTelemetryDiagnostics => _state?.Telemetry?.Diagnostics.Count is > 0;
    public bool HasCapturedAt => _state?.Telemetry != null;
    public string CapturedAt => _state?.Telemetry is { } snapshot
        ? string.Format(Culture, Text("Utilities_BatteryCapturedAt"), snapshot.CapturedAt.ToLocalTime().ToString("G", Culture)) : "";
    private CultureInfo Culture => CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(_localization.CurrentLanguage)
        ? CultureInfo.CurrentCulture.Name : _localization.CurrentLanguage);
    public IReadOnlyList<BatteryTelemetryCard> Batteries => _state?.Telemetry?.Batteries.Select(CreateBatteryCard).ToArray() ?? [];
    public bool CanSelectLimit => IsIdle && _state?.CanApply == true;
    private int? SelectedLimit => SelectedIndex >= 0 && SelectedIndex < Limits.Count ? Limits[SelectedIndex].Percentage : null;
    private ChargeResumeChoice? SelectedResumeChoice => SelectedResumeIndex >= 0 && SelectedResumeIndex < ResumeChoices.Count
        ? ResumeChoices[SelectedResumeIndex] : null;
    public bool ShowResumeChoices => ResumeChoices.Count > 0;
    public bool ShowResumeSetting => _state?.CanApply == true;
    public bool CanSelectResume => ShowResumeChoices && CanWrite;
    public string ResumeLabel => Text("Utilities_BatteryResumeCharging");
    public string ResumeDescription => ShowResumeChoices ? "" : Text("Utilities_BatteryResumeFirmware");
    public string ResumePlaceholder => ShowResumeChoices ? "" : Text("Utilities_BatteryResumeAutomatic");
    public bool ShowSupportHelper => _state is { HasBattery: true, CanApply: false };
    public bool ShowSetupAction => ShowSupportHelper && _state?.Setup != null &&
        _state.Issue is BatteryControlIssue.InterfaceUnavailable or BatteryControlIssue.NoProvider or BatteryControlIssue.FirmwareUnsupported;
    public bool ShowOptionalDrivers => ShowSetupAction && _state?.Setup?.Kind == BatterySupportSetupKind.DriverAndUtility &&
        _state.Issue is BatteryControlIssue.InterfaceUnavailable or BatteryControlIssue.NoProvider;
    public bool CanOpenSetup => ShowSetupAction && CanWrite;
    public string SupportHelperTitle => Text("Utilities_BatterySetupTitle");
    public string SupportHelperMessage => Text("Utilities_BatteryIssue_" + (_state?.Issue ?? BatteryControlIssue.None));
    public string SetupInstructions => !ShowSetupAction || _state?.Setup is not { } setup ? "" :
        string.Format(Text(setup.Kind == BatterySupportSetupKind.Firmware ? "Utilities_BatteryFirmwareSetup" :
            _state.Issue == BatteryControlIssue.FirmwareUnsupported ? "Utilities_BatteryVerifyModelInstructions" :
            "Utilities_BatteryInstallInstructions"), setup.PackageName, Device);
    public string SetupActionLabel => Text(_state?.Setup?.Kind == BatterySupportSetupKind.Firmware ||
        _state?.Issue == BatteryControlIssue.FirmwareUnsupported ? "Utilities_BatteryCheckModel" : "Utilities_BatteryInstallSupport");
    public string OptionalDriversLabel => Text("Utilities_BatteryOptionalDrivers");
    public string RecheckSupportLabel => Text("Utilities_BatteryRecheck");
    public Uri? SetupUri => _state?.Setup?.InstallUri;
    public Uri OptionalDriversUri => new("ms-settings:windowsupdate-optionalupdates");

    public BatteryChargeViewModel(IBatteryChargeService service, ILocalizationService localization, IApplicationModeService mode,
        IDispatcherService dispatcher, ITaskProgressService tasks) : base(localization, mode, dispatcher, tasks)
    { _service = service; _localization = localization; }

    /// <summary>Formats typed sensor readings once per rendered snapshot, with independent rows for each battery.</summary>
    private BatteryTelemetryCard CreateBatteryCard(BatteryTelemetry battery)
    {
        string Value(double? value, string unit, string format = "N2") => value is { } number
            ? number.ToString(format, Culture) + " " + unit : Text("Utilities_Unknown");
        BatteryReading Reading(string key, string value) => new(Text("Utilities_Battery" + key), value);
        string Flag(bool? value, string yes, string no) => value.HasValue ? Text("Utilities_Battery" + (value.Value ? yes : no)) : Text("Utilities_Unknown");
        var status = battery.Charging == true && battery.Discharging != true ? Text("Utilities_BatteryCharging") :
            battery.Discharging == true && battery.Charging != true ? Text("Utilities_BatteryDischarging") :
            battery.Charging == false && battery.Discharging == false ? Text("Utilities_BatteryIdle") : Text("Utilities_Unknown");
        var runtime = battery.EstimatedRuntime is { } time
            ? string.Format(Culture, Text("Utilities_BatteryDuration"), (int)time.TotalHours, time.Minutes) : Text("Utilities_Unknown");
        return new(string.IsNullOrWhiteSpace(battery.Name) ? Text("Utilities_BatteryDetails") : battery.Name,
            Text("Utilities_BatteryDetails"), Text("Utilities_BatteryConsumption"),
            [
                Reading("Manufacturer", string.IsNullOrWhiteSpace(battery.Manufacturer) ? Text("Utilities_Unknown") : battery.Manufacturer),
                Reading("DesignCapacity", Value(battery.DesignCapacityWh, "Wh")),
                Reading("FullCapacity", Value(battery.FullChargeCapacityWh, "Wh")),
                Reading("RemainingEnergy", Value(battery.RemainingEnergyWh, "Wh")),
                Reading("Health", Value(battery.HealthPercent, "%", "N1")),
                Reading("Wear", Value(battery.WearPercent, "%", "N1")),
                Reading("Cycles", battery.CycleCount?.ToString("N0", Culture) ?? Text("Utilities_Unknown")),
                Reading("Temperature", Value(battery.TemperatureCelsius, "°C", "N1"))
            ],
            [
                Reading("PowerSource", Flag(battery.PowerOnline, "AC", "DC")),
                Reading("Status", status),
                Reading("ChargePower", Value(battery.ChargePowerWatts, "W")),
                Reading("DischargePower", Value(battery.DischargePowerWatts, "W")),
                Reading("Voltage", Value(battery.Voltage, "V", "N3")),
                Reading("Current", Value(battery.CurrentAmperes, "A", "N3")),
                Reading("Runtime", runtime),
                Reading("Critical", Flag(battery.Critical, "Yes", "No"))
            ]);
    }

    public Task LoadAsync() => RefreshAsync();
    partial void OnSelectedIndexChanged(int value)
    {
        if (!_publishingLimits)
            UpdateResumeChoices(SelectedResumeChoice);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RefreshAsync() => ExecuteAsync(async () => Publish(await _service.ReadAsync()));

    private bool CanApply() => CanWrite && _state?.CanApply == true && SelectedLimit.HasValue &&
        (!ShowResumeChoices || SelectedResumeChoice != null);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync()
    {
        if (!CanApply() || SelectedLimit is not { } percentage) return Task.CompletedTask;
        var restart = SelectedResumeChoice?.Percentage;
        return ExecuteAsync(async () =>
        {
            var state = restart is { } threshold
                ? await _service.SetLimitAsync(percentage, threshold)
                : await _service.SetLimitAsync(percentage);
            Publish(state);
            if (!state.IsChargingAboveLimit)
                Status = string.Format(Text("Utilities_LimitApplied"), percentage);
        }, write: true);
    }

    /// <summary>Recomputes limit-dependent choices while keeping a supported draft selection.</summary>
    private void UpdateResumeChoices(ChargeResumeChoice? previous)
    {
        ResumeChoices.Clear();
        if (SelectedLimit is { } limit && _state is { } state && state.ResumeThresholds.TryGetValue(limit, out var range))
        {
            if (range.SupportsStandardCharging)
                ResumeChoices.Add(new(null, 0, $"{limit.ToString(Culture)}% ({Text("Utilities_BatteryStandardCharging")})"));
            foreach (var percentage in FixedResumePercentages.Where(range.Contains))
                AddFixedResumeChoice(percentage);
            foreach (var offset in ResumeOffsets)
            {
                var percentage = limit - offset;
                if (range.Contains(percentage))
                    ResumeChoices.Add(new(percentage, offset, string.Format(Culture, Text("Utilities_BatteryResumeOffset"), offset, percentage)));
            }
            var soonest = new ChargeResumeChoice(range.Maximum, 0,
                string.Format(Culture, Text("Utilities_BatteryResumeSoonest"), range.Maximum)) { IsSoonest = true };
            ResumeChoices.Add(soonest);

            var active = state.ActiveLimit == limit ? state.ActiveResumeThreshold : null;
            if (active is { } current && range.Contains(current) && ResumeChoices.All(choice => choice.Percentage != current))
                AddFixedResumeChoice(current);
            var choice = ResumeChoices.FirstOrDefault(option => previous != null && option.Matches(previous))
                ?? ResumeChoices.FirstOrDefault(option => active.HasValue && option.Percentage == active)
                ?? ResumeChoices.FirstOrDefault(option => !option.Percentage.HasValue)
                ?? ResumeChoices.FirstOrDefault(option => option.Offset == ResumeOffsets[0])
                ?? soonest;
            SelectedResumeIndex = ResumeChoices.IndexOf(choice);
        }
        else
        {
            SelectedResumeIndex = -1;
        }
        OnPropertyChanged(nameof(ShowResumeChoices));
        OnPropertyChanged(nameof(CanSelectResume));
        OnPropertyChanged(nameof(ResumeDescription));
        OnPropertyChanged(nameof(ResumePlaceholder));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void AddFixedResumeChoice(int percentage) => ResumeChoices.Add(new(percentage, 0,
        string.Format(Culture, Text("Utilities_BatteryResumeBelow"), percentage)));

    private void Publish(BatteryChargeState state)
    {
        var selectedLimit = SelectedLimit;
        var target = selectedLimit ?? 80;
        var previous = SelectedResumeChoice;
        _state = state;
        _publishingLimits = true;
        Limits.Clear();
        foreach (var limit in state.SupportedLimits) Limits.Add(new(limit));
        SelectedIndex = Limits.Select((value, index) => (value, index)).Where(p => p.value.Percentage == target)
            .Select(p => p.index).DefaultIfEmpty(Limits.Count > 0 ? 0 : -1).First();
        _publishingLimits = false;
        UpdateResumeChoices(SelectedLimit == selectedLimit ? previous : null);
        OnPropertyChanged(string.Empty);
    }

    protected override void NotifyActions()
    {
        RefreshCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSelectLimit)); OnPropertyChanged(nameof(CanOpenSetup));
        OnPropertyChanged(nameof(CanSelectResume));
    }

    protected override void RefreshLocalizedText()
    {
        UpdateResumeChoices(SelectedResumeChoice);
        base.RefreshLocalizedText();
    }
}
