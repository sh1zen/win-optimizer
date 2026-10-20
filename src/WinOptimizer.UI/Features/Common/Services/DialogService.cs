using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.Common.Services;

/// <summary>
/// WinUI 3 implementation of IDialogService using ContentDialog.
/// </summary>
/// <remarks>
/// The shared popup handler serializes modal preparation and display,
/// as WinUI 3 throws an exception if multiple ContentDialogs are open simultaneously.
/// </remarks>
public class DialogService : IDialogService
{
    private readonly ILocalizationService _localization;
    private readonly ILogService _logService;
    private readonly ITaskProgressService _taskProgressService;
    private readonly ISponsorsService _sponsorsService;

    /// <summary>
    /// The XamlRoot required for showing ContentDialogs.
    /// Must be set by MainWindow after content is loaded.
    /// </summary>
    public XamlRoot? XamlRoot { get; set; }

    public DialogService(ILocalizationService localization, ILogService logService, ITaskProgressService taskProgressService, ISponsorsService sponsorsService)
    {
        _localization = localization;
        _logService = logService;
        _taskProgressService = taskProgressService;
        _sponsorsService = sponsorsService;
    }

    #region Guard Helpers

    /// <summary>
    /// Acquires the dialog semaphore, checks XamlRoot, and executes the dialog action.
    /// Returns <paramref name="defaultValue"/> if XamlRoot is null.
    /// </summary>
    private async Task<T> ExecuteDialogAsync<T>(Func<Task<T>> dialogAction, T defaultValue)
    {
        return await PopupHandler.RunModalAsync(async () =>
        {
            if (XamlRoot == null)
            {
                _logService.LogWarning("[DialogService] XamlRoot is null");
                return defaultValue;
            }
            return await dialogAction();
        });
    }

    /// <summary>
    /// Acquires the dialog semaphore, checks XamlRoot, and executes a void dialog action.
    /// </summary>
    private async Task ExecuteDialogAsync(Func<Task> dialogAction)
    {
        await ExecuteDialogAsync(async () => { await dialogAction(); return true; }, true);
    }

    #endregion

    #region Simple Dialogs

