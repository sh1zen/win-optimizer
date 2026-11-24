using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.UI.Features.Optimize.ViewModels;

/// <summary>Shows a Windows VPN profile with localized scope, status and tunneling details.</summary>
public sealed record ConnectionVpnCard(string Name, string Details);

public partial class NetworkOptimizationViewModel
{
    private const string DnsSettingId = "gaming-dns-server";
    private readonly IConnectionService _connection;
    private readonly ICompatibleSettingsRegistry _settings;
    private readonly ISettingApplicationService _application;
    private ConnectionSnapshot? _connectionSnapshot;
    private IReadOnlyList<ComboBoxOption>? _dnsOptions;

    [ObservableProperty] public partial int SelectedDnsIndex { get; set; } = -1;

    public string DnsTitle => Text("Connection_Dns");
    public string DnsHint => Text("Connection_DnsHint");
    public string DnsApplyLabel => Text("Connection_ApplyDns");
    public IReadOnlyList<string> DnsChoices => _dnsOptions?.Select(option => Text(option.DisplayName)).ToArray() ?? [];
    public bool ShowDnsUnavailable => _dnsOptions is { Count: 0 };
    public string DnsUnavailable => ShowDnsUnavailable ? Text("Connection_DnsUnavailable") : "";

    public string VpnTitle => Text("Connection_Vpn");
    public string VpnHint => Text("Connection_VpnHint");
    public string ManageVpnLabel => Text("Connection_ManageVpn");
    public bool ShowEmptyVpn => _connectionSnapshot is { VpnProfiles.Count: 0, VpnDiagnostic.Length: 0 };
    public string EmptyVpn => ShowEmptyVpn ? Text("Connection_NoVpn") : "";
    public string VpnDiagnostic => _connectionSnapshot?.VpnDiagnostic ?? "";
    public bool HasVpnDiagnostic => VpnDiagnostic.Length > 0;
    public bool HasVpnSnapshot => _connectionSnapshot != null;
    public string VpnCapturedAt
    {
        get
        {
            if (_connectionSnapshot == null) return "";
            var culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(_localization.CurrentLanguage)
                ? CultureInfo.CurrentCulture.Name : _localization.CurrentLanguage);
            return string.Format(culture, Text("Connection_CapturedAt"), _connectionSnapshot.CapturedAt.ToLocalTime().ToString("G", culture));
        }
    }
    public IReadOnlyList<ConnectionVpnCard> VpnProfiles => _connectionSnapshot?.VpnProfiles.Select(profile => new ConnectionVpnCard(
        profile.Name, $"{profile.Server} · {VpnStatus(profile.Status)} · {Text(profile.AllUsers ? "Connection_AllUsers" : "Connection_CurrentUser")} · " +
        Text(profile.SplitTunneling ? "Connection_SplitTunnel" : "Connection_FullTunnel"))).ToArray() ?? [];

    partial void OnSelectedDnsIndexChanged(int value) => ApplyDnsCommand.NotifyCanExecuteChanged();

    /// <summary>Publishes compatible DNS options and the current Windows VPN inventory.</summary>
    private async Task RefreshConnectionSettingsAsync()
    {
        if (!_settings.IsInitialized) await _settings.InitializeAsync();
        var choices = _settings.GetById(DnsSettingId)?.ComboBox?.Options ?? [];
        if (!ReferenceEquals(choices, _dnsOptions))
        {
            _dnsOptions = choices;
            OnPropertyChanged(nameof(DnsChoices));
            OnPropertyChanged(nameof(DnsUnavailable));
            OnPropertyChanged(nameof(ShowDnsUnavailable));
        }
        PublishConnectionSnapshot(await _connection.ReadAsync());
    }

    private bool CanApplyDns() => CanWrite && _dnsOptions != null && SelectedDnsIndex >= 0 && SelectedDnsIndex < _dnsOptions.Count;

    /// <summary>Applies the selected option through the existing DNS setting workflow.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyDns))]
    private Task ApplyDnsAsync() => ExecuteAsync(async () =>
    {
        var result = await _application.ApplySettingAsync(new ApplySettingRequest
            { SettingId = DnsSettingId, Enable = true, Value = SelectedDnsIndex });
        if (!result.Success) throw new UtilityOperationException(result.ErrorMessage ?? Text("Connection_DnsFailed"));
        PublishConnectionSnapshot(await _connection.ReadAsync());
        Status = Text("Connection_DnsApplied");
    }, write: true);

    /// <summary>Opens the interactive user's Windows VPN settings.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private Task ManageVpnAsync() => ExecuteAsync(_connection.OpenVpnSettingsAsync, write: true);

    private void PublishConnectionSnapshot(ConnectionSnapshot snapshot)
    {
        _connectionSnapshot = snapshot;
        OnPropertyChanged(nameof(HasVpnSnapshot));
        OnPropertyChanged(nameof(VpnCapturedAt));
        OnPropertyChanged(nameof(VpnProfiles));
        OnPropertyChanged(nameof(VpnDiagnostic));
        OnPropertyChanged(nameof(HasVpnDiagnostic));
        OnPropertyChanged(nameof(EmptyVpn));
        OnPropertyChanged(nameof(ShowEmptyVpn));
    }

    private string VpnStatus(string status) => status switch
    {
        "Connected" => Text("Connection_Connected"), "Disconnected" => Text("Connection_Disconnected"),
        _ => status
    };
}
