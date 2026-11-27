using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Helpers;

namespace WinOptimizer.UI.Features.Benchmark;

/// <summary>Attaches benchmark commands and cancellation to the visible Bench page.</summary>
public sealed partial class BenchmarkPage : Page
{
    private readonly ILocalizationService _localization = App.Services.GetRequiredService<ILocalizationService>();
    private CancellationTokenSource? _visit;
    private Task _load = Task.CompletedTask;

    public BenchmarkViewModel ViewModel { get; } = App.Services.GetRequiredService<BenchmarkViewModel>();

    public BenchmarkPage()
    {
        InitializeComponent();
        PageScrollHelper.Attach(this, BenchScrollViewer);
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged += LanguageChanged;
        ViewModel.RefreshText();
        _visit = new CancellationTokenSource();
        _load = LoadAfterAsync(_load, _visit.Token);
    }

    private async Task LoadAfterAsync(Task previous, CancellationToken token)
    {
        await previous;
        if (!token.IsCancellationRequested) await ViewModel.LoadAsync(token);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged -= LanguageChanged;
        _visit?.Cancel();
        _visit?.Dispose();
        _visit = null;
    }

    private void LanguageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsLoaded) ViewModel.RefreshText();
    });

    private async void RunAll_Click(object sender, RoutedEventArgs e)
    {
        if (_visit is { } visit) await ViewModel.RunAsync(null, visit.Token);
    }

    private async void RunTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BenchmarkRow row } && _visit is { } visit)
            await ViewModel.RunAsync(row, visit.Token);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();
}
