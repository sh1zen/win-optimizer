using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class ConnectionViewModelTests
{
    private readonly Mock<IConnectionService> _service = new();
    private readonly Mock<IConnectionTrafficService> _traffic = new();
    private readonly Mock<ICompatibleSettingsRegistry> _settings = new();
    private readonly Mock<ISettingApplicationService> _application = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationModeService> _mode = new();

    public ConnectionViewModelTests()
    {
        _localization.Setup(value => value.GetString(It.IsAny<string>())).Returns((string key) => key);
        _localization.SetupGet(value => value.CurrentLanguage).Returns("en");
        _settings.SetupGet(value => value.IsInitialized).Returns(true);
        _settings.Setup(value => value.GetById("gaming-dns-server")).Returns(new SettingDefinition
        {
            Id = "gaming-dns-server", Name = "DNS", Description = "DNS", ComboBox = new()
            { Options = [new() { DisplayName = "Automatic", Script = ScriptOption.Disabled }, new() { DisplayName = "Cloudflare", Script = ScriptOption.Enabled }] }
        });
        _service.Setup(value => value.ReadAsync()).ReturnsAsync(new ConnectionSnapshot(DateTimeOffset.Now,
            [new("a", "Ethernet", "", "Ethernet", "Up", ["192.0.2.1"], new(100, 200, 1, 4), "")], [], ""));
        _traffic.Setup(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionUsageHistory(
            DateTimeOffset.UtcNow, Enumerable.Range(0, 30).Select(index =>
                new ConnectionDailyUsage(new DateOnly(2026, 10, 20).AddDays(index - 29), 1024, 2048, 1, 1)).ToArray(), ""));
    }

    private ConnectionViewModel Create() => new(_service.Object, _traffic.Object, _settings.Object, _application.Object,
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
        _service.VerifyNoOtherCalls();
        _traffic.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LoadReadsUsageOnceAndLeavesDnsSelectionUncommitted()
    {
        using var vm = Create();
        await vm.LoadAsync(); await vm.LoadAsync();
        Assert.Equal(-1, vm.SelectedDnsIndex); Assert.False(vm.ApplyDnsCommand.CanExecute(null));
        Assert.Equal(30, vm.HistoryDays.Count); Assert.Equal("90.00 KiB", vm.HistoryTotal);
        Assert.Equal(3072m, vm.HistoryMaximumBytes); Assert.Equal("3.00 KiB", vm.HistoryScale);
        _traffic.Verify(value => value.ReadHistoryAsync(It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(value => value.ReadAsync(), Times.Once); _application.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DnsApplyUsesTheExistingSettingIndexAndRefreshesSnapshot(int index)
    {
        _application.Setup(value => value.ApplySettingAsync(It.IsAny<ApplySettingRequest>())).ReturnsAsync(OperationResult.Succeeded());
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedDnsIndex = index;
        await vm.ApplyDnsCommand.ExecuteAsync(null);
        _application.Verify(value => value.ApplySettingAsync(It.Is<ApplySettingRequest>(request =>
            request.SettingId == "gaming-dns-server" && request.Enable && Equals(request.Value, index))), Times.Once);
        _service.Verify(value => value.ReadAsync(), Times.Exactly(2)); Assert.False(vm.IsError);
    }

    [Fact]
    public async Task DnsFailureIsVisibleAndDoesNotPublishSuccess()
    {
        _application.Setup(value => value.ApplySettingAsync(It.IsAny<ApplySettingRequest>())).ReturnsAsync(OperationResult.Failed("denied"));
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedDnsIndex = 0;
        await vm.ApplyDnsCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Equal("denied", vm.Status);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ModeGuardsAllowReadsAndPreventDnsAndVpnChanges(WinOptimizerMode mode)
    {
        _mode.SetupGet(value => value.CurrentMode).Returns(mode);
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedDnsIndex = 0;
        Assert.True(vm.HasSnapshot); Assert.False(vm.ApplyDnsCommand.CanExecute(null));
        Assert.False(vm.ManageVpnCommand.CanExecute(null)); Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task LiveRatesUseMeasuredIntervalsAndMissingDataRemainsUnavailable()
    {
        _localization.Setup(value => value.GetString("Connection_Coverage")).Returns("{0}/{1}");
        var at = DateTimeOffset.UtcNow;
        _traffic.Setup(value => value.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionLiveTraffic(at,
            [new(at.AddSeconds(-2), at, new(200, 400, 1, 2), 1, 2)]));
        using var vm = Create(); await vm.SampleTrafficAsync(CancellationToken.None);
        Assert.Equal("200 B/s", vm.LiveStats[0].Value); Assert.Equal("100 B/s", vm.LiveStats[1].Value);
        Assert.Equal("400 B", vm.LiveStats[2].Value); Assert.Equal("200 B", vm.LiveStats[3].Value);
        Assert.Equal("1/2", vm.LiveCoverage); Assert.False(vm.IsBusy);
        _traffic.Setup(value => value.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ConnectionLiveTraffic(at,
            [new(at, at, null, 0, 1)]));
        await vm.SampleTrafficAsync(CancellationToken.None);
        Assert.Equal("Connection_Waiting", vm.LiveStats[0].Value);
        Assert.Equal("Utilities_Unknown", vm.LiveStats[2].Value);
        _service.VerifyNoOtherCalls(); _application.VerifyNoOtherCalls();
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
