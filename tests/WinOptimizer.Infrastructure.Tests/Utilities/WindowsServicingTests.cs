using System.Text;
using System.Text.Json;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Exercises the production workflow and tool boundary without running DISM, SFC or registry queries.</summary>
public sealed class WindowsServicingTests
{
    private readonly Backend _backend;
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly List<WindowsServicingStep> _visited = [];

    public WindowsServicingTests()
    {
        _mode.Setup(service => service.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        _backend = new Backend(_visited);
    }

    private WindowsServicingService Create() => new(_backend, _mode.Object);
    private static WindowsServicingStepResult Success(WindowsServicingStep step) => new(step, true, 0, WindowsImageHealth.Healthy, false, "Native output");

    [Fact]
    public async Task QuickStatusUsesOnlyExistingHealthFlags()
    {
        var report = await Create().RunAsync(new(WindowsServicingAction.QuickCheck), null, CancellationToken.None);
        Assert.Equal(new[] { WindowsServicingStep.CheckHealth }, _visited);
        Assert.Equal(WindowsImageHealth.Healthy, report.ImageHealth);
    }

    [Fact]
    public async Task DiagnosisScansAnalyzesAndVerifiesWithoutRepairOrCleanup()
    {
        var report = await Create().RunAsync(new(WindowsServicingAction.Diagnosis), null, CancellationToken.None);
        Assert.Equal(new[] { WindowsServicingStep.ScanHealth, WindowsServicingStep.AnalyzeStore, WindowsServicingStep.VerifyFiles }, _visited);
        Assert.Equal(WindowsServicingState.ReviewFileResults, report.State);
    }

    [Fact]
    public async Task RepairRunsDismBeforeSfcAndKeepsRawFileResultsForReview()
    {
        var report = await Create().RunAsync(new(WindowsServicingAction.Repair), null, CancellationToken.None);
        Assert.Equal(new[] { WindowsServicingStep.RestoreHealth, WindowsServicingStep.RepairFiles }, _visited);
        Assert.Equal(WindowsServicingState.ReviewFileResults, report.State);
        Assert.All(report.Steps, step => Assert.Equal("Native output", step.Output));
    }

    [Theory]
    [InlineData(WindowsImageHealth.Unknown)]
    [InlineData(WindowsImageHealth.Repairable)]
    [InlineData(WindowsImageHealth.NonRepairable)]
    public async Task AnUnhealthyRestoreDoesNotStartSfc(WindowsImageHealth health)
    {
        _backend.Results[WindowsServicingStep.RestoreHealth] = Success(WindowsServicingStep.RestoreHealth) with { Health = health };
        var report = await Create().RunAsync(new(WindowsServicingAction.Repair), null, CancellationToken.None);
        Assert.Equal(WindowsServicingState.NeedsAttention, report.State); Assert.Single(report.Steps);
        Assert.Equal(health, report.ImageHealth);
    }

    [Fact]
    public async Task FailedRestoreRetainsItsCodeAndStopsBeforeSfc()
    {
        _backend.Results[WindowsServicingStep.RestoreHealth] = new(WindowsServicingStep.RestoreHealth, false, unchecked((int)0x800f081f), WindowsImageHealth.Unknown, false, "Source files missing");
        var report = await Create().RunAsync(new(WindowsServicingAction.Repair), null, CancellationToken.None);
        Assert.Equal(WindowsServicingState.Failed, report.State); Assert.Contains("missing", Assert.Single(report.Steps).Output);
        Assert.DoesNotContain(WindowsServicingStep.RepairFiles, _visited);
    }

    [Theory]
    [InlineData(WindowsServicingAction.Repair)]
    [InlineData(WindowsServicingAction.Cleanup)]
    public async Task PendingRestartBlocksMutatingPlans(WindowsServicingAction action)
    {
        _backend.RestartPending = true;
        var report = await Create().RunAsync(new(action), null, CancellationToken.None);
        Assert.Equal(WindowsServicingState.RestartRequired, report.State); Assert.Empty(report.Steps); Assert.Empty(_visited);
    }

    [Fact]
    public async Task RestartReportedByDismStopsFurtherCommands()
    {
        _backend.Results[WindowsServicingStep.RestoreHealth] = Success(WindowsServicingStep.RestoreHealth) with { RestartNeeded = true };
        var report = await Create().RunAsync(new(WindowsServicingAction.Repair), null, CancellationToken.None);
        Assert.Equal(WindowsServicingState.RestartRequired, report.State); Assert.Single(report.Steps);
    }

    [Fact]
    public async Task CleanupChecksHealthAndAnalyzesBeforeDeletingComponents()
    {
        var report = await Create().RunAsync(new(WindowsServicingAction.Cleanup), null, CancellationToken.None);
        Assert.Equal(new[] { WindowsServicingStep.CheckHealth, WindowsServicingStep.AnalyzeStore, WindowsServicingStep.Cleanup }, _visited);
        Assert.False(report.Request.ResetBase);
    }

    [Fact]
    public async Task CleanupStopsIfTheStoreIsNotKnownHealthy()
    {
        _backend.Results[WindowsServicingStep.CheckHealth] = Success(WindowsServicingStep.CheckHealth) with { Health = WindowsImageHealth.Repairable };
        var report = await Create().RunAsync(new(WindowsServicingAction.Cleanup), null, CancellationToken.None);
        Assert.Equal(WindowsServicingState.NeedsAttention, report.State); Assert.Single(report.Steps);
    }

    [Fact]
    public async Task CancellationAfterCompletedPhaseRetainsItsReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        _backend.AfterStep = _ => cancellation.Cancel();
        var report = await Create().RunAsync(new(WindowsServicingAction.Repair), null, cancellation.Token);
        Assert.Equal(WindowsServicingState.Cancelled, report.State); Assert.Single(report.Steps);
    }

