using System.ComponentModel;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Native;
using WinOptimizer.Infrastructure.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public sealed class RestorePointManagementTests
{
    private readonly Mock<ISystemRestorePointStore> _store = new();
    private SystemRestoreService CreateService() => new(Mock.Of<ILogService>(), _store.Object);

    [Fact]
    public async Task InventoryReturnsWindowsEvidenceWithoutReconstructingIt()
    {
        IReadOnlyList<SystemRestorePoint> points = new[] { new SystemRestorePoint(42, "Example point", DateTimeOffset.UnixEpoch) };
        _store.Setup(s => s.Read(It.IsAny<CancellationToken>())).Returns(points);
        var result = await CreateService().GetRestorePointsAsync();
        result.Should().BeSameAs(points);
    }

    [Fact]
    public async Task DeleteTargetsExactlyTheRequestedSequenceNumber()
    {
        var result = await CreateService().DeleteRestorePointAsync(42);
        result.Success.Should().BeTrue();
        _store.Verify(s => s.Delete(42), Times.Once);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NativeFailureIsReturnedWithoutReportingSuccessfulDeletion()
    {
        _store.Setup(s => s.Delete(42)).Throws(new Win32Exception(13));
        var result = await CreateService().DeleteRestorePointAsync(42);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.Exception.Should().BeOfType<Win32Exception>();
    }

    [Fact]
    public async Task CancelledOperationsDoNotEnterTheWindowsBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRestorePointsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteRestorePointAsync(42, cancellation.Token));
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvalidIdentifiersAreRejectedBeforeNativeCalls()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateService().DeleteRestorePointAsync(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsRestorePointStore().Delete(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SrClientApi.RemoveRestorePoint(0));
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public void CancelledInventoryIsRejectedBeforeWmiIsOpened()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new WindowsRestorePointStore().Read(cancellation.Token));
    }
}
