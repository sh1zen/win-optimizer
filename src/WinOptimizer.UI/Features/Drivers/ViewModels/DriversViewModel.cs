using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Core.Features.Drivers.Services;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Drivers.ViewModels;

/// <summary>Coordinates scans, details and confirmed removal of selected review candidates.</summary>
public partial class DriversViewModel : ObservableObject, IDisposable
{
    private readonly IDriverScanService _scanner;
    private readonly ILocalizationService _localization;
    private readonly ITaskProgressService _tasks;
    private readonly IDispatcherService _dispatcher;
    private readonly IDriverRemovalService _removal;
    private readonly IDialogService _dialogs;
    private readonly IApplicationModeService _mode;
    private readonly IDriverPackageService? _packages;
    private CancellationTokenSource? _operation;
    private DriverScanReport? _report;
    private readonly HashSet<DriverRemovalTarget> _pendingRemovals = [];
    private int _selectedCount;
    private int _downloadCount;
    private readonly HashSet<DriverResultItem> _visibleSelectableRows = [];
    private int _visibleSelectedCount;
    private bool _disposed;
    public DriverSettingsViewModel Settings { get; }
    public ObservableCollection<SelectableResultGroup> ResultGroups { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowEmptyState), nameof(ShowScanInformation), nameof(HasStatus))]
    public partial bool IsBusy { get; private set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] public partial string Status { get; private set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotices), nameof(ShowStandaloneNotices), nameof(CoverageDetails))]
    public partial string Notices { get; private set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScanning), nameof(CanCancelScan), nameof(ScanTitle), nameof(HasScanDescription),
        nameof(DetectMilestone), nameof(SourcesMilestone), nameof(ResultsMilestone), nameof(ScanActivity))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand), nameof(ShowScanDetailsCommand))]
    public partial DriverScanPhase ScanPhase { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelScan), nameof(ScanFooterHint))]
    [NotifyCanExecuteChangedFor(nameof(CancelScanCommand))]
    public partial bool IsScanCancelling { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceSearch), nameof(IsCandidateCategory), nameof(CategorySummary), nameof(SelectAllLabel))]
    public partial SelectableResultGroup? SelectedGroup { get; set; }
    [ObservableProperty] public partial string DeviceQuery { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyCategory), nameof(CategoryEmptyTitle), nameof(CategoryEmptyDescription), nameof(DeviceResultCount))]
    public partial IReadOnlyList<DriverResultItem> VisibleRows { get; private set; } = [];
    public bool IsIdle => !IsBusy;
    public bool HasReport => _report is not null;
    public bool ShowEmptyState => !HasReport && IsIdle;
    public bool HasStatus => IsIdle && !string.IsNullOrWhiteSpace(Status);
    public bool HasNotices => !string.IsNullOrWhiteSpace(Notices);
    public bool ShowStandaloneNotices => HasNotices && !HasLimitedCoverage;
    /// <summary>Shows scan guidance only while waiting to start a scan without results.</summary>
    public bool ShowScanInformation => ShowEmptyState;
    public bool HasEmptyCategory => HasReport && SelectedGroup is not null && VisibleRows.Count == 0;
    private bool UpdatesUnavailable => _report is { UpdatesChecked: false } && SelectedGroup?.LabelKey == "Drivers_Updates";
    public string CategoryEmptyTitle => Text(UpdatesUnavailable ? "Drivers_UpdatesUnavailableTitle" : "Drivers_CategoryEmpty");
    public string CategoryEmptyDescription => UpdatesUnavailable ? Text("Drivers_UpdatesUnavailable") :
        HasDeviceSearch && !string.IsNullOrWhiteSpace(DeviceQuery) ? Text("Drivers_NoDevicesMatch") :
        _report is { ExcludedUpdates: > 0 } && SelectedGroup?.LabelKey == "Drivers_Updates"
            ? Text("Drivers_FilterWhql") : Text("Drivers_CategoryEmptyDescription");
    public bool HasRescanHint => _report is not null && _report.Options != Settings.Options;
    public string Heading => Text("Nav_Drivers");
    public string IntroDescription => Text(HasReport ? "Cleaner_Results" : "Drivers_IntroDescription");
    public string EmptyTitle => Text("Scan_ReadyTitle");
    public string EmptyDescription => Text("Drivers_ReadyDescription");
    public string Description => Text("Drivers_Description");
    public string ScanLabel => Text("Cleaner_Scan");
    public string ScanAgainLabel => Text("UninstallResidue_Rescan");
    public string CancelLabel => Text("Button_Cancel");
    public bool IsScanning => ScanPhase != DriverScanPhase.None;
    public bool CanCancelScan => IsScanning && !IsScanCancelling;
    public string ScanInProgressLabel => Text("Drivers_ScanInProgress");
    public string ScanTitle => Text(ScanPhase switch
    {
        DriverScanPhase.CheckSources => "Drivers_CheckingSourcesTitle",
        DriverScanPhase.PrepareResults => "Drivers_PreparingResults",
        _ => "Drivers_DetectingDevices"
    });
    public bool HasScanDescription => ScanPhase == DriverScanPhase.CheckSources;
    public string ScanDescription => Text("Drivers_CheckingSourcesDescription");
    public string ScanActivityLabel => Text("Drivers_RecentActivity");
    public string ScanDetailsLabel => Text("Drivers_ViewDetails");
    public string CancelScanLabel => Text("Drivers_CancelScan");
    public string ScanFooterHint => Text(IsScanCancelling ? "Progress_Cancelling" : "Drivers_ScanResultsHint");
    public DriverScanMilestone DetectMilestone => Milestone(DriverScanPhase.DetectDevices, "Drivers_DetectDevicesStep");
    public DriverScanMilestone SourcesMilestone => Milestone(DriverScanPhase.CheckSources, "Drivers_CheckSourcesStep");
    public DriverScanMilestone ResultsMilestone => Milestone(DriverScanPhase.PrepareResults, "Drivers_PrepareResultsStep");
    public IReadOnlyList<DriverScanActivity> ScanActivity => ScanPhase switch
    {
        DriverScanPhase.None => [],
        DriverScanPhase.DetectDevices => [new(Text("Drivers_Inventory"), Icon.Desktop, true)],
        DriverScanPhase.CheckSources => [
            new(Text("Drivers_DevicesDetected"), Icon.Desktop),
            new(Text("Drivers_DriverVersionsLoaded"), Icon.Layer),
            new(Text("Drivers_SearchingUpdates"), Icon.Cloud, true)],
        _ => [
            new(Text("Drivers_DevicesDetected"), Icon.Desktop),
            new(Text("Drivers_DriverVersionsLoaded"), Icon.Layer),
            new(Text("Drivers_SourcesChecked"), Icon.Cloud),
            new(Text("Drivers_PreparingResults"), Icon.Document, true)]
    };
    public string InformationLabel => Text("Dialog_Information");
    public bool HasLimitedCoverage => _report is { } report && (!report.UpdatesChecked || !report.Inventory.BindingsComplete ||
        report.Notices.Count > 0 || report.Sources.Any(source => source.State is DriverSourceState.Partial or DriverSourceState.Unavailable
            or DriverSourceState.Stale or DriverSourceState.VerificationFailed));
    public string CoverageTitle => Text("Drivers_LimitedCoverage");
    public string CoverageSummary => _report?.Notices.Where(notice => !string.IsNullOrWhiteSpace(notice)).Distinct().Take(2).ToArray() is { Length: > 0 } notices
        ? string.Join(" · ", notices) : Text(_report is { UpdatesChecked: false } ? "Drivers_UpdatesUnavailable" : "Drivers_CoverageSummary");
    public string CoverageDetails
    {
        get
        {
            if (_report is not { } report) return Notices;
            var details = string.Join(Environment.NewLine,
                report.Sources.Select(source => $"{DriverSourceNames.Get(source.Source)} · {Text("Drivers_Source_" + source.State)}")
                    .Concat(HasNotices ? [Notices] : []));
            return details.Length > 0 ? details : CoverageSummary;
        }
    }
    public string CoverageDetailsLabel => Text("Drivers_ViewScanDetails");
    public string FilterSummary => Text(Settings.Options.OnlyWhqlUpdates ? "Drivers_FilterWhql" : "Drivers_FilterAll");
    public string ReportFilterLabel => Text(_report is { Options.OnlyWhqlUpdates: true } ? "Drivers_WhqlOnly" : "Drivers_AllCertification");
    public string FilterSettingsLabel => Text("Drivers_FilterSettings");
    public string Summary => _report is null ? "" : string.Format(Text("Drivers_Summary"),
        _report.UnreferencedPackages.Count, _report.DisconnectedDevices.Count, _report.Updates.Count);
    public string RescanHint => HasRescanHint ? Text("Drivers_Rescan") : "";
    public string NameLabel => Text("Drivers_Name");
    public string DriverLabel => Text("Drivers_ColumnDriver");
    public string DeviceLabel => Text("Drivers_ColumnDevice");
    public string InstalledLabel => Text("Status_Installed");
    public string CandidateLabel => Text("Drivers_ColumnCandidate");
    public string DriverDateLabel => Text("SystemInfo_Field_DriverDate");
    public string ReleasedLabel => Text("Drivers_ColumnReleased");
    public string CandidateStatusLabel => Text("Security_State");
    public string DeviceSearchLabel => Text("Drivers_SearchDevices");
    public string DeviceResultCount => string.Format(Text("Drivers_DeviceResults"), VisibleRows.Count, SelectedGroup?.Items.Count ?? 0);
    public bool HasDeviceSearch => SelectedGroup?.LabelKey == "Drivers_Disconnected";
    public bool IsCandidateCategory => SelectedGroup?.LabelKey is "Drivers_Updates" or "Drivers_Filtered";
    public string CategorySummary
    {
        get
        {
            var count = SelectedGroup?.Items.Count ?? 0;
            if (HasDeviceSearch) return string.Format(Text("Drivers_DisconnectedSummary"), count);
            if (IsCandidateCategory && count > 0)
            {
                var newer = VisibleRows.Count(row => row.IsNewer);
                var current = VisibleRows.Count(row => row.VersionRelation == DriverVersionRelation.Current);
                if (newer + current == count) return string.Format(Text("Drivers_CandidateSummary"), newer, current);
            }
            return string.Format(Text("Drivers_ResultCount"), count);
        }
    }
    public string DateLabel => Text("Drivers_Date");
    public string SizeLabel => Text("Drivers_Size");
    public string PackageSizeLabel => Text("Drivers_PackageSize");
    public string ResultsLabel => Text("Drivers_Results");
    public string UnusedPackagesLabel => Text("Drivers_Unreferenced");
    public string DisconnectedDevicesLabel => Text("Drivers_Disconnected");
    public string OfficialUpdatesLabel => Text("Drivers_Updates");
    public string SelectionHint => Text(ShowDownloadAction && !ShowRemovalAction ? "Drivers_SelectDownloads" :
        ShowRemovalAction && !ShowDownloadAction ? "Drivers_SelectRemovals" : "Drivers_SelectionHint");
    public string SelectAllLabel => Text(HasDeviceSearch ? "Cleaner_SelectAll" : "Drivers_SelectCategory");
    public string UninstallLabel => Text("Drivers_Uninstall");
    public string DownloadLabel => Text("Drivers_DownloadSelected");
    public bool ShowDownloadAction => HasReport && (_downloadCount > 0 || IsCandidateCategory);
    public bool ShowRemovalAction => HasReport && (SelectedCount > 0 || SelectedGroup?.LabelKey is "Drivers_Unreferenced" or "Drivers_Disconnected");
    public bool ShowSelectionFooter => ShowRemovalAction || ShowDownloadAction;
    public bool HasVisibleSelection => _visibleSelectableRows.Count > 0;
    public bool? VisibleSelection
    {
        get => _visibleSelectedCount == 0 ? false : _visibleSelectedCount == _visibleSelectableRows.Count ? true : null;
        set
        {
            if (!IsIdle || value is not bool selected) return;
            foreach (var row in _visibleSelectableRows)
            {
                if (row.CanClean) row.IsSelected = selected;
                else row.IsDownloadSelected = selected;
            }
        }
    }
    public bool CanDownload => !_disposed && !IsBusy && _packages is not null && _downloadCount > 0;
    public string DownloadSelectionSummary => string.Format(Text(ShowRemovalAction ? "Drivers_DownloadCount" : "Drivers_Selected"), _downloadCount);
    public int SelectedCount => _selectedCount;
    public string SelectionSummary => string.Format(Text("Drivers_Selected"), SelectedCount);
    public bool CanUninstall => !_disposed && !IsBusy && SelectedCount > 0 && _mode.CurrentMode != WinOptimizerMode.Builder;
    public bool HasTestModeHint => _mode.CurrentMode == WinOptimizerMode.Builder;
    public string TestModeHint => HasTestModeHint ? Text("Drivers_TestMode") : "";

    public DriversViewModel(IDriverScanService scanner, DriverSettingsViewModel settings, ILocalizationService localization,
        ITaskProgressService tasks, IDispatcherService dispatcher, IDriverRemovalService removal,
        IDialogService dialogs, IApplicationModeService mode, IDriverPackageService? packages = null)
    {
        _scanner = scanner; Settings = settings; _localization = localization; _tasks = tasks; _dispatcher = dispatcher;
        _removal = removal; _dialogs = dialogs; _mode = mode; _packages = packages;
        _localization.LanguageChanged += OnLanguageChanged;
        Settings.PropertyChanged += OnSettingsChanged;
        _mode.ModeChanged += OnModeChanged;
    }

    public async Task InitializeAsync()
    {
        if (!await Settings.LoadAsync() && !_disposed) Status = Settings.Status;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_disposed || IsBusy) return;
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        IsBusy = true;
        CancellationTokenSource? owner = null;
        try
        {
            if (!await Settings.LoadAsync()) { Status = Settings.Status; return; }
            if (!await Settings.FlushAsync()) { Status = Settings.Status; return; }
            if (_disposed) return;
            if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
            ClearResults();
            DeviceQuery = "";
            _report = null; Notices = ""; NotifyReport();
            owner = _tasks.StartTask(Heading, isIndeterminate: true);
            _operation = owner;
            Status = Text("Drivers_Inventory");
            ScanPhase = DriverScanPhase.DetectDevices;
            var scanOwner = owner;
            using var cancellationRegistration = scanOwner.Token.Register(() => _dispatcher.RunOnUIThread(() =>
            {
                if (!_disposed && ReferenceEquals(_operation, scanOwner)) IsScanCancelling = true;
            }));
            var options = Settings.Options;
            var report = await _scanner.ScanAsync(options, CreateProgress(owner), owner.Token);
            owner.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            _report = report;
            PublishRows(report);
            Notices = string.Join(Environment.NewLine, report.Notices);
            Status = "";
            NotifyReport();
        }
        catch (OperationCanceledException) { if (!_disposed) Status = Text("Cleaner_Cancelled"); }
        catch (Exception ex) when (ex is DriverScanException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ReportFailure(ex.Message, owner); }
        finally
        {
            if (owner is not null)
            {
                _operation = null;
                if (ReferenceEquals(_tasks.CurrentTaskCancellationSource, owner)) _tasks.CompleteTask();
            }
            ScanPhase = DriverScanPhase.None;
            IsScanCancelling = false;
            IsBusy = false;
        }
    }

    /// <summary>Routes the page action to the cancellation source shared with the task footer.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan() => _operation?.Cancel();

    /// <summary>Opens the live scan output through the shared task details dialog.</summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private Task ShowScanDetailsAsync() => _dialogs.ShowTaskOutputDialogAsync(
        Text("Dialog_TerminalOutput_Title"), _tasks.GetTerminalOutputLines());

    /// <summary>Projects the active phase into the step indicator.</summary>
    private DriverScanMilestone Milestone(DriverScanPhase phase, string label) =>
        new(Text(label), ScanPhase > phase ? Icon.CheckmarkCircle : Icon.Circle, ScanPhase == phase);

    private string UpdateDetail(DriverUpdateMatch match)
    {
        var update = match.Update;
        var installed = string.Join(", ", match.Devices.Select(device => device.Version).Where(version => version.Length > 0).Distinct());
        var overview = string.Format(Text("Drivers_UpdateDetail"), update.Title, update.Provider,
            installed.Length > 0 ? installed : Text("Drivers_Unknown"),
            update.Version.Length > 0 ? update.Version : Text("Drivers_Unknown"), update.Date,
            Text(update.Certification == DriverCertification.Whql ? "Drivers_WhqlVerified" : "Drivers_WhqlUnknown"));
        if (update.Catalog is not { } catalog)
            return $"{overview}\n{Text("Drivers_HardwareIds")}: {update.HardwareId}\nID: {update.Id}";
        var sourceLines = catalog.References.SelectMany(reference => new[]
        {
            $"{DriverSourceNames.Get(reference.Source)} · {reference.Id} · {reference.Revision} · {reference.RetrievedAt.LocalDateTime:g}",
            reference.LandingPage?.AbsoluteUri ?? "", reference.Descriptor?.AbsoluteUri ?? "",
            string.Join("\n", reference.Payloads.Select(url => url.AbsoluteUri))
        });
        var constraints = new[] { catalog.Target.Os, catalog.Target.Architecture, catalog.Target.Oem, catalog.Target.Model,
            catalog.Target.MinBuild?.ToString() ?? "", catalog.Target.MaxBuild?.ToString() ?? "" };
        var components = catalog.Components.Select(component =>
            $"{component.Inf} · {component.ClassName} · {component.Provider} · {component.Version} · {component.Date}\n" +
            $"{string.Join(", ", component.ModelsSections)} · {component.Signer} · {component.Catalog} · {Certification(component.Certification)}" +
            (component.SignatureValid ? $" · {Text("Drivers_ValidSignature")}" : "") + "\n" +
            $"INF SHA256 {component.InfSha256}\nCAT SHA256 {component.CatalogSha256}");
        var comparisons = match.Comparisons.Select(item =>
            $"{item.Device.Name} · {item.Device.Version} → {item.Component.Version} · {Text("Drivers_Version_" + item.VersionRelation)} · " +
            $"{item.DeviceIdentifier.Value} · {item.DeviceIdentifier.Kind} #{item.DeviceIdentifier.Order + 1} · " +
            $"{item.InfIdentifier.Kind} #{item.InfIdentifier.Order + 1}" +
            (item.MoreSpecificIdentifier ? $" · {Text("Drivers_BetterIdentifier")}" : "") +
            (item.NewestFound ? $" · {Text("Drivers_NewestFound")}" : ""));
        string[] evidence =
        [
            overview, CatalogSummary(match), $"{Text("Drivers_PackageVersion")} · {catalog.PackageVersion}",
            string.Join(" · ", constraints.Where(value => value.Length > 0)),
            string.Join(", ", match.Devices.Select(device => device.Name)),
            string.Join("\n", comparisons), string.Join("\n", sourceLines), string.Join("\n", catalog.ListedHardwareIds),
            catalog.VerificationDetail, catalog.ClaimsWhql ? Text("Drivers_WhqlClaim") : "",
            catalog.PayloadSignatureValid ? Text("Drivers_ValidSignature") : "",
            string.Join("\n", catalog.Payloads.SelectMany(payload => payload.Hashes).Select(hash => $"{hash.Algorithm} {hash.Value}")),
            string.Join("\n", catalog.ContentSha256.Select(hash => $"SHA256 {hash}")),
            string.Join("\n", catalog.DeclaredComponents), string.Join("\n", catalog.DeclaredPrerequisites), string.Join("\n\n", components)
        ];
        return string.Join("\n", evidence.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private string Certification(DriverCertification certification) =>
        Text(certification == DriverCertification.Whql ? "Drivers_WhqlVerified" : "Drivers_WhqlUnknown");

    private string CatalogSummary(DriverUpdateMatch match)
    {
        var update = match.Update;
        if (update.Catalog is not { } catalog) return Certification(update.Certification);
        var installed = match.Devices.Select(device => device.Version).Where(version => version.Length > 0).Distinct().ToArray();
        var available = update.Version.Length > 0 ? update.Version : string.Join(", ", match.Comparisons
            .Select(item => item.Component.Version).Where(version => version.Length > 0).Distinct());
        var versions = available.Length > 0
            ? $"{string.Join(", ", installed.DefaultIfEmpty(Text("Drivers_Unknown")))} → {available}"
            : catalog.PackageVersion.Length > 0 ? $"{Text("Drivers_PackageVersion")} {catalog.PackageVersion}" : "";
        var relations = match.Comparisons.Select(item => Text("Drivers_Version_" + item.VersionRelation)).Distinct();
        return string.Join(" · ", new[]
        {
            string.Join(", ", catalog.References.Select(reference => DriverSourceNames.Get(reference.Source)).Distinct()),
            catalog.Branch, catalog.References.All(reference => reference.Source is DriverSources.Nvidia or DriverSources.Amd or
                DriverSources.Intel or DriverSources.Realtek) ? Text("Drivers_GenericAlternative") : "",
            catalog.OemRecommended ? Text("Drivers_OemRecommendation") : "",
            string.Join(", ", match.Devices.Select(device => device.Name).Distinct()),
            versions,
            Text("Drivers_Applicability_" + match.Applicability), string.Join(", ", relations), Certification(update.Certification),
            match.Comparisons.Any(item => item.NewestFound) ? Text("Drivers_NewestFound") : "",
            catalog.Verification == DriverVerification.Metadata ? Text("Drivers_VerificationPending") : "",
            catalog.Verification == DriverVerification.LicenseRequired ? Text("Drivers_LicenseRequired") : ""
        }.Where(value => value.Length > 0));
    }

    /// <summary>Formats the original scan records only at the presentation boundary.</summary>
    private void PublishRows(DriverScanReport report)
    {
        var packages = report.Inventory.Packages.ToDictionary(package => package.PublishedName, StringComparer.OrdinalIgnoreCase);
        AddGroup("Drivers_Unreferenced", report.UnreferencedPackages.Select(package => new DriverResultItem(
            package.OriginalName, package.Date, package.SizeBytes, () =>
                $"{Text("SystemInfo_Field_InfName")}: {package.PublishedName}\n{Text("SystemInfo_Field_Provider")}: {package.Provider}\n{Text("SystemInfo_Field_DeviceClass")}: {package.ClassName}\n{Text("SystemInfo_Field_DriverVersion")}: {package.Version}",
            new(DriverRemovalKind.Package, package.PublishedName), _localization, _dialogs,
            _pendingRemovals.Contains(new(DriverRemovalKind.Package, package.PublishedName)))));
        AddGroup("Drivers_Disconnected", report.DisconnectedDevices.Select(device =>
        {
            packages.TryGetValue(device.InfName, out var package);
            return new DriverResultItem(device.Name, device.Date, package?.SizeBytes, () =>
                $"{Text("SystemInfo_Field_Provider")}: {package?.Provider ?? Text("Drivers_Unknown")}\n{Text("SystemInfo_Field_DeviceClass")}: {device.ClassName}\n{Text("SystemInfo_Field_DriverVersion")}: {device.Version}\n{Text("SystemInfo_Field_InfName")}: {device.InfName}\n{Text("SystemInfo_Field_DeviceId")}: {device.InstanceId}\n{Text("Drivers_HardwareIds")}: {string.Join(", ", device.HardwareIds)}",
                new(DriverRemovalKind.Device, device.InstanceId), _localization, _dialogs,
                _pendingRemovals.Contains(new(DriverRemovalKind.Device, device.InstanceId)), device: device);
        }));
        AddGroup("Drivers_Updates", report.Updates
            .OrderByDescending(match => match.Comparisons.Any(item => item is { VersionRelation: DriverVersionRelation.Newer, NewestFound: true }))
            .ThenByDescending(match => match.Comparisons.Any(item => item.VersionRelation == DriverVersionRelation.Newer))
            .ThenByDescending(match => match.Applicability).Select(CandidateRow));
        if (report.Filtered.Count > 0) AddGroup("Drivers_Filtered", report.Filtered.Select(CandidateRow));
        if (report.Sources.Count > 0) AddGroup("Drivers_Sources", report.Sources.Select(outcome =>
        {
            var name = DriverSourceNames.Get(outcome.Source);
            string Coverage() => $"{Text("Drivers_Source_" + outcome.State)} · {outcome.Records:N0}";
            return new DriverResultItem(name, "", null,
                () => string.Join("\n", new[] { Coverage() }.Concat(outcome.Notices)), null, _localization, _dialogs,
                subtitle: Coverage, supportPage: outcome.SupportPage, openSupport: OpenSupportAsync);
        }));
        SelectedGroup = ResultGroups.FirstOrDefault(group => group.Items.Count > 0) ?? ResultGroups.FirstOrDefault();
        NotifySelection();
    }

    private DriverResultItem CandidateRow(DriverUpdateMatch match) => new(match.Update.Title,
        match.Update.Date, match.Update.SizeBytes, () => UpdateDetail(match), null, _localization, _dialogs,
        candidate: match, subtitle: () => CatalogSummary(match), supportPage: match.Update.Catalog?.References.FirstOrDefault()?.LandingPage,
        verify: _packages is null ? null : row => RunPackagesAsync([row], download: false), openSupport: OpenSupportAsync);

    private async Task OpenSupportAsync(Uri page)
    {
        if (_disposed || IsBusy || !page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(page) && !_disposed) Status = page.AbsoluteUri;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        { if (!_disposed) Status = ex.Message; }
    }

    private void AddGroup(string key, IEnumerable<DriverResultItem> rows)
    {
        var items = rows.ToArray();
        foreach (var row in items) row.PropertyChanged += OnSelectionChanged;
        var icon = key switch
        {
            "Drivers_Disconnected" => FluentIcons.Common.Icon.PlugConnected,
            "Drivers_Updates" => Icon.ArrowSync,
            "Drivers_Sources" => Icon.Database,
            _ => FluentIcons.Common.Icon.HardDrive
        };
        ResultGroups.Add(new(key, items, "", icon, _localization));
    }
    private void ClearResults()
    {
        SelectedGroup = null;
        foreach (var group in ResultGroups)
        {
            foreach (var row in group.Items) row.PropertyChanged -= OnSelectionChanged;
            group.Dispose();
        }
        ResultGroups.Clear();
        _selectedCount = 0;
        _downloadCount = 0;
        NotifySelection();
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => RunPackagesAsync(ResultGroups.SelectMany(group => group.Items).OfType<DriverResultItem>()
        .Where(row => row.IsDownloadSelected).ToArray(), download: true);

    /// <summary>Verifies or saves the selected official payloads under the existing cancellable task owner.</summary>
    private async Task RunPackagesAsync(DriverResultItem[] rows, bool download)
    {
        if (_disposed || IsBusy || _packages is null || _report is not { } report || rows.Length == 0) return;
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        IsBusy = true;
        CancellationTokenSource? owner = null;
        using var lifetime = new CancellationTokenSource();
        _operation = lifetime;
        try
        {
            var gated = rows.Where(row => row.Candidate?.Update.Catalog?.LicenseRequired == true).ToArray();
            if (gated.Length > 0)
            {
                var confirmation = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
                {
                    Title = Text("Drivers_LicenseRequired"), Message = Text("Drivers_LicenseConfirm"),
                    Items = gated.Select(row => $"{row.Name}\n{row.Candidate!.Update.Catalog!.References[0].LandingPage}").ToArray(),
                    ConfirmButtonText = download ? DownloadLabel : Text("Drivers_Verify"), CancelButtonText = CancelLabel
                });
                if (!confirmation.Confirmed || _disposed || lifetime.IsCancellationRequested) return;
            }
            if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
            owner = _tasks.StartTask(download ? DownloadLabel : Text("Drivers_Verify"), isIndeterminate: true);
            _operation = owner;
            var changed = new Dictionary<DriverUpdateMatch, DriverUpdateMatch>();
            var paths = new List<string>();
            var failures = new List<string>();
            foreach (var row in rows)
            {
                owner.Token.ThrowIfCancellationRequested();
                if (row.Candidate is not { } candidate) continue;
                try
                {
                    var acquired = await _packages.AcquireAsync(candidate.Update, report.Inventory, download, CreateProgress(owner),
                        owner.Token, licenseAccepted: gated.Contains(row));
                    owner.Token.ThrowIfCancellationRequested();
                    if (_operation != owner || _disposed) return;
                    changed[candidate] = DriverCatalogMatching.Evaluate(acquired.Update, report.Inventory);
                    if (acquired.OutputPath is { } path) paths.Add(path);
                }
                catch (DriverScanException ex)
                {
                    failures.Add($"{row.Name}: {ex.Message}");
                    if (!download && candidate.Update.Catalog is { } catalog)
                        changed[candidate] = candidate with { Update = candidate.Update with
                        { Catalog = catalog with { Verification = DriverVerification.Failed, VerificationDetail = ex.Message } } };
                }
            }
            owner.Token.ThrowIfCancellationRequested();
            if (_operation != owner || _disposed) return;
            var removalSelection = ResultGroups.SelectMany(group => group.Items).OfType<DriverResultItem>()
                .Where(row => row.IsSelected).Select(row => row.Target).ToHashSet();
            var selectedDownloads = ResultGroups.SelectMany(group => group.Items).OfType<DriverResultItem>()
                .Where(row => row.IsDownloadSelected && (!download || !changed.ContainsKey(row.Candidate!)))
                .Select(row => row.Candidate!.Update).ToHashSet();
            var category = SelectedGroup is { } group ? ResultGroups.IndexOf(group) : 0;
            var reviewed = DriverCatalogMatching.Deduplicate(report.Updates.Concat(report.Filtered)
                    .Select(match => changed.GetValueOrDefault(match, match).Update))
                .Select(update => DriverCatalogMatching.Evaluate(update, report.Inventory)).ToArray();
            var filtered = reviewed.Where(match => match.Applicability == DriverApplicability.Incompatible ||
                (report.Options.OnlyWhqlUpdates && match.Update.Certification != DriverCertification.Whql)).ToHashSet();
            _report = report with { Updates = DriverCatalogMatching.MarkNewest(reviewed.Where(match => !filtered.Contains(match)).ToArray()),
                Filtered = filtered.ToArray(), ExcludedUpdates = filtered.Count };
            ClearResults();
            PublishRows(_report);
            if (category >= 0 && category < ResultGroups.Count) SelectedGroup = ResultGroups[category];
            foreach (var row in ResultGroups.SelectMany(result => result.Items).OfType<DriverResultItem>())
            {
                row.IsSelected = row.Target is not null && removalSelection.Contains(row.Target);
                row.IsDownloadSelected = row.Candidate is { } candidate && selectedDownloads.Any(update =>
                    update.Id == candidate.Update.Id && update.Catalog?.References[0].Revision == candidate.Update.Catalog?.References[0].Revision);
            }
            Notices = string.Join(Environment.NewLine, report.Notices.Concat(failures));
            Status = failures.Count > 0 ? string.Join(Environment.NewLine, failures.Concat(paths)) :
                download ? string.Join(Environment.NewLine, paths) : string.Join(Environment.NewLine, changed.Values.Select(match =>
                    string.Join(Environment.NewLine, new[] { CatalogSummary(match), match.Update.Catalog?.VerificationDetail }
                        .Where(value => !string.IsNullOrEmpty(value)))));
            NotifyReport();
        }
        catch (OperationCanceledException) { if (!_disposed) Status = Text("Cleaner_Cancelled"); }
        catch (Exception ex) when (ex is DriverScanException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ReportFailure(ex.Message, owner); }
        finally
        {
            _operation = null;
            if (owner is not null && ReferenceEquals(_tasks.CurrentTaskCancellationSource, owner)) _tasks.CompleteTask();
            IsBusy = false;
        }
    }

    /// <summary>Confirms the exact batch, retains successful changes on cancellation and keeps failed rows selected.</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (!CanUninstall || _report is null) return;
        if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
        var selected = ResultGroups.SelectMany(group => group.Items).OfType<DriverResultItem>()
            .Where(row => row.IsSelected && row.Target is not null).ToArray();
        var report = _report;
        IsBusy = true;
        CancellationTokenSource? owner = null;
        using var lifetime = new CancellationTokenSource();
        _operation = lifetime;
        try
        {
            using var reservation = _mode.BeginSystemOperation();
            var response = await _dialogs.ShowConfirmationAsync(new ConfirmationRequest
            {
                Title = UninstallLabel, Message = Text("Drivers_UninstallConfirm"),
                Items = selected.Select(row => $"{row.Name} ({row.Target!.Id})").ToArray(),
                ConfirmButtonText = UninstallLabel, CancelButtonText = CancelLabel
            });
            if (!response.Confirmed || _disposed || lifetime.IsCancellationRequested) return;
            if (_tasks.IsTaskRunning) { Status = Text("Cleaner_Wait"); return; }
            owner = _tasks.StartTask(UninstallLabel, isIndeterminate: true);
            _operation = owner;
            Status = Text("Drivers_Removing");
            var result = await _removal.RemoveAsync(report, selected.Select(row => row.Target).OfType<DriverRemovalTarget>().ToArray(), CreateProgress(owner), owner.Token);
            if (_disposed) return;
            var removed = result.Outcomes.Where(outcome => outcome.Success && !outcome.RestartRequired)
                .Select(outcome => outcome.Target).ToHashSet();
            var pending = result.Outcomes.Where(outcome => outcome.Success && outcome.RestartRequired)
                .Select(outcome => outcome.Target).ToArray();
            _pendingRemovals.UnionWith(pending);
            var categoryIndex = SelectedGroup is { } category ? ResultGroups.IndexOf(category) : 0;
            var downloads = ResultGroups.SelectMany(group => group.Items).OfType<DriverResultItem>()
                .Where(row => row.IsDownloadSelected).Select(row => row.Candidate).ToHashSet();
            _report = report with
            {
                UnreferencedPackages = report.UnreferencedPackages.Where(package => !removed.Contains(new(DriverRemovalKind.Package, package.PublishedName))).ToArray(),
                DisconnectedDevices = report.DisconnectedDevices.Where(device => !removed.Contains(new(DriverRemovalKind.Device, device.InstanceId))).ToArray()
            };
            ClearResults();
            PublishRows(_report);
            SelectedGroup = ResultGroups[Math.Clamp(categoryIndex, 0, ResultGroups.Count - 1)];
            var remaining = selected.Select(row => row.Target).Where(target => target is not null && !removed.Contains(target)).ToHashSet();
            for (var index = 0; index < ResultGroups.Count; index++)
            {
                foreach (var row in ResultGroups[index].Items.OfType<DriverResultItem>())
                {
                    row.IsSelected = row.Target is not null && remaining.Contains(row.Target);
                    row.IsDownloadSelected = row.Candidate is not null && downloads.Contains(row.Candidate);
                }
            }
            var failures = result.Outcomes.Where(outcome => !outcome.Success).ToArray();
            Notices = string.Join(Environment.NewLine, report.Notices.Concat(failures.Select(outcome => $"{outcome.Target.Id}: {outcome.Error}")));
            Status = string.Format(Text("Drivers_RemovalComplete"), removed.Count, failures.Length);
            if (result.Cancelled) Status += " " + Text("Drivers_RemovalCancelled");
            if (pending.Length != 0) Status += " " + Text("Drivers_RestartRequired");
            NotifyReport();
        }
        catch (OperationCanceledException) { if (!_disposed) Status = Text("Drivers_RemovalCancelled"); }
        catch (Exception ex) when (ex is DriverScanException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { ReportFailure(ex.Message, owner); }
        finally
        {
            _operation = null;
            if (owner is not null && ReferenceEquals(_tasks.CurrentTaskCancellationSource, owner)) _tasks.CompleteTask();
            IsBusy = false;
        }
    }

    /// <summary>Retains the page error and marks only this operation's footer task as failed.</summary>
    private void ReportFailure(string message, CancellationTokenSource? owner)
    {
        if (_disposed) return;
        Status = message;
        if (owner is not null && ReferenceEquals(_tasks.CurrentTaskCancellationSource, owner))
            _tasks.UpdateDetailedProgress(new TaskProgressDetail
            {
                LogLevel = LogLevel.Error, StatusText = Heading, TerminalOutput = message
            });
    }

    /// <summary>Maintains the summary in constant time, including bulk group selections.</summary>
    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DriverResultItem row) return;
        int delta;
        if (e.PropertyName == nameof(DriverResultItem.IsSelected)) _selectedCount += delta = row.IsSelected ? 1 : -1;
        else if (e.PropertyName == nameof(DriverResultItem.IsDownloadSelected)) _downloadCount += delta = row.IsDownloadSelected ? 1 : -1;
        else return;
        if (_visibleSelectableRows.Contains(row))
        {
            _visibleSelectedCount += delta;
            OnPropertyChanged(nameof(VisibleSelection));
        }
        NotifySelection();
    }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectionSummary)); OnPropertyChanged(nameof(CanUninstall));
        UninstallCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(DownloadSelectionSummary));
        OnPropertyChanged(nameof(ShowRemovalAction));
        OnPropertyChanged(nameof(ShowDownloadAction));
        OnPropertyChanged(nameof(ShowSelectionFooter));
        OnPropertyChanged(nameof(SelectionHint));
        DownloadCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedGroupChanged(SelectableResultGroup? value)
    {
        UpdateVisibleRows();
        NotifySelection();
    }

    partial void OnDeviceQueryChanged(string value)
    {
        if (HasDeviceSearch) UpdateVisibleRows();
    }

    /// <summary>Filters the current category while retaining the inventory and its selections.</summary>
    private void UpdateVisibleRows()
    {
        var query = DeviceQuery.Trim();
        var rows = SelectedGroup?.Items.Cast<DriverResultItem>() ?? [];
        if (HasDeviceSearch && query.Length > 0) rows = rows.Where(row => row.MatchesDevice(query));
        var visible = rows.ToArray();
        _visibleSelectableRows.Clear();
        _visibleSelectedCount = 0;
        foreach (var row in visible)
        {
            if (!row.CanClean && !row.CanDownload) continue;
            _visibleSelectableRows.Add(row);
            if (row.IsSelected || row.IsDownloadSelected) _visibleSelectedCount++;
        }
        VisibleRows = visible;
        OnPropertyChanged(nameof(HasVisibleSelection));
        OnPropertyChanged(nameof(VisibleSelection));
    }
    partial void OnIsBusyChanged(bool value)
    {
        NotifySelection();
    }

    /// <summary>Publishes scan milestones to the page and measured progress to the shared footer.</summary>
    private IProgress<DriverScanProgress> CreateProgress(CancellationTokenSource owner) => new ScanProgress(value =>
        _dispatcher.RunOnUIThread(() =>
        {
            if (_disposed || _operation != owner || owner.IsCancellationRequested) return;
            if (IsScanning && value.Phase > ScanPhase) ScanPhase = value.Phase;
            double? progress = value.Total > 0 ? Math.Clamp(value.Completed * 100d / value.Total, 0, 100) : null;
            var status = Text(value.Stage) + (value.Total > 0 ? $" ({value.Completed}/{value.Total})" : "");
            _tasks.UpdateDetailedProgress(new TaskProgressDetail
            {
                Progress = progress, IsIndeterminate = progress is null, StatusText = Heading,
                TerminalOutput = status, IsProgressIndicator = value.Phase == DriverScanPhase.None
            });
        }));
    private void OnModeChanged(object? sender, EventArgs e)
    {
        NotifySelection();
        OnPropertyChanged(nameof(HasTestModeHint));
        OnPropertyChanged(nameof(TestModeHint));
    }
    /// <summary>Publishes presentation derived from the current report without adding scan state.</summary>
    private void NotifyReport()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(RescanHint));
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(IntroDescription));
        OnPropertyChanged(nameof(HasLimitedCoverage));
        OnPropertyChanged(nameof(CoverageSummary));
        OnPropertyChanged(nameof(CoverageDetails));
        OnPropertyChanged(nameof(ShowStandaloneNotices));
        OnPropertyChanged(nameof(ReportFilterLabel));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasRescanHint));
        OnPropertyChanged(nameof(ShowScanInformation));
        OnPropertyChanged(nameof(HasEmptyCategory));
        OnPropertyChanged(nameof(CategoryEmptyTitle));
        OnPropertyChanged(nameof(CategoryEmptyDescription));
    }
    private string Text(string key) => _localization.GetString(key);
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.Options))
        {
            OnPropertyChanged(nameof(FilterSummary));
            OnPropertyChanged(nameof(RescanHint));
            OnPropertyChanged(nameof(HasRescanHint));
        }
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(string.Empty);
        foreach (var group in ResultGroups) { group.RefreshText(); foreach (var row in group.Items.OfType<DriverResultItem>()) row.RefreshText(); }
    }
    public void Dispose()
    {
        _disposed = true; _operation?.Cancel(); ClearResults();
        _localization.LanguageChanged -= OnLanguageChanged; Settings.PropertyChanged -= OnSettingsChanged;
        _mode.ModeChanged -= OnModeChanged;
    }
    private sealed class ScanProgress(Action<DriverScanProgress> callback) : IProgress<DriverScanProgress>
    { public void Report(DriverScanProgress value) => callback(value); }
}

/// <summary>A labeled step with its status icon for the scan progress card.</summary>
public sealed record DriverScanMilestone(string Label, Icon Icon, bool IsCurrent);

/// <summary>A completed or active operation shown in recent scan activity.</summary>
public sealed record DriverScanActivity(string Text, Icon Icon, bool IsActive = false)
{
    public bool IsComplete => !IsActive;
}