    [Fact]
    public async Task CancellationAfterTheFinalPhaseStillRetainsItsReceipt()
    {
        using var cancellation = new CancellationTokenSource();
        _backend.AfterStep = _ => cancellation.Cancel();
        var report = await Create().RunAsync(new(WindowsServicingAction.AnalyzeStore), null, cancellation.Token);
        Assert.Equal(WindowsServicingState.Cancelled, report.State);
        Assert.Equal(WindowsServicingStep.AnalyzeStore, Assert.Single(report.Steps).Step);
    }

    [Theory]
    [InlineData(WindowsServicingAction.QuickCheck, WindowsServicingStep.CheckHealth)]
    [InlineData(WindowsServicingAction.Diagnosis, WindowsServicingStep.AnalyzeStore)]
    [InlineData(WindowsServicingAction.AnalyzeStore, WindowsServicingStep.AnalyzeStore)]
    [InlineData(WindowsServicingAction.Repair, WindowsServicingStep.RestoreHealth)]
    [InlineData(WindowsServicingAction.Cleanup, WindowsServicingStep.Cleanup)]
    public async Task CancellationInterruptsActivePhaseAndReleasesTheWorkflow(WindowsServicingAction action, WindowsServicingStep activeStep)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = new Mock<IDisposable>();
        _mode.Setup(service => service.BeginSystemOperation()).Returns(reservation.Object);
        _backend.RunStep = async (step, token) =>
        {
            if (step == activeStep)
            {
                Assert.Equal(cancellation.Token, token);
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Success(step);
        };
        var service = Create();
        var pending = service.RunAsync(new(action), null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var report = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WindowsServicingState.Cancelled, report.State);
        Assert.Equal(_visited.Take(_visited.Count - 1), report.Steps.Select(result => result.Step));
        Assert.Equal(activeStep, _visited[^1]);
        reservation.Verify(resource => resource.Dispose(), Times.Once);

        _backend.RunStep = null;
        var next = await service.RunAsync(new(WindowsServicingAction.QuickCheck), null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WindowsServicingState.Completed, next.State);
    }

