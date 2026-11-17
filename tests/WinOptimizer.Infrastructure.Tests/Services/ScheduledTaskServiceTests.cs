using FluentAssertions;
using Moq;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class ScheduledTaskServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IFileSystemService> _mockFileSystem = new();
    private readonly ScheduledTaskService _service;

    public ScheduledTaskServiceTests()
    {
        _service = new ScheduledTaskService(_mockLog.Object, _mockFileSystem.Object);
    }

    [Fact]
    public async Task RegisterScheduledTaskAsync_NullScript_ReturnsFailure()
    {
        var result = await _service.RegisterScheduledTaskAsync(null!);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RegisterScheduledTaskAsync_NullScriptPath_ReturnsFailure()
    {
        var script = new RemovalScript
        {
            Name = "TestTask",
            Content = "# script content",
            TargetScheduledTaskName = "TestTask",
            ActualScriptPath = null,
        };

        var result = await _service.RegisterScheduledTaskAsync(script);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Script or script path is null");
    }

    [Fact]
    public async Task ReadingTaskStatesReusesSessionAndRetriesAConnectionFailureInPlace()
    {
        var calls = new List<(int Session, string Name)>();
        var connections = 0;
        var service = new ScheduledTaskService(_mockLog.Object, _mockFileSystem.Object, () =>
            new TaskStateSession(++connections, calls));

        var states = await service.GetTaskStatesAsync([@"\Test\Ready", @"\Test\Missing", @"\Test\Disabled", @"\Test\Reconnect", @"\Test\Ready"]);

        Assert.Equal(2, connections);
        Assert.Equal(new[] { (1, "Ready"), (1, "Missing"), (1, "Disabled"), (1, "Reconnect"), (2, "Reconnect"), (2, "Ready") }, calls);
        Assert.Equal(new bool?[] { true, null, false, true, true }, states.Select(state => state.Enabled));
        Assert.All(states, state => Assert.Null(state.Error));
    }

    public sealed class TaskStateSession(int connection, List<(int Session, string Name)> calls)
    {
        public TaskStateSession GetFolder(string path)
        {
            Assert.Equal(@"\Test", path);
            return this;
        }

        public TaskState GetTask(string name)
        {
            calls.Add((connection, name));
            return name switch
            {
                "Missing" => throw new FileNotFoundException("Task is missing"),
                "Reconnect" when connection == 1 => throw new COMException("Connection lost"),
                "Disabled" => new(1),
                _ => new(3)
            };
        }
    }

    public sealed record TaskState(int State);
}
