using WinOptimizer.Application.Configuration;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.Application.Tests;

public class ConfigRegistryInitializerTests
{
    private readonly Mock<ICompatibleSettingsRegistry> _mockRegistry = new();
    private readonly Mock<IGlobalSettingsPreloader> _mockPreloader = new();
    private readonly Mock<ILogService> _mockLogService = new();

    /// <summary>Initialization and its log entries follow each dependency's current state.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EnsureInitializedAsync_InitializesAndLogsOnlyMissingDependencies(
        bool registryInitialized, bool preloaded)
    {
        _mockRegistry.Setup(r => r.IsInitialized).Returns(registryInitialized);
        _mockPreloader.Setup(p => p.IsPreloaded).Returns(preloaded);

        await ConfigRegistryInitializer.EnsureInitializedAsync(
            _mockRegistry.Object, _mockPreloader.Object, _mockLogService.Object);

        _mockRegistry.Verify(r => r.InitializeAsync(), registryInitialized ? Times.Never() : Times.Once());
        _mockPreloader.Verify(p => p.PreloadAllSettingsAsync(), preloaded ? Times.Never() : Times.Once());
        _mockLogService.Verify(
            l => l.Log(LogLevel.Info, It.Is<string>(s => s.Contains("registry")), null),
            registryInitialized ? Times.Never() : Times.Once());
        _mockLogService.Verify(
            l => l.Log(LogLevel.Info, It.Is<string>(s => s.Contains("Preloading")), null),
            preloaded ? Times.Never() : Times.Once());
        if (registryInitialized && preloaded)
            _mockLogService.Verify(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<string>(), It.IsAny<Exception?>()),
                Times.Never());
    }

    [Fact]
    public async Task EnsureInitializedAsync_CalledTwice_SecondCallIsNoOp()
    {
        // First call: not initialized
        var callCount = 0;
        _mockRegistry.Setup(r => r.IsInitialized)
            .Returns(() => callCount > 0);
        _mockRegistry.Setup(r => r.InitializeAsync())
            .Callback(() => callCount++)
            .Returns(Task.CompletedTask);

        var preloadCount = 0;
        _mockPreloader.Setup(p => p.IsPreloaded)
            .Returns(() => preloadCount > 0);
        _mockPreloader.Setup(p => p.PreloadAllSettingsAsync())
            .Callback(() => preloadCount++)
            .Returns(Task.CompletedTask);

        // First call initializes
        await ConfigRegistryInitializer.EnsureInitializedAsync(
            _mockRegistry.Object,
            _mockPreloader.Object,
            _mockLogService.Object);

        // Second call should be idempotent
        await ConfigRegistryInitializer.EnsureInitializedAsync(
            _mockRegistry.Object,
            _mockPreloader.Object,
            _mockLogService.Object);

        _mockRegistry.Verify(r => r.InitializeAsync(), Times.Once);
        _mockPreloader.Verify(p => p.PreloadAllSettingsAsync(), Times.Once);
    }
}
