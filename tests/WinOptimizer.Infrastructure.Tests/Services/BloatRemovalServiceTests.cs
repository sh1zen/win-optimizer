using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Exceptions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Core.Features.SoftwareApps.Utilities;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public sealed class BloatRemovalServiceTests
{
    private static readonly string PlanPath = Path.Combine(ScriptPaths.ScriptsDirectory, "BloatRemoval.ps1");
    private const string TaskPath = @"\win-optimizer\BloatRemoval";
    private readonly Dictionary<string, string> _storage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Mock<IScheduledTaskService> _tasks = new(MockBehavior.Strict);
    private readonly Mock<IPowerShellRunner> _runner = new(MockBehavior.Strict);
    private readonly Mock<IFileSystemService> _files = new(MockBehavior.Strict);
    private readonly BloatRemovalService _service;

    public BloatRemovalServiceTests()
    {
        _service = new(Mock.Of<ILogService>(), _tasks.Object, _runner.Object, _files.Object);
        _files.Setup(files => files.FileExists(It.IsAny<string>())).Returns(_storage.ContainsKey);
        _files.Setup(files => files.ReadAllTextAsync(It.IsAny<string>(), default)).ReturnsAsync((string path, CancellationToken _) => _storage[path]);
        _files.Setup(files => files.CreateDirectory(ScriptPaths.ScriptsDirectory));
        _files.Setup(files => files.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .Callback((string path, string text, CancellationToken _) => _storage.Add(path, text)).Returns(Task.CompletedTask);
        _files.Setup(files => files.MoveFile(It.IsAny<string>(), PlanPath))
            .Callback((string path, string destination) => { _storage.Add(destination, _storage[path]); _storage.Remove(path); });
        _files.Setup(files => files.ReplaceFile(It.IsAny<string>(), PlanPath))
            .Callback((string path, string destination) => { _storage[destination] = _storage[path]; _storage.Remove(path); });
        _files.Setup(files => files.DeleteFile(It.IsAny<string>())).Callback((string path) => _storage.Remove(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateRemovalTouchesOnlyTheSelectionAndReturnsIndependentResults(bool save)
    {
        var existing = BloatRemovalScriptGenerator.GenerateScript(["Prior.App"], [], []);
        _storage.Add(PlanPath, existing);
        _runner.Setup(runner => runner.RunScriptAsync(It.IsAny<string>(), null, default)).ReturnsAsync((string script, IProgress<TaskProgressDetail>? _, CancellationToken _) =>
        {
            Assert.Equal(new[] { "New.First", "New.Second" }, BloatRemovalScriptGenerator.ExtractArrayFromScript(script, "packages"));
            Assert.Contains("$reportResults = $true", script);
            return Report("""[{"Key":"package:new.first","Error":null,"RestartRequired":false},{"Key":"package:New.Second","Error":"Windows refused","RestartRequired":false}]""");
        });
        if (save)
        {
            _tasks.Setup(tasks => tasks.IsTaskRegisteredAsync("BloatRemoval")).ReturnsAsync(true);
            _tasks.Setup(tasks => tasks.IsTaskEnabledAsync(TaskPath)).ReturnsAsync(true);
        }

        var outcomes = await _service.ExecuteBloatRemovalAsync([App("first", "New.First"), App("second", "New.Second")], save);

        Assert.Collection(outcomes,
            first => { Assert.Equal("first", first.AppId); Assert.True(first.Success); },
            second => { Assert.Equal("second", second.AppId); Assert.False(second.Success); Assert.Equal("Windows refused", second.Message); });
        if (save)
            Assert.Equal(new[] { "Prior.App", "New.First" }, BloatRemovalScriptGenerator.ExtractArrayFromScript(_storage[PlanPath], "packages"));
        else
        {
            Assert.Equal(existing, _storage[PlanPath]);
            _files.VerifyNoOtherCalls();
            _tasks.VerifyNoOtherCalls();
        }
        Assert.Single(_storage);
    }

    [Fact]
    public async Task CancellationSignalsTheWorkerAndOnlyPersistsConfirmedRemovals()
    {
        using var cancellation = new CancellationTokenSource();
        _storage.Add(PlanPath, BloatRemovalScriptGenerator.GenerateScript([], ["Prior.Capability"], []));
        _tasks.Setup(tasks => tasks.IsTaskRegisteredAsync("BloatRemoval")).ReturnsAsync(true);
        _tasks.Setup(tasks => tasks.IsTaskEnabledAsync(TaskPath)).ReturnsAsync(true);
        _runner.Setup(runner => runner.RunScriptAsync(It.IsAny<string>(), null, CancellationToken.None)).ReturnsAsync(
            (string script, IProgress<TaskProgressDetail>? _, CancellationToken _) =>
            {
                var workerEvent = System.Text.RegularExpressions.Regex.Match(script, @"\$cancellationEventName = '([^']+)'");
                using var signal = EventWaitHandle.OpenExisting(workerEvent.Groups[1].Value);
                Assert.False(signal.WaitOne(0));
                cancellation.Cancel();
                Assert.True(signal.WaitOne(0));
                return Report("""[{"Key":"capability:First.Capability","Error":null,"RestartRequired":true}]""");
            });
        var first = App("first", "unused") with { CapabilityName = "First.Capability" };
        var second = App("second", "unused") with { CapabilityName = "Second.Capability" };

        var outcomes = await _service.ExecuteBloatRemovalAsync([first, second], true, ct: cancellation.Token);

        Assert.Collection(outcomes,
            completed => { Assert.True(completed.Success); Assert.True(completed.Deferred); Assert.False(completed.Cancelled); },
            skipped => { Assert.False(skipped.Success); Assert.True(skipped.Cancelled); });
        Assert.Equal(new[] { "Prior.Capability", "First.Capability" },
            BloatRemovalScriptGenerator.ExtractArrayFromScript(_storage[PlanPath], "capabilities"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PolicyBlockRequiresOptInAndConfirmedRegistration(bool save, bool registrationSucceeds)
    {
        var prior = BloatRemovalScriptGenerator.GenerateScript(["Prior.App"], [], []);
        _storage.Add(PlanPath, prior);
        _runner.Setup(runner => runner.RunScriptAsync(It.IsAny<string>(), null, default)).ThrowsAsync(new ExecutionPolicyException("Policy blocked"));
        if (save)
        {
            _tasks.Setup(tasks => tasks.IsTaskRegisteredAsync("BloatRemoval")).ReturnsAsync(false);
            _tasks.Setup(tasks => tasks.RegisterScheduledTaskAsync(It.Is<RemovalScript>(script =>
                    script.ActualScriptPath == PlanPath && script.Name == "BloatRemoval" && !script.RunOnStartup)))
                .ReturnsAsync(registrationSucceeds ? OperationResult.Succeeded() : OperationResult.Failed("Access denied"));
            if (registrationSucceeds) _tasks.Setup(tasks => tasks.IsTaskEnabledAsync(TaskPath)).ReturnsAsync(true);
        }

        var outcome = Assert.Single(await _service.ExecuteBloatRemovalAsync([App("new", "New.App")], save));

        Assert.Equal(registrationSucceeds, outcome.Success);
        Assert.Equal(registrationSucceeds, outcome.Deferred);
        if (save && !registrationSucceeds) Assert.Contains("Access denied", outcome.Message);
        if (registrationSucceeds)
            Assert.Equal(new[] { "Prior.App", "New.App" }, BloatRemovalScriptGenerator.ExtractArrayFromScript(_storage[PlanPath], "packages"));
        else
            Assert.Equal(prior, _storage[PlanPath]);
        Assert.Single(_storage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task DisabledOrUnreadableExistingTaskCannotDeferRemoval(bool? enabled)
    {
        var previous = BloatRemovalScriptGenerator.GenerateScript(["Prior.App"], [], []);
        _storage.Add(PlanPath, previous);
        _runner.Setup(runner => runner.RunScriptAsync(It.IsAny<string>(), null, default)).ThrowsAsync(new ExecutionPolicyException("Policy blocked"));
        _tasks.Setup(tasks => tasks.IsTaskRegisteredAsync("BloatRemoval")).ReturnsAsync(true);
        _tasks.Setup(tasks => tasks.IsTaskEnabledAsync(TaskPath)).ReturnsAsync(enabled);

        var result = Assert.Single(await _service.ExecuteBloatRemovalAsync([App("new", "New.App")], true));

        Assert.False(result.Success);
        Assert.False(result.Deferred);
        Assert.Contains("disabled or its state could not be verified", result.Message);
        Assert.Equal(previous, _storage[PlanPath]);
        _tasks.Verify(tasks => tasks.RegisterScheduledTaskAsync(It.IsAny<RemovalScript>()), Times.Never);
    }

    [Fact]
    public async Task NewlyRegisteredTaskWithoutEnabledReadbackRestoresThePriorPlan()
    {
        var previous = BloatRemovalScriptGenerator.GenerateScript(["Prior.App"], [], []);
        _storage.Add(PlanPath, previous);
        _runner.Setup(runner => runner.RunScriptAsync(It.IsAny<string>(), null, default)).ThrowsAsync(new ExecutionPolicyException("Policy blocked"));
        _tasks.Setup(tasks => tasks.IsTaskRegisteredAsync("BloatRemoval")).ReturnsAsync(false);
        _tasks.Setup(tasks => tasks.RegisterScheduledTaskAsync(It.IsAny<RemovalScript>())).ReturnsAsync(OperationResult.Succeeded());
        _tasks.Setup(tasks => tasks.IsTaskEnabledAsync(TaskPath)).ReturnsAsync(false);
        _tasks.Setup(tasks => tasks.UnregisterScheduledTaskAsync("BloatRemoval")).ReturnsAsync(OperationResult.Succeeded());

        var result = Assert.Single(await _service.ExecuteBloatRemovalAsync([App("new", "New.App")], true));

        Assert.False(result.Success);
        Assert.False(result.Deferred);
        Assert.Equal(previous, _storage[PlanPath]);
        Assert.Single(_storage);
        _tasks.Verify(tasks => tasks.UnregisterScheduledTaskAsync("BloatRemoval"), Times.Once);
    }

    [Fact]
    public async Task AppliesRestartAndMissingReceiptsToTheOwningSelections()
    {
        _runner.Setup(runner => runner.RunScriptAsync(It.IsAny<string>(), null, default)).ReturnsAsync(
            Report("""[{"Key":"package:Variant.A","Error":null,"RestartRequired":false},{"Key":"feature:MyFeature","Error":null,"RestartRequired":true}]"""));
        var variants = App("app", "Variant.A", "Variant.B");
        var feature = new ItemDefinition { Id = "feature", Name = "Feature", Description = "", OptionalFeatureName = "MyFeature" };

        var result = await _service.ExecuteBloatRemovalAsync([variants, feature], false);

        Assert.False(result[0].Success);
        Assert.Contains("Variant.B", result[0].Message);
        Assert.True(result[1].Success);
        Assert.True(result[1].Deferred);
        _tasks.VerifyNoOtherCalls();
    }

    private static string Report(string json) => "Servicing output\r\n" + BloatRemovalScriptGenerator.ResultPrefix + json;

    private static ItemDefinition App(string id, params string[] packages) =>
        new() { Id = id, Name = id, Description = "", AppxPackageName = packages };
}
