using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.AdvancedTools;

/// <summary>Shares the latest presentation and change command between the card and its dialog.</summary>
public sealed partial class ExperimentalFeatureCard(
    string key, IAsyncRelayCommand<ExperimentalFeatureAction> change) : ObservableObject
{
    public string Key { get; } = key;
    public IAsyncRelayCommand<ExperimentalFeatureAction> Change { get; } = change;

    [ObservableProperty]
    public partial ExperimentalFeaturePresentation Presentation { get; set; } = null!;
}

/// <summary>Localized text and actions for one discovery result.</summary>
public sealed record ExperimentalFeaturePresentation(string Name, string Description, string Category,
    string Configured, string Runtime, string Restart, string CompatibilityNotice, string DetailsLabel,
    ExperimentalFeatureAction Enable, ExperimentalFeatureAction Disable, ExperimentalFeatureAction Restore,
    ExperimentalFeatureAction Undo, string ActionReasons, string RecoveryNotice, string CompatibilityEvidence,
    string Builds, string Prerequisite, string SharedIds, ExperimentalFeatureIdDetails[] FeatureIds, Uri[] Sources)
{
    public Visibility CompatibilityNoticeVisibility => CompatibilityNotice.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActionReasonsVisibility => ActionReasons.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public bool RecoveryRequired => RecoveryNotice.Length > 0;
    public Visibility PrerequisiteVisibility => Prerequisite.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SharedIdsVisibility => SharedIds.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Groups the action targets and the three configuration observations for one feature ID.</summary>
public sealed record ExperimentalFeatureIdDetails(string Id, string Role, ExperimentalDetailRow[] Rows)
{
    public Visibility RoleVisibility => Role.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>Pairs a localized diagnostic label with a selectable value.</summary>
public sealed record ExperimentalDetailRow(string Label, string Value);

/// <summary>Identifies the exact card and action represented by a button.</summary>
public sealed record ExperimentalFeatureAction(string FeatureKey, ExperimentalAction Action, string Label, string HelpText);

/// <summary>Presents the local catalog and explicit changes, with live per-store diagnostics.</summary>
public sealed partial class ExperimentalFeaturesPage : Page
{
    private readonly IExperimentalFeatures _features = App.Services.GetRequiredService<IExperimentalFeatures>();
    private readonly ILocalizationService _localization = App.Services.GetRequiredService<ILocalizationService>();
    private readonly IApplicationModeService _mode = App.Services.GetRequiredService<IApplicationModeService>();
    private readonly ILogService _log = App.Services.GetRequiredService<ILogService>();
    private readonly AsyncRelayCommand<ExperimentalFeatureAction> _changeFeature;
    private CancellationTokenSource? _operation;
    private ExperimentalDiscovery? _snapshot;
    private ExperimentalChangeResult? _result;
    private ExperimentalFeatureCard[] _cards = [];
    private string? _recoveryFeature;
    private ExperimentalFeatureCard? _detailsCard;
    private ContentDialog? _detailsDialog;
    private Task? _detailsTask;

    public ExperimentalFeaturesPage()
    {
        _changeFeature = new AsyncRelayCommand<ExperimentalFeatureAction>(RunFeatureActionAsync, CanChangeFeature);
        InitializeComponent();
        Loaded += PageLoaded;
        Unloaded += PageUnloaded;
    }

    private async void PageLoaded(object sender, RoutedEventArgs args)
    {
        _localization.LanguageChanged += LanguageChanged;
        _mode.ModeChanged += ModeChanged;
        Relocalize();
        await RunAsync();
    }

    private void PageUnloaded(object sender, RoutedEventArgs args)
    {
        _localization.LanguageChanged -= LanguageChanged;
        _mode.ModeChanged -= ModeChanged;
        _detailsDialog?.Hide();
    }

    private void LanguageChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(Relocalize);
    private void ModeChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        RenderCatalog();
        RenderBusy();
    });
    private void SearchChanged(object sender, TextChangedEventArgs args) => ApplySearch();
    private async void RefreshClicked(object sender, RoutedEventArgs args) => await RunAsync();
    private bool CanChangeFeature(ExperimentalFeatureAction? action) =>
        action is { HelpText.Length: 0 } && _operation is null && _mode.CurrentMode == WinOptimizerMode.Normal;

    private async Task RunFeatureActionAsync(ExperimentalFeatureAction? action)
    {
        if (action is null || !CanChangeFeature(action)) return;
        // Return to the page before the operation so its progress, cancellation and report are visible.
        if (_detailsDialog is { } dialog && _detailsTask is { } detailsTask)
        {
            dialog.Hide();
            await detailsTask;
        }
        if (IsLoaded && CanChangeFeature(action)) await RunAsync(action);
    }

    private void DetailsClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { CommandParameter: ExperimentalFeatureCard card }) OpenDetails(card);
    }

    /// <summary>Shows feature diagnostics in the shared modal frame and keeps the body within the host viewport.</summary>
    private async void OpenDetails(ExperimentalFeatureCard card, bool recovery = false)
    {
        if (_detailsTask is { IsCompleted: false } active)
        {
            if (recovery && ReferenceEquals(_detailsCard, card)) return;
            await active;
            if (_detailsTask is { IsCompleted: false } || (recovery && _recoveryFeature != card.Key)) return;
        }
        await (_detailsTask = PopupHandler.RunModalAsync(async () =>
        {
            if (!IsLoaded || !_cards.Contains(card) || XamlRoot is not { } root) return ContentDialogResult.None;

            var content = new ContentControl
            {
                Content = card,
                ContentTemplate = (DataTemplate)Resources["FeatureDetailsTemplate"],
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsTabStop = false
            };
            var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
            {
                Content = content,
                CloseButtonText = L("Button_Close"),
                DefaultButton = ContentDialogButton.Close
            });
            dialog.SetBinding(ContentDialog.TitleProperty, new Binding
            {
                Source = card, Path = new PropertyPath("Presentation.Name")
            });
            var maxWidth = (double)Microsoft.UI.Xaml.Application.Current.Resources["Popup.MaxWidth"];
            dialog.Resources["ContentDialogMaxWidth"] = maxWidth;
            void Resize(XamlRoot sender, XamlRootChangedEventArgs? args)
            {
                // Leave space for the native frame, title and close button.
                content.Width = Math.Max(0, Math.Min(maxWidth, sender.Size.Width - 48) - 50);
                content.MaxHeight = Math.Max(0, sender.Size.Height - 200);
            }

            Resize(root, null);
            root.Changed += Resize;
            _detailsDialog = dialog;
            _detailsCard = card;
            try { return await PopupHandler.ShowDialogAsync(dialog, root); }
            finally
            {
                root.Changed -= Resize;
                dialog.Content = null;
                _detailsDialog = null;
                _detailsCard = null;
            }
        }));
    }

    private void CancelClicked(object sender, RoutedEventArgs args) => _operation?.Cancel();
    private async void ImportProfilesClicked(object sender, RoutedEventArgs args)
    {
        if (_operation is not null || _mode.CurrentMode != WinOptimizerMode.Normal) return;
        var title = L("Experimental_ImportProfiles");
        var path = App.Services.GetRequiredService<IFilePickerService>().PickFile([title, "*.json"]);
        if (path is not null) await RunAsync(profilePath: path);
    }

    /// <summary>Requeries after every action, including cancellation, so stale data never blocks recovery.</summary>
    private async Task RunAsync(ExperimentalFeatureAction? action = null, string? profilePath = null)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        _result = null;
        SetNotice(null);
        RenderBusy();
        try
        {
            if (profilePath is not null)
            {
                try
                {
                    var count = await _features.ImportProfilesAsync(profilePath, cancellation.Token);
                    SetNotice(_localization.GetString("Experimental_ProfilesImported", count), InfoBarSeverity.Success);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                    InvalidOperationException or System.Security.SecurityException or System.Text.Json.JsonException)
                {
                    SetNotice(ex.Message, InfoBarSeverity.Error);
                    return;
                }
            }
            if (action is not null)
            {
                _result = await _features.ChangeAsync(action.FeatureKey, action.Action, cancellation.Token);
                RenderResult(_result);
            }
            _snapshot = await _features.DiscoverAsync(action is not null || profilePath is not null
                ? CancellationToken.None : cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetNotice(L("Restore_Cancelled"), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _snapshot = null;
            _log.LogError("[Experimental Features] Discovery failed.", ex);
            SetNotice(L("Experimental_QueryFailed") + " " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _operation = null;
            RenderCatalog();
            RenderBusy();
        }
    }

    private void Relocalize()
    {
        Heading.Title = L("Experimental_Title");
        Heading.Description = L("Experimental_Description");
        SearchBox.PlaceholderText = L("Common_Search_Placeholder");
        AutomationProperties.SetName(SearchBox, L("Common_Search_Placeholder"));
        AutomationProperties.SetName(Catalog, Heading.Title);
        RefreshButton.Content = L("Button_Refresh");
        ImportProfilesButton.Content = L("Experimental_ImportProfiles");
        ToolTipService.SetToolTip(ImportProfilesButton, L("Experimental_ImportProfilesHint"));
        CancelButton.Content = L("Button_Cancel");
        if (_detailsDialog is { } dialog) dialog.CloseButtonText = L("Button_Close");
        NoMatches.Text = L("Cleaner_NoResults");
        RenderCatalog();
        if (_result is { } result) RenderResult(result);
    }

    private void RenderBusy()
    {
        var busy = _operation is not null;
        Progress.IsActive = busy;
        Progress.Visibility = CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy;
        ImportProfilesButton.IsEnabled = !busy && _mode.CurrentMode == WinOptimizerMode.Normal;
        if (busy) Report.Visibility = Visibility.Collapsed;
        _changeFeature.NotifyCanExecuteChanged();
    }

    /// <summary>Updates cards from the current snapshot and surfaces newly required recovery.</summary>
    private void RenderCatalog()
    {
        var existing = _cards.ToDictionary(card => card.Key);
        var recoveryFeature = _snapshot is { RecoveryRequired: true } recovery ? recovery.UndoFeature : null;
        _cards = _snapshot is { } snapshot ? snapshot.Features.Select(status =>
        {
            var key = status.Feature.Key;
            var card = existing.GetValueOrDefault(key) ?? new ExperimentalFeatureCard(key, _changeFeature);
            card.Presentation = PresentFeature(status, snapshot);
            return card;
        }).ToArray() : [];
        var offerRecovery = recoveryFeature is not null && recoveryFeature != _recoveryFeature;
        _recoveryFeature = recoveryFeature;
        WindowsText.Text = _snapshot is { Windows: var windows }
            ? $"Windows {windows.DisplayVersion} · {windows.Build}.{windows.Revision} · {windows.Architecture}"
            : "";
        WindowsText.Visibility = _snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        ApplySearch();
        if (_detailsCard is { } openCard && !_cards.Contains(openCard)) _detailsDialog?.Hide();
        if (offerRecovery && _cards.FirstOrDefault(card => card.Key == recoveryFeature) is { } recoveryCard)
            OpenDetails(recoveryCard, recovery: true);
    }

    private void ApplySearch()
    {
        if (Catalog is null || NoMatches is null) return;
        var search = SearchBox.Text.Trim();
        var rows = search.Length == 0 ? _cards : _cards.Where(card =>
        {
            var text = card.Presentation;
            return $"{text.Name} {text.Description} {text.Category}".Contains(search, StringComparison.CurrentCultureIgnoreCase);
        }).ToArray();
        if (Catalog.ItemsSource is not ExperimentalFeatureCard[] current || !current.SequenceEqual(rows))
            Catalog.ItemsSource = rows;
        Catalog.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoMatches.Visibility = _snapshot is not null && rows.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Moves the action group beside the summary when the card has enough room.</summary>
    private void FeatureHeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid grid ||
            grid.FindName("FeatureSummary") is not FrameworkElement summary ||
            grid.FindName("PrimaryActions") is not FrameworkElement actions) return;

        var sideBySide = args.NewSize.Width >= 840;
        if (sideBySide == (Grid.GetColumn(actions) == 1)) return;
        Grid.SetColumnSpan(summary, sideBySide ? 1 : 2);
        Grid.SetColumnSpan(actions, sideBySide ? 1 : 2);
        Grid.SetColumn(actions, sideBySide ? 1 : 0);
        Grid.SetRow(actions, sideBySide ? 0 : 1);
        actions.Margin = sideBySide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 8, 0, 0);
    }

    private ExperimentalFeaturePresentation PresentFeature(ExperimentalFeatureStatus status, ExperimentalDiscovery snapshot)
    {
        var feature = status.Feature;
        var observations = feature.Requirements.Select(item => snapshot.Observations[item.Id]).ToArray();
        var configured = Line("Experimental_Configured", string.Join(", ", observations.Select(item =>
            State(item.Persistent, item.PersistenceError, absentOverride: true)).Distinct()));
        var runtime = Line("Experimental_Runtime", string.Join(", ", observations.Select(item =>
            State(item.Runtime?.Value, item.RuntimeError)).Distinct()));
        var restart = observations.Select(item => snapshot.Restarts.GetValueOrDefault(item.Id)).Max();
        var restartText = restart switch
        {
            ExperimentalRestart.Pending => L("Drivers_RemovalPending"),
            ExperimentalRestart.Unverified => Line("Experimental_Restart", L("Experimental_Unverified")),
            _ => L("Experimental_NoPendingRestart")
        };

        var reasons = new List<string>();
        string? modeReason = _mode.CurrentMode != WinOptimizerMode.Normal ? "Experimental_NormalMode" : null;
        ExperimentalFeatureAction Action(ExperimentalAction action, string? reason)
        {
            var label = L(ExperimentalActionText.Key(action));
            var help = (modeReason ?? reason) is { } blocked ? L(blocked) : "";
            if (help.Length > 0) reasons.Add($"{label} · {help}");
            return new(feature.Key, action, label, help);
        }

        var enable = Action(ExperimentalAction.Enable, status.EnableBlocked);
        var disable = Action(ExperimentalAction.Disable, status.DisableBlocked);
        var restore = Action(ExperimentalAction.RestoreDefault, status.RestoreBlocked);
        var undo = Action(ExperimentalAction.Undo,
            snapshot.UndoFeature == feature.Key ? snapshot.UndoBlocked : "Experimental_NoUndo");
        var compatibilityNotice = status.Availability.Compatibility == ExperimentalCompatibility.Supported
            ? "" : L(status.Availability.ReasonKey);

        var evidence = L(feature.Imported ? "Experimental_ImportedProfile" : feature.Verified ? "Experimental_Supported" : "Experimental_Unverified");
        var builds = string.Join("  ·  ", feature.Builds.Select(build =>
            $"{build.Build}.{build.Revision} ({string.Join(", ", build.Architectures)})"));
        var prerequisite = feature.Prerequisites.Contains(ExperimentalPrerequisite.EnglishWindows) ? L("Experimental_EnglishWindows") : "";
        var sharedIds = status.SharedIds.Length > 0 ? Line("Experimental_SharedIds", string.Join(", ", status.SharedIds)) : "";
        var recoveryNotice = snapshot.RecoveryRequired && snapshot.UndoFeature == feature.Key ? L("Experimental_RecoveryRequired") : "";
        var featureIds = new ExperimentalFeatureIdDetails[observations.Length];
        for (var index = 0; index < featureIds.Length; index++)
        {
            var item = observations[index];
            var requirement = feature.Requirements[index];
            var role = requirement.Dependency ? L("TechnicalDetails_Section_Dependencies") : "";
            var rows = new List<ExperimentalDetailRow>(5)
            {
                new(L("TechnicalDetails_Script_OnEnable"), State(requirement.Enable, null))
            };
            if (requirement.Disable is { } disableOverride)
                rows.Add(new(L("TechnicalDetails_Script_OnDisable"), State(disableOverride, null)));
            rows.Add(new(L("Experimental_Configured"), State(item.Persistent, item.PersistenceError, absentOverride: true, details: true)));
            rows.Add(new(L("Experimental_Runtime"), Observed(item.Runtime, item.RuntimeError, details: true)));
            rows.Add(new(L("Experimental_Startup"), Observed(item.Startup, item.StartupError, details: true)));
            featureIds[index] = new(Number(item.Id), role, rows.ToArray());
        }
        return new(L(feature.NameKey), L(feature.DescriptionKey), L(feature.CategoryKey), configured, runtime, restartText,
            compatibilityNotice, L("View_TechnicalDetails"), enable, disable, restore, undo, string.Join(Environment.NewLine, reasons),
            recoveryNotice, evidence, builds, prerequisite, sharedIds, featureIds, feature.Sources.ToArray());
    }

    private void RenderResult(ExperimentalChangeResult result)
    {
        var failed = result.Error is not null || result.RecoveryRequired;
        var message = result.Error is { } error ? L(error) : L(result.Cancelled ? "Experimental_Cancelled" :
            failed ? "Experimental_ChangeFailed" : result.Items.Any(item =>
                item.Runtime == ExperimentalWriteStage.Verified || item.Persistence == ExperimentalWriteStage.Verified)
                ? "Experimental_ChangeVerified" : "Experimental_NoChange");
        if (result.RecoveryRequired) message += " " + L("Experimental_RecoveryRequired");
        SetNotice(message, failed || result.Cancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        Report.Text = string.Join(Environment.NewLine, result.Items.Select(item =>
            $"{item.Id} · {Line("Experimental_Runtime", Stage(item.Runtime, item.RuntimeError))} · " +
            Line("Experimental_Configured", Stage(item.Persistence, item.PersistenceError))));
        Report.Visibility = result.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetNotice(string? message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Notice.Message = message ?? "";
        Notice.Severity = severity;
        Notice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        Notice.IsOpen = message is not null;
    }

    private string Observed(ObservedFeature? feature, string? error, bool details = false) =>
        State(feature?.Value, error, details: details) + (feature is null ? "" : $" (P{feature.Priority})");

    private string State(FeatureOverride? value, string? error, bool absentOverride = false, bool details = false)
    {
        if (error is not null) return details ? error : L("Utilities_Unknown");
        if (value is null) return L(absentOverride ? "Experimental_NoOverride" : "SystemInfo_Unknown");
        var state = L(value.State switch
        {
            FeatureOverrideState.Enabled => "TechnicalDetails_State_Enabled",
            FeatureOverrideState.Disabled => "TechnicalDetails_State_Disabled",
            FeatureOverrideState.Default => "TechnicalDetails_DefaultValue",
            _ => "SystemInfo_Unknown"
        });
        return details ? $"{state} · WEXP={Number(value.Options)} · variant={Number(value.Variant)} · " +
            $"payload={Number(value.Payload)} ({Number(value.PayloadKind)})" : state;
    }

    private static string Number(uint? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private string Stage(ExperimentalWriteStage stage, string? error) => error ?? L(stage switch
    {
        ExperimentalWriteStage.Verified => "Experimental_ChangeVerified",
        ExperimentalWriteStage.Unchanged => "Experimental_NoChange",
        _ => "Experimental_Unverified"
    });

    private string Line(string key, string value) => $"{L(key)} · {value}";
    private string L(string key)
    {
        var translation = _localization.GetString(key);
        return translation == $"[{key}]" ? key : translation;
    }
}
