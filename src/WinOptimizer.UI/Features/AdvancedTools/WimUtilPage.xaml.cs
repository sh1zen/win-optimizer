using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.AdvancedTools.Models;
using WinOptimizer.UI.Features.AdvancedTools.ViewModels;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.AdvancedTools;

/// <summary>
/// WIM Utility page for creating custom Windows installation images.
/// </summary>
public sealed partial class WimUtilPage : Page
{
    private readonly ILocalizationService? _localizationService;
    private readonly List<WizardActionCard> _subscribedCards = new();

    public WimUtilViewModel ViewModel { get; }

    public WimUtilPage()
    {
        this.InitializeComponent();
        ViewModel = App.Services.GetRequiredService<WimUtilViewModel>();
        _localizationService = App.Services.GetService<ILocalizationService>();
        UpdateWinOptimizerXmlCardIcon();

        // PageUp/PageDown fast-scroll + Home/End jump (issue #581).
        PageScrollHelper.Attach(this, PageScrollView);
    }

    /// <summary>Applies the application mark to the configuration export card.</summary>
    private void UpdateWinOptimizerXmlCardIcon()
    {
        WinOptimizerXmlCardIcon.Source = new BitmapImage(new Uri(AppBranding.ImageUri));
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Initialize the ViewModel
        await ViewModel.OnNavigatedToAsync();

        // Live-region announcements when wizard action cards transition state (issue #647).
        SubscribeToActionCards();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        UnsubscribeFromActionCards();
    }

    private void SubscribeToActionCards()
    {
        UnsubscribeFromActionCards();

        // Every card the user can see a spinner/checkmark/X on.
        WizardActionCard?[] cards =
        {
            ViewModel.Step1.SelectIsoCard, ViewModel.Step1.SelectDirectoryCard, ViewModel.ImageFormat.ConvertImageCard,
            ViewModel.Step2.GenerateWinOptimizerXmlCard, ViewModel.Step2.SelectXmlCard,
            ViewModel.Step3.ExtractSystemDriversCard, ViewModel.Step3.SelectCustomDriversCard,
            ViewModel.Step4.DownloadOscdimgCard, ViewModel.Step4.SelectOutputCard,
        };

        foreach (var card in cards)
        {
            if (card == null) continue;
            card.PropertyChanged += OnCardPropertyChanged;
            _subscribedCards.Add(card);
        }
    }

    private void UnsubscribeFromActionCards()
    {
        foreach (var card in _subscribedCards)
        {
            card.PropertyChanged -= OnCardPropertyChanged;
        }
        _subscribedCards.Clear();
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WizardActionCard card) return;

        // Only announce state transitions into a "noteworthy" state — the source-generated
        // PropertyChanged only fires on actual value changes, so this won't repeat.
        string? message = e.PropertyName switch
        {
            nameof(WizardActionCard.IsProcessing) when card.IsProcessing
                => $"{card.Title}: {_localizationService?.GetString("Accessibility_InProgress") ?? "in progress"}",
            nameof(WizardActionCard.IsComplete) when card.IsComplete
                => $"{card.Title}: {_localizationService?.GetString("Accessibility_Complete") ?? "complete"}",
            nameof(WizardActionCard.HasFailed) when card.HasFailed
                => $"{card.Title}: {_localizationService?.GetString("Accessibility_Failed") ?? "failed"}",
            _ => null,
        };
        if (message != null) Announce(message);
    }

    private void Announce(string message)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(this)
                   ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);

        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            message,
            "WimUtil");
    }

    private void Windows10Download_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Step1.OpenWindows10DownloadCommand.Execute(null);
    }

    private void Windows11Download_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Step1.OpenWindows11DownloadCommand.Execute(null);
    }

    private void SchneegansLink_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Step2.OpenSchneegansXmlGeneratorCommand.Execute(null);
    }
}
