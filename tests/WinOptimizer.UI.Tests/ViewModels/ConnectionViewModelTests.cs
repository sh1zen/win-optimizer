using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class ConnectionViewModelTests
{
    private readonly Mock<IConnectionTrafficService> _traffic = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationModeService> _mode = new();

    public ConnectionViewModelTests()
    {
        _localization.Setup(value => value.GetString(It.IsAny<string>())).Returns((string key) => key);
        _localization.SetupGet(value => value.CurrentLanguage).Returns("en");
        _traffic.Setup(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionUsageHistory(
            DateTimeOffset.UtcNow, Enumerable.Range(0, 30).Select(index =>
                new ConnectionDailyUsage(new DateOnly(2026, 10, 20).AddDays(index - 29), 1024, 2048, 1, 1)).ToArray(), ""));
    }

    private ConnectionViewModel Create() => new(_traffic.Object,
        _localization.Object, _mode.Object, new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());

    [Fact]
    public void SectionTitleFollowsManagementLocalization()
    {
        _localization.Setup(value => value.GetString("Nav_Management")).Returns("Management");
        using var vm = Create();
        Assert.Equal("Management", vm.SectionTitle);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        _localization.Setup(value => value.GetString("Nav_Management")).Returns("Gestione");
        _localization.Raise(value => value.LanguageChanged += null, EventArgs.Empty);
        Assert.Equal("Gestione", vm.SectionTitle);
        Assert.Contains(string.Empty, notifications);
        _traffic.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadReusesHistoryAfterTheFirstRead()
    {
        using var vm = Create();
        await vm.LoadAsync(); await vm.LoadAsync();
        Assert.Equal(30, vm.HistoryDays.Count); Assert.Equal("90.00 KiB", vm.HistoryTotal);
        Assert.Equal(3072m, vm.HistoryMaximumBytes); Assert.Equal("3.00 KiB", vm.HistoryScale);
        _traffic.Verify(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadingHistoryDoesNotBlockLiveSamplesAndSharesThePendingRead()
    {
        var history = new TaskCompletionSource<ConnectionUsageHistory>();
        var at = DateTimeOffset.UtcNow;
        _traffic.Setup(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>())).Returns(history.Task);
        _traffic.Setup(value => value.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionLiveTraffic(at, []));
        using var vm = Create();

        var load = vm.LoadAsync();
        Assert.Same(load, vm.LoadAsync());
        Assert.True(vm.IsHistoryLoading);
        await vm.SampleTrafficAsync(CancellationToken.None);
        Assert.Equal(at, vm.LiveCapturedAt);
        Assert.Empty(vm.HistoryDays);

        history.SetResult(new(at, [new(new(2026, 10, 20), 100, 200, 1, 1)], ""));
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsHistoryLoading);
        Assert.Equal("300 B", vm.HistoryTotal);
        _traffic.Verify(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HistoryReadErrorsAreVisibleAndCanBeRetriedOnEntry()
    {
        _traffic.SetupSequence(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UtilityOperationException("History denied"))
            .ReturnsAsync(new ConnectionUsageHistory(DateTimeOffset.UtcNow, [], ""));
        using var vm = Create();
        await vm.LoadAsync();
        Assert.Equal("History denied", vm.HistoryDiagnostic);
        Assert.False(vm.IsHistoryLoading);
        await vm.LoadAsync();
        Assert.False(vm.HasHistoryDiagnostic);
        _traffic.Verify(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task LiveRatesUseMeasuredIntervalsAndMissingDataRemainsUnavailable()
    {
        _localization.Setup(value => value.GetString("Connection_Coverage")).Returns("{0}/{1}");
        var at = DateTimeOffset.UtcNow;
        _traffic.Setup(value => value.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionLiveTraffic(at,
            [new(at.AddSeconds(-2), at, new(200, 400, 1, 2), 1, 2)]));
        using var vm = Create(); await vm.SampleTrafficAsync(CancellationToken.None);
        Assert.Equal(200m, vm.LiveMaximumRate); Assert.Equal("200 B/s", vm.LiveScale);
        Assert.Equal("200 B/s", vm.LiveStats[0].Value); Assert.Equal("100 B/s", vm.LiveStats[1].Value);
        Assert.Equal("400 B", vm.LiveStats[2].Value); Assert.Equal("200 B", vm.LiveStats[3].Value);
        Assert.Equal("1/2", vm.LiveCoverage); Assert.False(vm.IsBusy);
        _traffic.Setup(value => value.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionLiveTraffic(at,
            [new(at, at, null, 0, 1)]));
        await vm.SampleTrafficAsync(CancellationToken.None);
        Assert.Equal("Connection_Waiting", vm.LiveStats[0].Value);
        Assert.Equal("Utilities_Unknown", vm.LiveStats[2].Value);
    }

    [Fact]
    public async Task HistoryFailureKeepsMissingDaysDistinctFromZeroTraffic()
    {
        _traffic.Setup(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionUsageHistory(
            DateTimeOffset.UtcNow, [new(new(2026, 10, 20), null, null, 0, 2)], "History denied"));
        using var vm = Create(); await vm.LoadAsync();
        Assert.Equal("Utilities_Unknown", vm.HistoryTotal); Assert.True(vm.HasHistoryDiagnostic);
        Assert.Equal("History denied", vm.HistoryDiagnostic); Assert.Null(vm.HistoryDays[0].BytesSent);
        Assert.Equal(0m, vm.HistoryMaximumBytes); Assert.Equal("0 B", vm.HistoryScale);
    }

    [Fact]
    public async Task SamplingErrorsAreVisibleAndCancellationDoesNotPublishStaleReadings()
    {
        _traffic.Setup(value => value.SampleAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new UtilityOperationException("Counters denied"));
        using var vm = Create(); await vm.SampleTrafficAsync(CancellationToken.None);
        Assert.Equal("Counters denied", vm.LiveDiagnostic); Assert.True(vm.HasLiveDiagnostic);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SampleTrafficAsync(cancellation.Token));
        Assert.Equal("Counters denied", vm.LiveDiagnostic);
        _traffic.Setup(value => value.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionLiveTraffic(DateTimeOffset.UtcNow, []));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.SampleTrafficAsync(cancellation.Token));
        Assert.Empty(vm.LiveIntervals); Assert.True(vm.HasLiveDiagnostic);
    }
}
