using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Infrastructure.Features.Drivers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Drivers;

public sealed class DriverRemovalBackendTests
{
    private readonly Mock<IProcessExecutor> _process = new(MockBehavior.Strict);

    [Theory]
    [InlineData(DriverRemovalKind.Package, "oem12.inf", "/delete-driver oem12.inf")]
    [InlineData(DriverRemovalKind.Device, "USB\\VID_1234&PID_5678\\1", "/remove-device \"USB\\VID_1234&PID_5678\\1\"")]
    public async Task ExactIdentity_UsesSystemPnpUtil_WithoutForceOrReboot(DriverRemovalKind kind, string id, string args)
    {
        _process.Setup(process => process.ExecuteAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), args, CancellationToken.None))
            .ReturnsAsync(new ProcessExecutionResult { ExitCode = 0 });
        var result = await new WindowsDriverRemovalBackend(_process.Object).RemoveAsync(new(kind, id), default);
        Assert.True(result.Success);
        _process.VerifyAll();
    }

    [Theory]
    [InlineData(DriverRemovalKind.Package, "../oem1.inf")]
    [InlineData(DriverRemovalKind.Package, "oem1.inf /force")]
    [InlineData(DriverRemovalKind.Device, "USB\\*")]
    [InlineData(DriverRemovalKind.Device, "USB\" /subtree")]
    [InlineData(DriverRemovalKind.Device, "USB\nTEST")]
    public async Task InvalidOrBroadIdentity_IsRejectedBeforeLaunching(DriverRemovalKind kind, string id)
    {
        await Assert.ThrowsAsync<DriverScanException>(() => new WindowsDriverRemovalBackend(_process.Object).RemoveAsync(new(kind, id), default));
        _process.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(3010, true, true)] [InlineData(5, false, false)]
    public async Task WindowsResult_PreservesRestartAndFailure(int exitCode, bool success, bool restart)
    {
        _process.Setup(process => process.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(new ProcessExecutionResult { ExitCode = exitCode, StandardError = "Windows message" });
        var result = await new WindowsDriverRemovalBackend(_process.Object).RemoveAsync(new(DriverRemovalKind.Package, "oem1.inf"), default);
        Assert.Equal(success, result.Success); Assert.Equal(restart, result.RestartRequired);
        if (!success) Assert.Contains("Windows message", result.Error);
    }

    [Fact]
    public async Task Cancellation_BeforeStart_DoesNotLaunchWindows()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsDriverRemovalBackend(_process.Object)
            .RemoveAsync(new(DriverRemovalKind.Package, "oem1.inf"), cancellation.Token));
        _process.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancellationDuringNativeWork_WaitsForItsActualOutcome()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<ProcessExecutionResult>();
        _process.Setup(process => process.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None))
            .Callback(() => cancellation.Cancel()).Returns(completion.Task);
        var operation = new WindowsDriverRemovalBackend(_process.Object).RemoveAsync(new(DriverRemovalKind.Package, "oem1.inf"), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(operation.IsCompleted);
        completion.SetResult(new ProcessExecutionResult { ExitCode = 0 });
        Assert.True((await operation).Success);
    }
}
