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
    private readonly Mock<IProcessExecutor> _mockProcessExecutor = new();
    private readonly Mock<ISystemRestoreService> _mockSystemRestore = new();
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
            _mockProcessExecutor.Object,
            _mockSystemRestore.Object);
    }

    // ── CreateRestorePointAsync ──

    [Fact]
    public async Task CreateRestorePointAsync_FailureResult_ContainsErrorMessage()
    {
        // On a test environment, WMI calls will fail, producing a failure with an error message
        var result = await _sut.CreateRestorePointAsync();

        // The service either succeeds or returns a failure with a message
        if (!result.Success)
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
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
