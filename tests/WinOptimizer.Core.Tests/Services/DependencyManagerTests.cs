using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Services;

public class DependencyManagerTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IGlobalSettingsRegistry> _mockRegistry = new();
    private readonly Mock<ISettingApplicationService> _mockSettingApp = new();
    private readonly Mock<ISystemSettingsDiscoveryService> _mockDiscovery = new();
    private readonly DependencyManager _manager;

    public DependencyManagerTests()
    {
        _manager = new DependencyManager(_mockLog.Object, _mockRegistry.Object);
    }

    private static SettingDefinition CreateSetting(
        string id,
        IReadOnlyList<SettingDependency>? dependencies = null) => new()
    {
        Id = id,
        Name = $"Setting {id}",
        Description = $"Description for {id}",
        Dependencies = dependencies ?? Array.Empty<SettingDependency>(),
    };

    [Fact]
    public async Task HandleSettingEnabled_NoDependencies_ReturnsTrue()
    {
        var settings = new[] { CreateSetting("s1") };

        var result = await _manager.HandleSettingEnabledAsync(
            "s1", settings, _mockSettingApp.Object, _mockDiscovery.Object);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSettingEnabled_SettingNotFound_ReturnsTrue()
    {
        var settings = Array.Empty<SettingDefinition>();
        _mockRegistry.Setup(r => r.GetSetting(It.IsAny<string>(), null)).Returns((ISettingItem?)null);

        var result = await _manager.HandleSettingEnabledAsync(
            "nonexistent", settings, _mockSettingApp.Object, _mockDiscovery.Object);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HandleSettingEnabled_DependencyNotFound_ReturnsFalse()
    {
        var dep = new SettingDependency
        {
            DependentSettingId = "s1",
            RequiredSettingId = "missing-dep",
            DependencyType = SettingDependencyType.RequiresEnabled,
        };
        var settings = new ISettingItem[] { CreateSetting("s1", new[] { dep }) };

        // FindSetting for "missing-dep" returns null from both local and registry
        _mockRegistry.Setup(r => r.GetSetting("missing-dep", null)).Returns((ISettingItem?)null);

        var result = await _manager.HandleSettingEnabledAsync(
            "s1", settings, _mockSettingApp.Object, _mockDiscovery.Object);

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(SettingDependencyType.RequiresEnabled, InputType.Toggle, null, true, null)]
    [InlineData(SettingDependencyType.RequiresDisabled, InputType.Toggle, null, false, null)]
    [InlineData(SettingDependencyType.RequiresSpecificValue, InputType.Selection, "2", true, "2")]
    [InlineData(SettingDependencyType.RequiresSpecificValue, InputType.Selection, "", true, null)]
    [InlineData(SettingDependencyType.RequiresSpecificValue, InputType.Toggle, "2", true, null)]
    [InlineData(SettingDependencyType.RequiresValueBeforeAnyChange, InputType.Selection, "2", false, null)]
    public async Task HandleSettingEnabled_UnsatisfiedDependency_PreservesApplicationRequest(
        SettingDependencyType type, InputType inputType, string? requiredValue, bool enable, string? value)
    {
        // Failed discovery requires the normal dependency application path for every dependency kind.
        var dependency = new SettingDependency
        {
            DependentSettingId = "child", RequiredSettingId = "parent",
            DependencyType = type, RequiredValue = requiredValue
        };
        var parent = CreateSetting("parent") with { InputType = inputType };
        var child = CreateSetting("child", [dependency]);
        _mockRegistry.Setup(r => r.GetSetting("parent", null)).Returns(parent);
        _mockDiscovery.Setup(d => d.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>());
        _mockSettingApp.Setup(s => s.ApplySettingAsync(It.IsAny<ApplySettingRequest>()))
            .ReturnsAsync(OperationResult.Succeeded());

        var result = await _manager.HandleSettingEnabledAsync(
            "child", new ISettingItem[] { parent, child }, _mockSettingApp.Object, _mockDiscovery.Object);

        result.Should().BeTrue();
        _mockSettingApp.Verify(s => s.ApplySettingAsync(It.Is<ApplySettingRequest>(request =>
            request.SettingId == "parent" && request.Enable == enable && Equals(request.Value, value))), Times.Once);
    }

    [Fact]
    public async Task HandleSettingDisabled_NoDependents_DoesNotApplyAnything()
    {
        var settings = new ISettingItem[] { CreateSetting("s1") };

        await _manager.HandleSettingDisabledAsync(
            "s1", settings, _mockSettingApp.Object, _mockDiscovery.Object);

        _mockSettingApp.Verify(
            s => s.ApplySettingAsync(It.IsAny<ApplySettingRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleSettingDisabled_WithEnabledDependent_DisablesDependent()
    {
        var dep = new SettingDependency
        {
            DependentSettingId = "child",
            RequiredSettingId = "parent",
            DependencyType = SettingDependencyType.RequiresEnabled,
        };
        var parent = CreateSetting("parent");
        var child = CreateSetting("child", new[] { dep });
        var settings = new ISettingItem[] { parent, child };

        // GetSettingStateAsync for "child": looks up in registry, then calls discovery
        _mockRegistry.Setup(r => r.GetSetting("child", null)).Returns(child);
        _mockDiscovery.Setup(d => d.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                ["child"] = new SettingStateResult { Success = true, IsEnabled = true },
            });
        _mockSettingApp.Setup(s => s.ApplySettingAsync(It.IsAny<ApplySettingRequest>()))
            .ReturnsAsync(OperationResult.Succeeded());

        await _manager.HandleSettingDisabledAsync(
            "parent", settings, _mockSettingApp.Object, _mockDiscovery.Object);

        _mockSettingApp.Verify(s => s.ApplySettingAsync(
            It.Is<ApplySettingRequest>(r => r.SettingId == "child" && r.Enable == false && r.ResetToDefault == true)),
            Times.Once);
    }

    [Fact]
    public async Task HandleSettingDisabled_DependentAlreadyDisabled_DoesNotDisableAgain()
    {
        var dep = new SettingDependency
        {
            DependentSettingId = "child",
            RequiredSettingId = "parent",
            DependencyType = SettingDependencyType.RequiresEnabled,
        };
        var child = CreateSetting("child", new[] { dep });
        var settings = new ISettingItem[]
        {
            CreateSetting("parent"),
            child,
        };

        _mockRegistry.Setup(r => r.GetSetting("child", null)).Returns(child);
        _mockDiscovery.Setup(d => d.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                ["child"] = new SettingStateResult { Success = true, IsEnabled = false },
            });

        await _manager.HandleSettingDisabledAsync(
            "parent", settings, _mockSettingApp.Object, _mockDiscovery.Object);

        _mockSettingApp.Verify(
            s => s.ApplySettingAsync(It.IsAny<ApplySettingRequest>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleSettingValueChanged_NoSpecificValueDependents_DoesNothing()
    {
        var settings = new ISettingItem[] { CreateSetting("s1") };

        await _manager.HandleSettingValueChangedAsync(
            "s1", settings, _mockSettingApp.Object, _mockDiscovery.Object);

        _mockSettingApp.Verify(
            s => s.ApplySettingAsync(It.IsAny<ApplySettingRequest>()),
            Times.Never);
    }
}