    [Fact]
    public async Task UnrequestedCancellationIsNotReportedAsUserCancellation()
    {
        _backend.RunStep = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Create().RunAsync(new(WindowsServicingAction.QuickCheck), null, CancellationToken.None));
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task TestAndReviewModesDoNotReachTheSystemBoundary(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().RunAsync(new(WindowsServicingAction.Diagnosis), null, CancellationToken.None));
        Assert.Equal(0, _backend.Preparations);
    }

    [Fact]
    public async Task ResetBaseCannotBeAppliedToAnotherOperation()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create().RunAsync(new(WindowsServicingAction.Repair, ResetBase: true), null, CancellationToken.None));
        Assert.Empty(_visited);
    }

    [Fact]
    public async Task NativeTransportFailureRetainsCompletedDiagnosticSteps()
    {
        _backend.FailingStep = WindowsServicingStep.AnalyzeStore;
        var report = await Create().RunAsync(new(WindowsServicingAction.Diagnosis), null, CancellationToken.None);
        Assert.Equal(WindowsServicingState.Failed, report.State); Assert.Equal(2, report.Steps.Count);
        Assert.Null(report.Steps[1].ExitCode); Assert.Equal("Tool unavailable", report.Steps[1].Output);
    }

    private sealed class Backend(List<WindowsServicingStep> visited) : IWindowsServicingBackend
    {
        public bool RestartPending;
        public int Preparations;
        public WindowsServicingStep? FailingStep;
        public Action<WindowsServicingStep>? AfterStep;
        public Func<WindowsServicingStep, CancellationToken, Task<WindowsServicingStepResult>>? RunStep;
        public Dictionary<WindowsServicingStep, WindowsServicingStepResult> Results { get; } = [];
        public Task<WindowsServicingPreparation> PrepareAsync(WindowsServicingRequest request)
        { Preparations++; return Task.FromResult(new WindowsServicingPreparation(request, RestartPending)); }
        public Task<WindowsServicingStepResult> RunStepAsync(WindowsServicingStep step, WindowsServicingRequest request, CancellationToken cancellationToken)
        {
            visited.Add(step);
            if (step == FailingStep) throw new IOException("Tool unavailable");
            if (RunStep != null) return RunStep(step, cancellationToken);
            AfterStep?.Invoke(step);
            return Task.FromResult(Results.GetValueOrDefault(step) ?? Success(step));
        }
    }

    [Fact]
    public void SourceValidationRejectsMissingFilesAndInvalidIndexes()
    {
        var files = new Mock<IFileSystemService>();
        Assert.Throws<FileNotFoundException>(() => WindowsServicingBackend.NormalizeSource(new(WindowsServicingAction.Repair, @"D:\images\install.wim"), files.Object));
        files.Setup(file => file.FileExists(It.IsAny<string>())).Returns(true);
        Assert.Throws<ArgumentException>(() => WindowsServicingBackend.NormalizeSource(new(WindowsServicingAction.Repair, @"D:\images\install.wim", 0), files.Object));
        Assert.Throws<ArgumentException>(() => WindowsServicingBackend.NormalizeSource(new(WindowsServicingAction.Repair, "../install.wim"), files.Object));
        var request = WindowsServicingBackend.NormalizeSource(new(WindowsServicingAction.Repair, @"D:\images\install.wim", 6, true), files.Object);
        Assert.Equal(6, request.WimIndex); Assert.True(request.LimitAccess);
    }

    [Fact]
    public async Task BackendUsesStructuredHealthAndEncodesUserSourceAsData()
    {
        var process = new Mock<IProcessExecutor>();
        string? encoded = null;
        process.Setup(executor => executor.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, CancellationToken.None))
            .Callback<string, string, Action<string>?, Action<string>?, CancellationToken>((_, args, _, _, _) => encoded = args.Split(' ')[^1])
            .ReturnsAsync(new ProcessExecutionResult { StandardOutput = """{"Success":true,"ExitCode":0,"Health":"Repairable","RestartNeeded":false,"Output":"risultato Windows"}""" });
        var backend = new WindowsServicingBackend(process.Object, Mock.Of<IDismProcessRunner>(), Mock.Of<IFileSystemService>());
        var request = new WindowsServicingRequest(WindowsServicingAction.Repair, @"D:\images\a'; Write-Output 'bad.wim", 6, true);
        var result = await backend.RunStepAsync(WindowsServicingStep.RestoreHealth, request, CancellationToken.None);
        Assert.Equal(WindowsImageHealth.Repairable, result.Health); Assert.Equal("risultato Windows", result.Output);
        Assert.NotNull(encoded);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.DoesNotContain(request.SourcePath, script);
        var payload = script.Split("FromBase64String('")[1].Split("')")[0];
        using var data = JsonDocument.Parse(Convert.FromBase64String(payload));
        Assert.Equal(request.SourcePath, data.RootElement.GetProperty("SourcePath").GetString());
        Assert.Equal(6, data.RootElement.GetProperty("WimIndex").GetInt32());
        Assert.Contains("NoRestart=$true", script); Assert.Contains("StandardOutputEncoding = [Text.Encoding]::Unicode", script);
    }

    [Fact]
    public async Task InvalidToolJsonCannotMasqueradeAsSuccess()
    {
        var process = new Mock<IProcessExecutor>();
        process.Setup(executor => executor.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, CancellationToken.None))
            .ReturnsAsync(new ProcessExecutionResult { StandardOutput = "{}" });
        var backend = new WindowsServicingBackend(process.Object, Mock.Of<IDismProcessRunner>(), Mock.Of<IFileSystemService>());
        await Assert.ThrowsAsync<WinOptimizer.Core.Features.Utilities.UtilityOperationException>(() => backend.RunStepAsync(WindowsServicingStep.ScanHealth, new(WindowsServicingAction.Diagnosis), CancellationToken.None));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("FutureState")]
    public async Task UnrecognizedHealthNeverBecomesAHealthyVerdict(string health)
    {
        var process = new Mock<IProcessExecutor>();
        process.Setup(executor => executor.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, CancellationToken.None))
            .ReturnsAsync(new ProcessExecutionResult { StandardOutput = JsonSerializer.Serialize(new { Success = true, ExitCode = 0, Health = health, RestartNeeded = false, Output = "Native output" }) });
        var backend = new WindowsServicingBackend(process.Object, Mock.Of<IDismProcessRunner>(), Mock.Of<IFileSystemService>());
        var result = await backend.RunStepAsync(WindowsServicingStep.ScanHealth, new(WindowsServicingAction.Diagnosis), CancellationToken.None);
        Assert.Equal(WindowsImageHealth.Unknown, result.Health);
    }

    [Fact]
    public async Task AnalysisUsesTheSharedDismRunnerWithFixedOnlineArguments()
    {
        var runner = new Mock<IDismProcessRunner>();
        runner.Setup(dism => dism.RunProcessWithProgressAsync(It.IsAny<string>(), It.IsAny<string>(), null, CancellationToken.None)).ReturnsAsync((3010, "Restart required"));
        var backend = new WindowsServicingBackend(Mock.Of<IProcessExecutor>(), runner.Object, Mock.Of<IFileSystemService>());
        var result = await backend.RunStepAsync(WindowsServicingStep.AnalyzeStore, new(WindowsServicingAction.AnalyzeStore), CancellationToken.None);
        Assert.True(result.Succeeded); Assert.True(result.RestartNeeded);
        runner.Verify(dism => dism.RunProcessWithProgressAsync(It.IsAny<string>(), "/Online /English /NoRestart /Cleanup-Image /AnalyzeComponentStore", null, CancellationToken.None), Times.Once);
    }

    [Theory]
    [InlineData(WindowsServicingStep.CheckHealth)]
    [InlineData(WindowsServicingStep.ScanHealth)]
    [InlineData(WindowsServicingStep.AnalyzeStore)]
    [InlineData(WindowsServicingStep.VerifyFiles)]
    [InlineData(WindowsServicingStep.RestoreHealth)]
    [InlineData(WindowsServicingStep.RepairFiles)]
    [InlineData(WindowsServicingStep.Cleanup)]
    public async Task BackendForwardsCancellationToEveryNativeTool(WindowsServicingStep step)
    {
        using var cancellation = new CancellationTokenSource();
        var process = new Mock<IProcessExecutor>(MockBehavior.Strict);
        var runner = new Mock<IDismProcessRunner>(MockBehavior.Strict);
        if (step is WindowsServicingStep.AnalyzeStore or WindowsServicingStep.Cleanup)
            runner.Setup(dism => dism.RunProcessWithProgressAsync(It.IsAny<string>(), It.IsAny<string>(), null, cancellation.Token))
                .Callback(() => cancellation.Cancel())
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        else
            process.Setup(executor => executor.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, cancellation.Token))
                .Callback(() => cancellation.Cancel())
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var backend = new WindowsServicingBackend(process.Object, runner.Object, Mock.Of<IFileSystemService>());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            backend.RunStepAsync(step, new(WindowsServicingAction.Diagnosis), cancellation.Token));
        process.VerifyAll();
        runner.VerifyAll();
    }

    [Theory]
    [InlineData(false, "/Online /English /NoRestart /Cleanup-Image /StartComponentCleanup")]
    [InlineData(true, "/Online /English /NoRestart /Cleanup-Image /StartComponentCleanup /ResetBase")]
    public async Task CleanupRunsOnceWithResetBaseOnlyWhenExplicitlyRequested(bool resetBase, string arguments)
    {
        var runner = new Mock<IDismProcessRunner>();
        runner.Setup(dism => dism.RunProcessWithProgressAsync(It.IsAny<string>(), arguments, null, CancellationToken.None)).ReturnsAsync((0, "Cleanup completed"));
        var backend = new WindowsServicingBackend(Mock.Of<IProcessExecutor>(), runner.Object, Mock.Of<IFileSystemService>());
        var result = await backend.RunStepAsync(WindowsServicingStep.Cleanup, new(WindowsServicingAction.Cleanup, ResetBase: resetBase), CancellationToken.None);
        Assert.True(result.Succeeded);
        runner.Verify(dism => dism.RunProcessWithProgressAsync(It.IsAny<string>(), arguments, null, CancellationToken.None), Times.Once);
        runner.VerifyNoOtherCalls();
    }
}
