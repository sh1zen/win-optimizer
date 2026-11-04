using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class SystemBackupServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly FakeBackupBackend _backend = new();
    private readonly SystemBackupService _sut;

    public SystemBackupServiceTests()
    {
        // Default localization returns the key itself for any GetString call
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _sut = new SystemBackupService(
            _mockLog.Object,
            _mockLocalization.Object,
            _backend, TimeProvider.System);
    }

    // ── CreateRestorePointAsync ──

    [Fact]
    public async Task CreateRestorePointAsync_FailureResult_ContainsErrorMessage()
    {
        var result = await _sut.CreateRestorePointAsync();
        Assert.False(result.Success);
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateRestorePointAsync_LogsStartOfProcess()
    {
        await _sut.CreateRestorePointAsync();

        _mockLog.Verify(
            l => l.Log(
                Core.Features.Common.Enums.LogLevel.Info,
                It.Is<string>(s => s.Contains("Creating restore point")),
                It.IsAny<Exception?>()),
            Times.Once);
    }

    // ── BackupResult model coverage ──

    [Theory]
    [InlineData("before")]
    [InlineData("enable")]
    [InlineData("storage")]
    [InlineData("create")]
    [InlineData("verification")]
    public async Task CancellationStopsAtTheNextBoundaryWithoutStartingLaterNativeWork(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ControlledClock();
        var backend = new FakeBackupBackend { Enabled = false, CreateResult = (true, 0) };
        backend.OnCall = stage => { if (stage == phase) cancellation.Cancel(); };
        var service = new SystemBackupService(_mockLog.Object, _mockLocalization.Object, backend, clock);
        if (phase == "before") cancellation.Cancel();
        var operation = service.CreateRestorePointAsync(cancellationToken: cancellation.Token);
        if (phase == "verification") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        if (phase == "before") Assert.Empty(backend.Calls);
        else if (phase == "enable") Assert.Equal(new[] { "status", "enable" }, backend.Calls);
        else if (phase == "storage") Assert.Equal(new[] { "status", "enable", "storage" }, backend.Calls);
        else Assert.Equal(new[] { "status", "enable", "storage", "create" }, backend.Calls);
    }

    [Fact]
    public async Task SuccessfulCreationRequiresVerificationAndPreservesTheRequestedNameAndToken()
    {
        var clock = new ControlledClock();
        var date = new DateTime(2026, 10, 1);
        var backend = new FakeBackupBackend { CreateResult = (true, 0), Found = date };
        var service = new SystemBackupService(_mockLog.Object, _mockLocalization.Object, backend, clock);
        using var cancellation = new CancellationTokenSource();
        var operation = service.CreateRestorePointAsync("Example backup", cancellationToken: cancellation.Token);
        Assert.False(operation.IsCompleted);
        clock.Advance();
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.True(result.RestorePointCreated);
        Assert.Equal(date, result.RestorePointDate);
        Assert.Equal("Example backup", backend.Description);
        Assert.Equal(cancellation.Token, backend.Token);
        Assert.Equal(new[] { "status", "storage", "create", "find" }, backend.Calls);
    }

    /// <summary>Records the real orchestration's native boundary calls without touching restore points.</summary>
    private sealed class FakeBackupBackend : ISystemBackupBackend
    {
        public bool Enabled { get; init; } = true;
        public (bool, int) CreateResult { get; init; } = (false, 5);
        public DateTime? Found { get; init; }
        public List<string> Calls { get; } = [];
        public Action<string>? OnCall { get; set; }
        public string? Description { get; private set; }
        public CancellationToken Token { get; private set; }
        private void Record(string stage, CancellationToken token = default) { Calls.Add(stage); Token = token; OnCall?.Invoke(stage); }
        public bool IsEnabled() { Record("status"); return Enabled; }
        public Task<bool> EnableAsync(CancellationToken token) { Record("enable", token); return Task.FromResult(true); }
        public Task EnsureStorageAsync(CancellationToken token) { Record("storage", token); return Task.CompletedTask; }
        public Task<(bool Success, int StatusCode)> CreateAsync(string description, CancellationToken token)
        { Description = description; Record("create", token); return Task.FromResult(CreateResult); }
        public Task<DateTime?> FindAsync(string description, CancellationToken token)
        { Assert.Equal(Description, description); Record("find", token); return Task.FromResult(Found); }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private readonly List<DelayTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new DelayTimer(callback, state); _timers.Add(timer); return timer; }
        public void Advance() { foreach (var timer in _timers.ToArray()) timer.Fire(); }
        private sealed class DelayTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _active = true;
            public bool Change(TimeSpan dueTime, TimeSpan period) => _active;
            public void Fire() { if (!_active) return; _active = false; callback(state); }
            public void Dispose() => _active = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public void BackupResult_CreateSuccess_SetsCorrectProperties()
    {
        var date = new DateTime(2025, 1, 15);
        var result = BackupResult.CreateSuccess(
            restorePointDate: date,
            restorePointCreated: true);

        result.Success.Should().BeTrue();
        result.RestorePointDate.Should().Be(date);
        result.RestorePointCreated.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void BackupResult_CreateFailure_SetsCorrectProperties()
    {
        var result = BackupResult.CreateFailure("Something went wrong");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Something went wrong");
        result.RestorePointCreated.Should().BeFalse();
    }
}