    /// <summary>
    /// Shared implementation for ShowInformationAsync, ShowWarningAsync, and ShowErrorAsync.
    /// </summary>
    private async Task ShowSimpleDialogAsync(string message, string title, string buttonText)
    {
        await ExecuteDialogAsync(async () =>
        {
            var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
            {
                Title = title,
                Content = message,
                CloseButtonText = buttonText,
                DefaultButton = ContentDialogButton.Close
            });
            await PopupHandler.ShowDialogAsync(dialog, XamlRoot!);
        });
    }

    public void ShowMessage(string message, string title = "")
    {
        // Fire-and-forget for non-async message display
        _ = ShowInformationAsync(message, title);
    }

    public async Task ShowInformationAsync(string message, string title = "Information", string buttonText = "OK")
        => await ShowSimpleDialogAsync(message, title, buttonText);

    public async Task ShowWarningAsync(string message, string title = "Warning", string buttonText = "OK")
        => await ShowSimpleDialogAsync(message, title, buttonText);

    public async Task ShowErrorAsync(string message, string title = "Error", string buttonText = "OK")
        => await ShowSimpleDialogAsync(message, title, buttonText);

    #endregion

    /// <summary>Resolves a test draft through the shared dialog queue without applying anything itself.</summary>
    public Task<TestModeExitAction> ShowTestModeExitDialogAsync() => ExecuteDialogAsync(async () =>
    {
        var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
        {
            Title = _localization.GetString("TrialMode_ExitTitle"),
            Content = _localization.GetString("TrialMode_ExitDescription"),
            PrimaryButtonText = _localization.GetString("TrialMode_Export"),
            SecondaryButtonText = _localization.GetString("TrialMode_Apply"),
            CloseButtonText = _localization.GetString("TrialMode_Discard"),
            DefaultButton = ContentDialogButton.Close
        });
        return await PopupHandler.ShowDialogAsync(dialog, XamlRoot!) switch
        {
            ContentDialogResult.Primary => TestModeExitAction.Export,
            ContentDialogResult.Secondary => TestModeExitAction.Apply,
            _ => TestModeExitAction.Discard
        };
    }, TestModeExitAction.KeepEditing);

    public async Task<(bool SupportClicked, bool DontShowAgain)> ShowSponsorsDialogAsync(SponsorsDialogMode mode)
    {
        return await ExecuteDialogAsync(async () =>
        {
            // GetSponsorsAsync never throws and may return null; the builder
            // handles a null document gracefully.
            var data = await _sponsorsService.GetSponsorsAsync();

            var builder = new Dialogs.SponsorsDialogBuilder(_localization, _sponsorsService);
            var dialog = builder.Build(data, mode, XamlRoot!);
            await PopupHandler.ShowDialogAsync(dialog, XamlRoot!);
            return builder.ExtractResult();
        }, (false, false));
    }

    public async Task<(ImportOption? Option, ImportOptions Options)> ShowConfigImportOptionsDialogAsync()
    {
        return await ExecuteDialogAsync(async () =>
        {
            var builder = new Dialogs.ConfigImportDialogBuilder(_localization);
            var dialog = builder.Build(XamlRoot!);
            var result = await PopupHandler.ShowDialogAsync(dialog, XamlRoot!);
            return builder.ExtractResult(result);
        }, ((ImportOption?)null, new ImportOptions()));
    }

    public async Task<ConfirmationResponse> ShowConfirmationAsync(ConfirmationRequest confirmationRequest)
    {
        return await ExecuteDialogAsync(async () =>
        {
            var contentPanel = new StackPanel { Spacing = 12 };

            if (!string.IsNullOrEmpty(confirmationRequest.Message))
            {
                contentPanel.Children.Add(new TextBlock
                {
                    Text = confirmationRequest.Message,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (confirmationRequest.Items is { Count: > 0 } items)
            {
                var itemContainerStyle = new Style(typeof(ListViewItem));
                itemContainerStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(0)));
                itemContainerStyle.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 0d));

                var listView = new ListView
                {
                    Padding = (Thickness)Microsoft.UI.Xaml.Application.Current.Resources["ScrollContentPadding"],
                    ItemsSource = items.ToList(),
                    MaxHeight = 300,
                    SelectionMode = ListViewSelectionMode.None,
                    ItemContainerStyle = itemContainerStyle
                };

                var listContainer = new Border
                {
                    Child = listView,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        (XamlRoot?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark
                            ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black),
                    BorderThickness = new Thickness(1)
                };

                contentPanel.Children.Add(listContainer);
            }

            CheckBox? checkBox = null;
            if (!string.IsNullOrEmpty(confirmationRequest.CheckboxText))
            {
                var checkboxText = confirmationRequest.CheckboxText;
                checkBox = new CheckBox
                {
                    Content = checkboxText,
                    IsChecked = confirmationRequest.CheckboxInitiallyChecked
                };

                // Announce checkbox state changes to Narrator
                checkBox.Checked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(
                    checkBox,
                    $"{checkboxText}: {_localization.GetString("Accessibility_Checked") ?? "Checked"}",
                    "CheckboxStateChange");
                checkBox.Unchecked += (_, _) => DialogAccessibilityHelper.AnnounceToNarrator(
                    checkBox,
                    $"{checkboxText}: {_localization.GetString("Accessibility_Unchecked") ?? "Unchecked"}",
                    "CheckboxStateChange");

                contentPanel.Children.Add(checkBox);
            }

            var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
            {
                Title = string.IsNullOrEmpty(confirmationRequest.Title)
                    ? _localization.GetString("Dialog_Confirmation")
                    : confirmationRequest.Title,
                Content = contentPanel,
                PrimaryButtonText = confirmationRequest.ConfirmButtonText,
                CloseButtonText = confirmationRequest.CancelButtonText,
                DefaultButton = ContentDialogButton.Primary
            });

            var result = await PopupHandler.ShowDialogAsync(dialog, XamlRoot!);
            return new ConfirmationResponse
            {
                Confirmed = result == ContentDialogResult.Primary,
                CheckboxChecked = checkBox?.IsChecked == true
            };
        }, new ConfirmationResponse { Confirmed = false, CheckboxChecked = false });
    }

    public async Task ShowTaskOutputDialogAsync(string title, IReadOnlyList<string> logMessages)
    {
        await ExecuteDialogAsync(async () =>
        {
            var builder = new Dialogs.TaskOutputDialogBuilder(_localization, _taskProgressService);
            var dialog = builder.Build(XamlRoot!, title, logMessages);
            await PopupHandler.ShowDialogAsync(dialog, XamlRoot!,
                () => builder.StartLiveUpdates(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()),
                builder.StopLiveUpdates);
        });
    }

    public async Task ShowCustomContentDialogAsync(string title, object content, string closeButtonText = "Close")
    {
        await ExecuteDialogAsync(async () =>
        {
            var dialog = PopupHandler.CreateDialog(new PopupDialogOptions
            {
                Title = title,
                Content = content,
                CloseButtonText = closeButtonText,
                DefaultButton = ContentDialogButton.Close
            });
            if (content is ICustomDialogContentState) dialog.Resources["ContentDialogMaxWidth"] = 1000d;
            dialog.Closing += (_, args) =>
            {
                if (content is ICustomDialogContentState { CanClose: false }) args.Cancel = true;
            };
            await PopupHandler.ShowDialogAsync(dialog, XamlRoot!);
        });
    }
}
