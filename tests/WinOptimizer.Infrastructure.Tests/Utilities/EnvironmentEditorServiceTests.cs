using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class EnvironmentEditorServiceTests
{
    private readonly Mock<IEnvironmentVariableStore> _store = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    public EnvironmentEditorServiceTests() => _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());

    [Fact]
    public async Task EditsOnlyTheRequestedVariableAndReturnsNotificationStatus()
    {
        var original = new EnvironmentVariableEntry("PATH", "%USERPROFILE%\\bin", true);
        var edited = original with { Value = "%USERPROFILE%\\bin;C:\\Tools" };
        _store.Setup(store => store.Read(EnvironmentScope.User)).Returns([original, new("OTHER", "keep", false)]);
        _store.Setup(store => store.Write(EnvironmentScope.User, original, edited)).Returns(false);
        var service = new EnvironmentEditorService(_store.Object, _mode.Object);
        var result = await service.SaveAsync(EnvironmentScope.User, original, edited);
        Assert.False(result.NotificationDelivered);
        _store.Verify(store => store.Write(EnvironmentScope.User, original, edited), Times.Once);
        _store.Verify(store => store.Write(EnvironmentScope.System, It.IsAny<EnvironmentVariableEntry>(), It.IsAny<EnvironmentVariableEntry>()), Times.Never);
    }

    [Fact]
    public async Task DetectsExternalEditsAndDuplicateCreationCaseInsensitively()
    {
        var original = new EnvironmentVariableEntry("PATH", "before", true);
        _store.Setup(store => store.Read(EnvironmentScope.User)).Returns([original with { Value = "external" }]);
        var service = new EnvironmentEditorService(_store.Object, _mode.Object);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SaveAsync(EnvironmentScope.User, original, original with { Value = "edit" }));
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SaveAsync(EnvironmentScope.User, null, new("path", "new", false)));
        _store.Verify(store => store.Write(It.IsAny<EnvironmentScope>(), It.IsAny<EnvironmentVariableEntry>(), It.IsAny<EnvironmentVariableEntry>()), Times.Never);
    }

    [Fact]
    public async Task CreatesAndDeletesUsingTheOriginalSnapshot()
    {
        var variable = new EnvironmentVariableEntry("TEST", "%TEMP%", true);
        _store.Setup(store => store.Read(EnvironmentScope.User)).Returns(Array.Empty<EnvironmentVariableEntry>());
        var service = new EnvironmentEditorService(_store.Object, _mode.Object);
        await service.SaveAsync(EnvironmentScope.User, null, variable);
        _store.Verify(store => store.Write(EnvironmentScope.User, null, variable), Times.Once);
        _store.Setup(store => store.Read(EnvironmentScope.User)).Returns([variable]);
        await service.DeleteAsync(EnvironmentScope.User, variable);
        _store.Verify(store => store.Write(EnvironmentScope.User, variable, null), Times.Once);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task PreventsWritesOutsideNormalMode(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        var service = new EnvironmentEditorService(_store.Object, _mode.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(EnvironmentScope.User, null, new("TEST", "value", false)));
        _store.Verify(store => store.Write(It.IsAny<EnvironmentScope>(), It.IsAny<EnvironmentVariableEntry>(), It.IsAny<EnvironmentVariableEntry>()), Times.Never);
    }

    [Fact]
    public async Task RejectsRenameAndInvalidScopeBeforeAccessingWindows()
    {
        var service = new EnvironmentEditorService(_store.Object, _mode.Object);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(EnvironmentScope.User, new("OLD", "", false), new("NEW", "", false)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadAsync((EnvironmentScope)42));
        _store.VerifyNoOtherCalls();
    }
}
