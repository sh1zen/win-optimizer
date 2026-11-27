using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Helpers;
using WinOptimizer.UI.Helpers;

namespace WinOptimizer.UI.Features.Dashboard;

/// <summary>Owns navigation, clipboard interaction and the dashboard's visible sampling session.</summary>
public sealed partial class DashboardPage : Page
{
    private readonly ILocalizationService _localization = App.Services.GetRequiredService<ILocalizationService>();
    private readonly IApplicationModeService _mode = App.Services.GetRequiredService<IApplicationModeService>();
    private CancellationTokenSource? _visit;
    private Task _session = Task.CompletedTask;

    public DashboardViewModel ViewModel { get; } = App.Services.GetRequiredService<DashboardViewModel>();

    public DashboardPage()
    {
        InitializeComponent();
        PageScrollHelper.Attach(this, OverviewScrollViewer);
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    /// <summary>Schedules local inventory and telemetry after the page has rendered.</summary>
    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged += LanguageChanged;
        _mode.ModeChanged += ModeChanged;
        ViewModel.RefreshText();
        _visit = new CancellationTokenSource();
        _session = RunSessionAsync(_session, _visit);
    }

    private async Task RunSessionAsync(Task previous, CancellationTokenSource visit)
    {
        using (visit)
        {
            try
            {
                await previous;
                visit.Token.ThrowIfCancellationRequested();
                await StartupUiCoordinator.RunAfterFirstRenderAsync(this, () => Task.WhenAll(
                    ViewModel.LoadInventoryAsync(visit.Token), ViewModel.WatchUsageAsync(visit.Token)));
            }
            catch (OperationCanceledException) when (visit.IsCancellationRequested || !IsLoaded) { }
            catch (Exception error) { ViewModel.ReportSessionFailure(error); }
            finally
            {
                if (_visit == visit) _visit = null;
            }
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged -= LanguageChanged;
        _mode.ModeChanged -= ModeChanged;
        _visit?.Cancel();
    }

    private void LanguageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!IsLoaded) return;
        ViewModel.RefreshText();
        CopyStatus.Visibility = Visibility.Collapsed;
    });

    private void ModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsLoaded) ViewModel.RefreshActions();
    });

    private void QuickAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DashboardAction action } && ViewModel.CanOpen(action.Destination))
            Frame?.Navigate(action.Destination);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var copied = false;
        try
        {
            var content = new DataPackage();
            content.SetText(ViewModel.GetCopyText());
            Clipboard.SetContent(content);
            copied = true;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            ViewModel.ReportClipboardFailure(error);
        }
        CopyStatus.Text = ViewModel.CopyFeedback(copied);
        CopyStatus.Visibility = Visibility.Visible;
    }
}
