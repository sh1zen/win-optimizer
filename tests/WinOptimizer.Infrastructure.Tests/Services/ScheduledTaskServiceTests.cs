using FluentAssertions;
using Moq;
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
}
