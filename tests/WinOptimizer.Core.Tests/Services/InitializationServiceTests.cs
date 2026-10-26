using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Services;

public class InitializationServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly InitializationService _service;

    public InitializationServiceTests()
    {
        _service = new InitializationService(_mockLog.Object);
    }

    [Fact]
    public void MultipleFeatures_OnlyCompletesWhenAllDone()
    {
        _service.StartFeatureInitialization("Feature1");
        _service.StartFeatureInitialization("Feature2");

        _service.CompleteFeatureInitialization("Feature1");
        _service.IsGloballyInitializing.Should().BeTrue();

        _service.CompleteFeatureInitialization("Feature2");
        _service.IsGloballyInitializing.Should().BeFalse();
    }

    [Fact]
    public void StartFeatureInitialization_DuplicateAdd_StillWorksCorrectly()
    {
        _service.StartFeatureInitialization("Feature1");
        _service.StartFeatureInitialization("Feature1"); // HashSet ignores duplicate

        _service.CompleteFeatureInitialization("Feature1");
        _service.IsGloballyInitializing.Should().BeFalse();
    }

    [Fact]
    public void CompleteFeatureInitialization_NonExistentFeature_DoesNotThrow()
    {
        var action = () => _service.CompleteFeatureInitialization("NonExistent");
        action.Should().NotThrow();
    }

    /// <summary>Checks the normal feature lifecycle and its start/completion receipts together.</summary>
    [Fact]
    public void SingleFeatureLifecycle_UpdatesStateAndLogsStartAndCompletion()
    {
        _service.IsGloballyInitializing.Should().BeFalse();

        _service.StartFeatureInitialization("TestFeature");
        _service.IsGloballyInitializing.Should().BeTrue();
        _mockLog.Verify(l => l.Log(
            WinOptimizer.Core.Features.Common.Enums.LogLevel.Info,
            It.Is<string>(s => s.Contains("TestFeature") && s.Contains("Started")),
            null), Times.Once);

        _service.CompleteFeatureInitialization("TestFeature");
        _service.IsGloballyInitializing.Should().BeFalse();

        _mockLog.Verify(l => l.Log(
            WinOptimizer.Core.Features.Common.Enums.LogLevel.Info,
            It.Is<string>(s => s.Contains("TestFeature") && s.Contains("Completed")),
            null), Times.Once);
    }
}
