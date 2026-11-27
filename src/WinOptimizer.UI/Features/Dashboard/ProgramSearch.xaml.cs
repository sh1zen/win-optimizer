using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Dashboard;

/// <summary>Runs the title-bar query and keeps the page-wide results in sync with its text.</summary>
public sealed partial class ProgramSearch : UserControl
{
    private readonly ILocalizationService _localization = App.Services.GetRequiredService<ILocalizationService>();
    private readonly IApplicationModeService _mode = App.Services.GetRequiredService<IApplicationModeService>();
    private readonly ProgramSearchService _search = App.Services.GetRequiredService<ProgramSearchService>();
    private readonly ILogService _log = App.Services.GetRequiredService<ILogService>();
    private ProgramSearchResults? _resultsView;
    private CancellationTokenSource? _searchRequest;
    private (string Query, Task<IReadOnlyList<ProgramSearchResult>?> Results)? _latestSearch;

    internal event Action<ProgramSearchEntry>? DestinationSelected;

    public ProgramSearch()
    {
        InitializeComponent();
        Loaded += Search_Loaded;
        Unloaded += Search_Unloaded;
    }

    internal void AttachResults(ProgramSearchResults resultsView)
    {
        _resultsView = resultsView;
        resultsView.ResultInvoked += OpenResult;
        resultsView.DismissRequested += () => DismissResults(restoreFocus: true);
    }

    private void Search_Loaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged += LanguageChanged;
        _mode.ModeChanged += ModeChanged;
        LocalizeSearch();
    }

    private void Search_Unloaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged -= LanguageChanged;
        _mode.ModeChanged -= ModeChanged;
        DismissResults();
    }

    /// <summary>Cancels the current query and reveals the page beneath the results.</summary>
    internal bool DismissResults(bool restoreFocus = false)
    {
        if (_latestSearch is null) return false;
        _latestSearch = null;
        _searchRequest?.Cancel();
        _resultsView?.Hide();
        ProgramSearchBox.Text = "";
        if (restoreFocus) ProgramSearchBox.Focus(FocusState.Programmatic);
        return true;
    }

    private void LanguageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (!IsLoaded) return;
        LocalizeSearch();
        if (_latestSearch is not null) await UpdateResultsAsync(refresh: true);
    });

    private void ModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (IsLoaded && _latestSearch is not null) await UpdateResultsAsync(refresh: true);
    });

    private void LocalizeSearch()
    {
        ProgramSearchBox.PlaceholderText = _localization.GetString("Dashboard_SearchPlaceholder");
        AutomationProperties.SetName(ProgramSearchBox, ProgramSearchBox.PlaceholderText);
        _resultsView?.SetLabels(ProgramSearchBox.PlaceholderText, _localization.GetString("Button_Close"));
    }

    private async void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        await UpdateResultsAsync();
    }

    private async void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if ((await UpdateResultsAsync())?.FirstOrDefault() is { } result) OpenResult(result);
    }

    private void Search_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && _resultsView?.FocusFirstResult() == true) e.Handled = true;
    }

    private void OpenResult(ProgramSearchResult result)
    {
        if (_latestSearch?.Query != ProgramSearchBox.Text.Trim() || !_search.CanOpen(result.Entry)) return;
        DismissResults();
        DestinationSelected?.Invoke(result.Entry);
    }

    /// <summary>Shares the same lookup between typing and submitting a query.</summary>
    private Task<IReadOnlyList<ProgramSearchResult>?> UpdateResultsAsync(bool refresh = false)
    {
        var query = ProgramSearchBox.Text.Trim();
        if (!IsLoaded || _resultsView is null) return Task.FromResult<IReadOnlyList<ProgramSearchResult>?>(null);
        if (query.Length == 0)
        {
            DismissResults();
            return Task.FromResult<IReadOnlyList<ProgramSearchResult>?>(null);
        }
        if (!refresh && _latestSearch is { } latest && latest.Query == query &&
            latest.Results is not { IsCompletedSuccessfully: true, Result: null }) return latest.Results;

        _searchRequest?.Cancel();
        _resultsView.Present(null, searching: true);
        var pending = FindAndPresentAsync(_resultsView, query);
        _latestSearch = (query, pending);
        return pending;
    }

    /// <summary>Publishes only the active query after its asynchronous lookup has completed.</summary>
    private async Task<IReadOnlyList<ProgramSearchResult>?> FindAndPresentAsync(ProgramSearchResults resultsView, string query)
    {
        using var request = new CancellationTokenSource();
        _searchRequest = request;
        IReadOnlyList<ProgramSearchResult>? results;
        try
        {
            results = await _search.FindAsync(query, request.Token);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return null; }
        catch (Exception error)
        {
            _log.LogWarning($"Could not search program components: {error.Message}");
            results = null;
        }
        finally
        {
            if (_searchRequest == request) _searchRequest = null;
        }

        if (request.IsCancellationRequested || !IsLoaded || ProgramSearchBox.Text.Trim() != query) return null;
        var status = results is { Count: > 0 } ? "" :
            _localization.GetString(results is null ? "Dashboard_SearchFailed" : "Dashboard_SearchNoResults");
        resultsView.Present(results, status);
        return results;
    }
}
