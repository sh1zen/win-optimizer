using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Security;
using Xunit;

namespace WinOptimizer.Core.Tests.Security;

/// <summary>Exercises real report authority with deterministic fake Windows boundaries.</summary>
public sealed class SecurityScanServiceTests
{
    private readonly Mock<ISecurityBackend> _backend = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly SecurityScanService _service;
    public SecurityScanServiceTests()
    {
        _service = new(_backend.Object, _mode.Object);
        _backend.Setup(backend => backend.ReadAsync(It.IsAny<IReadOnlyList<SecurityCheckId>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SecurityCheckId> ids, CancellationToken _) => ids.Select(id => new SecurityObservation(id,
                id == SecurityCheckId.Spyware ? SecurityCheckState.Unknown : SecurityCheckState.Attention, "enabled", "original", id != SecurityCheckId.Spyware)).ToArray());
        _backend.Setup(backend => backend.FixAsync(It.IsAny<SecurityObservation>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    [Theory]
    [InlineData(false, 5)] [InlineData(true, 10)]
    public async Task ScanIncludesExpectedChecksAndAllFiveAreas(bool deep, int count)
    {
        var report = await _service.ScanAsync(deep, default);
        Assert.Equal(count, report.Items.Count); Assert.Equal(deep, report.Deep);
        Assert.Equal(5, SecurityCheck.All.Where(check => report.Items.Any(item => item.Id == check.Id)).Select(check => check.Area).Distinct().Count());
        Assert.Equal(deep, report.Items.Any(item => item.Id == SecurityCheckId.Firefox));
    }

    [Fact]
    public async Task FailedScanInvalidatesPreviousAuthority()
    {
        var old = await _service.ScanAsync(true, default);
        _backend.Setup(backend => backend.ReadAsync(It.IsAny<IReadOnlyList<SecurityCheckId>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("query failed"));
        await Assert.ThrowsAsync<IOException>(() => _service.ScanAsync(false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.FixAsync(old.Id, [SecurityCheckId.Firewall], default));
        _backend.Verify(backend => backend.FixAsync(It.IsAny<SecurityObservation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IncompleteScanCannotPublishAReport()
    {
        _backend.Setup(backend => backend.ReadAsync(It.IsAny<IReadOnlyList<SecurityCheckId>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ScanAsync(true, default));
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)] [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ModeBlocksWritesButAllowsScanning(WinOptimizerMode mode)
    {
        _mode.SetupGet(modeService => modeService.CurrentMode).Returns(mode);
        var report = await _service.ScanAsync(true, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.FixAsync(report.Id, [SecurityCheckId.Firewall], default));
        _backend.Verify(backend => backend.FixAsync(It.IsAny<SecurityObservation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(SecurityCheckId.Spyware)] [InlineData((SecurityCheckId)500)]
    public async Task InvalidSelectionRejectsWholeBatch(SecurityCheckId invalid)
    {
        var report = await _service.ScanAsync(true, default);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.FixAsync(report.Id, [SecurityCheckId.Firewall, invalid], default));
        _backend.Verify(backend => backend.FixAsync(It.IsAny<SecurityObservation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailureIsReportedAndCannotReplayConsumedEvidence()
    {
        var report = await _service.ScanAsync(true, default);
        _backend.Setup(backend => backend.FixAsync(It.Is<SecurityObservation>(item => item.Id == SecurityCheckId.Firewall), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("managed"));
        var result = await _service.FixAsync(report.Id, [SecurityCheckId.Firewall, SecurityCheckId.Isatap], default);
        Assert.False(result.Outcomes[0].Fixed); Assert.Equal("managed", result.Outcomes[0].Error); Assert.True(result.Outcomes[1].Fixed);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.FixAsync(report.Id, [SecurityCheckId.Firewall], default));
        var original = report.Items.Single(item => item.Id == SecurityCheckId.Isatap);
        _backend.Verify(backend => backend.FixAsync(It.Is<SecurityObservation>(item => ReferenceEquals(item, original)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellationRetainsCompletedChangesAndStopsBetweenWrites()
    {
        using var source = new CancellationTokenSource();
        var report = await _service.ScanAsync(true, default);
        _backend.Setup(backend => backend.FixAsync(It.IsAny<SecurityObservation>(), It.IsAny<CancellationToken>()))
            .Callback(() => source.Cancel()).Returns(Task.CompletedTask);
        var result = await _service.FixAsync(report.Id, [SecurityCheckId.Firewall, SecurityCheckId.Isatap], source.Token);
        Assert.True(result.Cancelled); Assert.True(Assert.Single(result.Outcomes).Fixed);
        _backend.Verify(backend => backend.FixAsync(It.IsAny<SecurityObservation>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
