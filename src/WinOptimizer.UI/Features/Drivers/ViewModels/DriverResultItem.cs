using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Drivers.ViewModels;

/// <summary>A compact driver row with independent selection and an explicit details action.</summary>
public sealed partial class DriverResultItem(string name, string date, long? sizeBytes, Func<string> details,
    DriverRemovalTarget? target, ILocalizationService localization, IDialogService dialogs, bool removalPending = false,
    DriverUpdateMatch? candidate = null, Func<string>? subtitle = null, Uri? supportPage = null,
    Func<DriverResultItem, Task>? verify = null, Func<Uri, Task>? openSupport = null, DriverDevice? device = null) : ObservableObject, ISelectableResultItem
{
    private bool _isSelected;
    private bool _isDownloadSelected;
    private readonly string? _matchedDeviceName = SingleDeviceName(candidate);
    private readonly string _installedVersions = Versions(candidate?.Devices.Select(item => item.Version));
    private readonly string _candidateVersions = CandidateVersions(candidate);
    private readonly string _sources = string.Join(", ", candidate?.Update.Catalog?.References
        .Select(reference => DriverSourceNames.Get(reference.Source)).Distinct() ?? []);
    public string Name => removalPending ? $"{name} ({localization.GetString("Drivers_RemovalPending")})" : name;
    public DriverRemovalTarget? Target => target;
    public bool ShowSelection => Target is not null;
    public bool CanClean => Target is not null && !removalPending;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value && CanClean); }
    public DriverUpdateMatch? Candidate => candidate;
    public string DisplayName => _matchedDeviceName ?? Name;
    public string CandidateSubtitle => _matchedDeviceName is null ? candidate?.Update.Provider ?? "" : name;
    public string InstalledVersion => Reported(_installedVersions);
    public string CandidateVersion => Reported(_candidateVersions);
    public string SourceNames => _sources;
    public bool HasSources => _sources.Length > 0;
    public DriverVersionRelation? VersionRelation { get; } = CompareVersions(candidate);
    public bool IsNewer => VersionRelation == DriverVersionRelation.Newer;
    public string VersionStatus => localization.GetString(VersionRelation is { } relation ? "Drivers_Version_" + relation :
        candidate is not null ? "Drivers_Applicability_" + candidate.Applicability : "Drivers_Unknown");
    public string CertificationLabel => localization.GetString(candidate?.Update.Certification == DriverCertification.Whql
        ? "Drivers_WhqlVerified" : "Drivers_WhqlUnknown");
    public Icon RowIcon => device?.ClassName.ToUpperInvariant() switch
    {
        "MOUSE" or "HIDCLASS" => Icon.Cursor,
        "WPD" => Icon.Phone,
        "USB" or "USBDEVICE" => Icon.UsbPlug,
        "DISKDRIVE" or "VOLUME" or "CDROM" => Icon.HardDrive,
        "BLUETOOTH" => Icon.Bluetooth,
        _ => Icon.Desktop
    };
    public bool HasDeviceIcon => device is not null;
    public bool HasInventoryColumns => Target is not null || candidate is not null;
    public string Subtitle => subtitle?.Invoke() ?? "";
    public bool HasSubtitle => subtitle is not null;
    public bool CanDownload => candidate is { Applicability: not DriverApplicability.Incompatible, Update.Catalog: { } catalog } &&
        (catalog.Payloads.Length > 0 || catalog.References.Any(reference => reference.Source == DriverSources.WindowsUpdate) && !catalog.LicenseRequired);
    public bool IsDownloadSelected { get => _isDownloadSelected; set => SetProperty(ref _isDownloadSelected, value && CanDownload); }
    public bool CanVerify => verify is not null && CanDownload && candidate?.Update.Catalog?.Verification != DriverVerification.Verified;
    public bool HasSupport => supportPage is not null && openSupport is not null;
    public bool HasActions => HasSupport || CanVerify;
    public string VerifyLabel => localization.GetString("Drivers_Verify");
    public string SupportLabel => localization.GetString("Drivers_OpenSupport");
    public string Detail => Name;
    public int DetailMaxLines => 1;
    public ResultReport? Report => null;
    public string Date => DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        ? parsed.ToString("d", CultureInfo.CurrentCulture) : localization.GetString("Drivers_Unknown");
    public string Size => sizeBytes is not long bytes ? localization.GetString("Drivers_Unknown") : bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KB",
        _ => $"{bytes:N0} B"
    };
    public string Details => $"{localization.GetString("Drivers_Date")}: {Date}\n{localization.GetString("Drivers_Size")}: {Size}\n{details()}";

    /// <summary>Matches the displayed device name and its registered identifiers.</summary>
    public bool MatchesDevice(string query) => device is not null &&
        (device.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
         device.InstanceId.Contains(query, StringComparison.OrdinalIgnoreCase));

    private string Reported(string value) => value.Length > 0 ? value : localization.GetString("Drivers_Unknown");

    private static string Versions(IEnumerable<string>? values) => values is null ? "" :
        string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());

    /// <summary>Uses verified component versions when the offer covers multiple devices.</summary>
    private static string CandidateVersions(DriverUpdateMatch? match)
    {
        var components = Versions(match?.Comparisons.Select(item => item.Component.Version));
        return components.Length > 0 ? components : match?.Update.Version ?? "";
    }

    private static string? SingleDeviceName(DriverUpdateMatch? match)
    {
        var names = match?.Devices.Select(item => item.Name).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct().Take(2).ToArray();
        return names is [var single] ? single : null;
    }

    private static DriverVersionRelation? CompareVersions(DriverUpdateMatch? match)
    {
        var relations = match?.Comparisons.Select(item => item.VersionRelation).Distinct().Take(2).ToArray();
        return relations is [var single] ? single : null;
    }

    /// <summary>Opens the complete metadata without changing checkbox selection.</summary>
    [RelayCommand]
    private Task ShowDetailsAsync() => dialogs.ShowInformationAsync(Details, Name);

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private Task VerifyAsync() => verify!(this);

    [RelayCommand(CanExecute = nameof(HasSupport))]
    private Task OpenSupportAsync() => openSupport!(supportPage!);

    public void RefreshText() => OnPropertyChanged(string.Empty);
}
