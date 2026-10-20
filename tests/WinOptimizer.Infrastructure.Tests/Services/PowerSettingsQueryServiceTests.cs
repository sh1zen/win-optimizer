using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class PowerSettingsQueryServiceTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly PowerSettingsQueryService _service;

    public PowerSettingsQueryServiceTests()
    {
        _service = new PowerSettingsQueryService(_mockLogService.Object);
    }

    #region GetAvailablePowerPlansAsync

    [Fact]
    public async Task GetAvailablePowerPlansAsync_ReturnsValidPlansWithActivePlanFirst()
    {
        // Act — this calls native PowerEnumerate APIs which may or may not work
        // in the test environment. The service handles exceptions gracefully.
        var result = await _service.GetAvailablePowerPlansAsync();

        result.Should().NotBeNull();
        result.Should().BeOfType<List<PowerPlan>>();
        foreach (var plan in result)
        {
            plan.Guid.Should().NotBeNullOrEmpty();
            plan.Name.Should().NotBeNullOrEmpty();
        }
        result.Count(p => p.IsActive).Should().BeLessOrEqualTo(1);
        if (result.Any(p => p.IsActive))
        {
            result.First().IsActive.Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetAvailablePowerPlansAsync_CachesUntilInvalidated()
    {
        var result1 = await _service.GetAvailablePowerPlansAsync();
        var result2 = await _service.GetAvailablePowerPlansAsync();
        result2.Should().BeSameAs(result1);

        _service.InvalidateCache();
        _service.InvalidateCache();
        _service.InvalidateCache();

        var refreshed = await _service.GetAvailablePowerPlansAsync();
        refreshed.Should().NotBeNull();
        refreshed.Should().NotBeSameAs(result1);
    }

    #endregion

    #region GetActivePowerPlanAsync

    [Fact]
    public async Task GetActivePowerPlanAsync_ReturnsNonNullPlan()
    {
        // Act
        var result = await _service.GetActivePowerPlanAsync();

        // Assert
        result.Should().NotBeNull();
        result.IsActive.Should().BeTrue();
        result.Name.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region GetPowerSettingACDCValuesAsync

    [Fact]
    public async Task GetPowerSettingACDCValuesAsync_MalformedGuid_ReturnsNullsAndLogs()
    {
        // Arrange
        var powerCfgSetting = new PowerCfgSetting
        {
            SubgroupGuid = "not-a-valid-guid",
            SettingGuid = "also-not-valid",
            RecommendedValueAC = null,
            RecommendedValueDC = null,
            DefaultValueAC = null,
            DefaultValueDC = null
        };

        // Act
        var result = await _service.GetPowerSettingACDCValuesAsync(powerCfgSetting);

        // Assert — the method catches exceptions and returns nulls
        result.acValue.Should().BeNull();
        result.dcValue.Should().BeNull();
        _mockLogService.Verify(
            l => l.Log(Core.Features.Common.Enums.LogLevel.Error, It.IsAny<string>()),
            Times.Once);
    }

    #endregion

    #region GetAllPowerSettingsACDCAsync

    [Fact]
    public async Task GetAllPowerSettingsACDCAsync_DefaultParameter_ReturnsNonNullDictionary()
    {
        // Act
        var result = await _service.GetAllPowerSettingsACDCAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<Dictionary<string, (int?, int?)>>();
    }

    [Theory]
    [InlineData("not-a-valid-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task GetAllPowerSettingsACDCAsync_InvalidOrEmptyGuid_ReturnsEmptyDictionary(string guid)
    {
        var result = await _service.GetAllPowerSettingsACDCAsync(guid);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    #endregion

    #region IsSettingHardwareControlledAsync

    [Fact]
    public async Task IsSettingHardwareControlledAsync_ValidSetting_ReturnsBool()
    {
        // Arrange — use a well-known power setting GUID
        var powerCfgSetting = new PowerCfgSetting
        {
            SubgroupGuid = "7516b95f-f776-4464-8c53-06167f40cc99",
            SettingGuid = "aded5e82-b909-4619-9949-f5d71dac0bcb",
            SettingGUIDAlias = "VIDEONORMALLEVEL",
            RecommendedValueAC = null,
            RecommendedValueDC = null,
            DefaultValueAC = null,
            DefaultValueDC = null
        };

        // Act
        var act = async () => await _service.IsSettingHardwareControlledAsync(powerCfgSetting);

        // Assert — should not throw; returns a bool value
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task IsSettingHardwareControlledAsync_MalformedGuid_ReturnsFalse()
    {
        // Arrange
        var powerCfgSetting = new PowerCfgSetting
        {
            SubgroupGuid = "invalid-guid",
            SettingGuid = "also-invalid",
            RecommendedValueAC = null,
            RecommendedValueDC = null,
            DefaultValueAC = null,
            DefaultValueDC = null
        };

        // Act — the exception path returns (null, null) for capabilities,
        // so min == 0 && max == 0 will be false because null != 0
        var result = await _service.IsSettingHardwareControlledAsync(powerCfgSetting);

        // Assert
        result.Should().BeFalse();
    }

    #endregion
}
