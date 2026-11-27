using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class PowerCfgApplierTests
{
    private readonly Mock<IPowerSettingsQueryService> _mockPowerQuery = new();
    private readonly Mock<IHardwareDetectionService> _mockHardware = new();
    private readonly Mock<IComboBoxResolver> _mockComboBox = new();
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IPowerSchemeOperations> _writer = new();
    private readonly Guid _scheme = Guid.NewGuid();
    private readonly PowerCfgApplier _applier;

    public PowerCfgApplierTests()
    {
        var scheme = _scheme;
        _writer.Setup(x => x.GetActiveScheme(out scheme)).Returns(0);
        _applier = new PowerCfgApplier(
            _mockPowerQuery.Object,
            _mockHardware.Object,
            _mockComboBox.Object,
            _mockLog.Object,
            _writer.Object);
    }

    private static SettingDefinition CreatePowerSetting(
        string id,
        InputType inputType = InputType.Toggle,
        PowerModeSupport powerMode = PowerModeSupport.Both,
        string? units = null) => new()
    {
        Id = id,
        Name = $"Power Setting {id}",
        Description = $"Description for {id}",
        InputType = inputType,
        NumericRange = units != null
            ? new NumericRangeMetadata { MinValue = 0, MaxValue = 100, Units = units }
            : null,
        PowerCfgSettings = new[]
        {
            new PowerCfgSetting
            {
                SubgroupGuid = "238c9fa8-0aad-41ed-83f4-97be242c8f20",
                SettingGuid = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da",
                PowerModeSupport = powerMode,
                Units = units,
                RecommendedValueAC = null,
                RecommendedValueDC = null,
                DefaultValueAC = null,
                DefaultValueDC = null
            }
        }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPowerCfgSettingsAsync_WithoutSettings_ReturnsSuccess(bool hasEmptyList)
    {
        var setting = CreatePowerSetting("no-power") with
        {
            PowerCfgSettings = hasEmptyList ? Array.Empty<PowerCfgSetting>() : null,
        };

        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, true, null);

        result.Success.Should().BeTrue();
        _writer.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(PowerModeSupport.Both, true, true, true)]
    [InlineData(PowerModeSupport.ACOnly, true, true, false)]
    [InlineData(PowerModeSupport.DCOnly, true, false, true)]
    [InlineData(PowerModeSupport.Both, false, true, false)]
    [InlineData(PowerModeSupport.DCOnly, false, false, false)]
    public async Task ApplyPowerCfgSettingsAsync_ToggleWritesOnlySupportedPowerSources(
        PowerModeSupport mode, bool hasBattery, bool writesAc, bool writesDc)
    {
        var setting = CreatePowerSetting("toggle", powerMode: mode);
        var powerSetting = setting.PowerCfgSettings![0];
        var subgroup = Guid.Parse(powerSetting.SubgroupGuid);
        var settingId = Guid.Parse(powerSetting.SettingGuid);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(hasBattery);
        _mockPowerQuery.Setup(q => q.GetPowerSettingACDCValuesAsync(powerSetting)).ReturnsAsync((0, 0));

        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, true, null);

        result.Success.Should().BeTrue();
        _mockHardware.Verify(h => h.HasBatteryAsync(), Times.Once);
        _mockPowerQuery.Verify(q => q.GetPowerSettingACDCValuesAsync(powerSetting), Times.Once);
        var scheme = _scheme;
        _writer.Verify(w => w.GetActiveScheme(out scheme), Times.Once);
        _writer.Verify(w => w.WriteValue(_scheme, subgroup, settingId, true, 1), writesAc ? Times.Once() : Times.Never());
        _writer.Verify(w => w.WriteValue(_scheme, subgroup, settingId, false, 1), writesDc ? Times.Once() : Times.Never());
        _writer.Verify(w => w.SetActiveScheme(_scheme), writesAc || writesDc ? Times.Once() : Times.Never());
        _writer.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(5, 0, 0, "active power scheme")]
    [InlineData(0, 5, 0, "DC")]
    [InlineData(0, 0, 5, "activate")]
    public async Task ApplyPowerCfgSettingsAsync_ReportsNativeFailure(uint lookup, uint dcWrite, uint activation, string error)
    {
        var setting = CreatePowerSetting("fixture");
        var scheme = _scheme;
        _writer.Setup(x => x.GetActiveScheme(out scheme)).Returns(lookup);
        _writer.Setup(x => x.WriteValue(_scheme, It.IsAny<Guid>(), It.IsAny<Guid>(), false, 1)).Returns(dcWrite);
        _writer.Setup(x => x.SetActiveScheme(_scheme)).Returns(activation);
        _mockHardware.Setup(x => x.HasBatteryAsync()).ReturnsAsync(true);
        _mockPowerQuery.Setup(x => x.GetPowerSettingACDCValuesAsync(setting.PowerCfgSettings![0])).ReturnsAsync((0, 0));

        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, true, null);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain(error).And.Contain("Windows error 5");
        _writer.Verify(x => x.WriteValue(_scheme, It.IsAny<Guid>(), It.IsAny<Guid>(), true, 1), lookup == 0 ? Times.Once() : Times.Never());
        _writer.Verify(x => x.WriteValue(_scheme, It.IsAny<Guid>(), It.IsAny<Guid>(), false, 1), lookup == 0 ? Times.Once() : Times.Never());
        _writer.Verify(x => x.SetActiveScheme(_scheme), lookup == 0 ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_DisableToggle_UsesZeroValue()
    {
        // Arrange: enable=false for Toggle should produce valueToApply=0
        var setting = CreatePowerSetting("disable-toggle", InputType.Toggle, PowerModeSupport.Both);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(true);
        _mockPowerQuery
            .Setup(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()))
            .ReturnsAsync((1, 1));

        // Act
        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, enable: false, value: null);

        // Assert: The method should log with value 0
        result.Success.Should().BeTrue();
        _mockLog.Verify(l => l.Log(
            LogLevel.Info,
            It.Is<string>(msg => msg.Contains("disable-toggle") && msg.Contains("value: 0")),
            null), Times.Once);
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_EnableToggle_UsesOneValue()
    {
        // Arrange: enable=true for Toggle should produce valueToApply=1
        var setting = CreatePowerSetting("enable-toggle", InputType.Toggle, PowerModeSupport.Both);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(true);
        _mockPowerQuery
            .Setup(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()))
            .ReturnsAsync((0, 0));

        // Act
        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, enable: true, value: null);

        // Assert: The method should log with value 1
        result.Success.Should().BeTrue();
        _mockLog.Verify(l => l.Log(
            LogLevel.Info,
            It.Is<string>(msg => msg.Contains("enable-toggle") && msg.Contains("value: 1")),
            null), Times.Once);
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_SelectionInput_ResolvesIndexAndLogsValue()
    {
        // Arrange: Selection with int index
        var setting = CreatePowerSetting("selection-power", InputType.Selection, PowerModeSupport.Both);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(true);
        _mockComboBox
            .Setup(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), 2))
            .Returns(42);
        _mockPowerQuery
            .Setup(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()))
            .ReturnsAsync((0, 0));

        // Act
        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, enable: true, value: 2);

        // Assert: ComboBoxResolver was called to map index 2 to its real value
        result.Success.Should().BeTrue();
        _mockComboBox.Verify(c => c.GetValueFromIndex(
            It.Is<SettingDefinition>(s => s.Id == "selection-power"), 2), Times.Once);
        _mockLog.Verify(l => l.Log(
            LogLevel.Info,
            It.Is<string>(msg => msg.Contains("selection-power") && msg.Contains("value: 42")),
            null), Times.Once);
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_SelectionSeparate_WithTuple_ResolvesACDCSeparately()
    {
        // Arrange: Selection with Separate PowerModeSupport and ValueTuple
        var setting = CreatePowerSetting("sel-separate", InputType.Selection, PowerModeSupport.Separate);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(true);
        _mockComboBox
            .Setup(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), 1))
            .Returns(10);
        _mockComboBox
            .Setup(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), 3))
            .Returns(30);
        _mockPowerQuery
            .Setup(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()))
            .ReturnsAsync((0, 0));

        var tupleValue = (1, 3);

        // Act
        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, enable: true, value: tupleValue);

        // Assert: Resolver called with both AC (index 1) and DC (index 3)
        result.Success.Should().BeTrue();
        _mockComboBox.Verify(c => c.GetValueFromIndex(
            It.Is<SettingDefinition>(s => s.Id == "sel-separate"), 1), Times.Once);
        _mockComboBox.Verify(c => c.GetValueFromIndex(
            It.Is<SettingDefinition>(s => s.Id == "sel-separate"), 3), Times.Once);
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_SelectionSeparate_WithDictionary_ResolvesACDCSeparately()
    {
        // Arrange: Selection with Separate PowerModeSupport and Dictionary
        var setting = CreatePowerSetting("sel-dict", InputType.Selection, PowerModeSupport.Separate);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(true);
        _mockComboBox
            .Setup(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), 2))
            .Returns(20);
        _mockComboBox
            .Setup(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), 4))
            .Returns(40);
        _mockPowerQuery
            .Setup(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()))
            .ReturnsAsync((0, 0));

        var dictValue = new Dictionary<string, object?>
        {
            ["ACValue"] = 2,
            ["DCValue"] = 4,
        };

        // Act
        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, enable: true, value: dictValue);

        // Assert: Resolver called for both AC and DC indices
        result.Success.Should().BeTrue();
        _mockComboBox.Verify(c => c.GetValueFromIndex(
            It.Is<SettingDefinition>(s => s.Id == "sel-dict"), 2), Times.Once);
        _mockComboBox.Verify(c => c.GetValueFromIndex(
            It.Is<SettingDefinition>(s => s.Id == "sel-dict"), 4), Times.Once);
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_NoBattery_SeparateSelection_StillResolvesValues()
    {
        // Arrange: No battery with Separate mode selection
        var setting = CreatePowerSetting("no-bat-sep", InputType.Selection, PowerModeSupport.Separate);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(false);
        _mockComboBox
            .Setup(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), It.IsAny<int>()))
            .Returns(5);
        _mockPowerQuery
            .Setup(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()))
            .ReturnsAsync((0, 0));

        var tupleValue = (0, 1);

        // Act
        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, true, tupleValue);

        // Assert: Both AC and DC values are resolved even though DC will be skipped at write time
        result.Success.Should().BeTrue();
        _mockComboBox.Verify(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), 0), Times.Once);
        _mockComboBox.Verify(c => c.GetValueFromIndex(It.IsAny<SettingDefinition>(), 1), Times.Once);
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_NumericRange_RequiresAnExplicitValue()
    {
        var setting = CreatePowerSetting("numeric-null", InputType.NumericRange, PowerModeSupport.Both);
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _applier.ApplyPowerCfgSettingsAsync(setting, true, value: null));
        Assert.Equal("value", error.ParamName);
        Assert.Contains("numeric-null", error.Message);
        _mockPowerQuery.Verify(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()), Times.Never);
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_UnsupportedInputType_Throws()
    {
        // Arrange: Action input type with non-null value is not supported
        var setting = CreatePowerSetting("action-type", InputType.Action, PowerModeSupport.Both);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(true);

        // Act
        var action = () => _applier.ApplyPowerCfgSettingsAsync(setting, true, value: "unsupported");

        // Assert
        await action.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Action*not supported*");
    }

    [Fact]
    public async Task ApplyPowerCfgSettingsAsync_NumericRangeSeparate_WithDictionary_ResolvesACDC()
    {
        // Arrange: NumericRange with Separate mode and dictionary values
        var setting = CreatePowerSetting(
            "numeric-separate",
            InputType.NumericRange,
            PowerModeSupport.Separate,
            units: null);
        _mockHardware.Setup(h => h.HasBatteryAsync()).ReturnsAsync(true);
        _mockPowerQuery
            .Setup(q => q.GetPowerSettingACDCValuesAsync(It.IsAny<PowerCfgSetting>()))
            .ReturnsAsync((0, 0));

        var dictValue = new Dictionary<string, object?>
        {
            ["ACValue"] = 5,
            ["DCValue"] = 10,
        };

        // Act
        var result = await _applier.ApplyPowerCfgSettingsAsync(setting, true, dictValue);

        // Assert
        result.Success.Should().BeTrue();
        _mockLog.Verify(l => l.Log(
            LogLevel.Info,
            It.Is<string>(msg => msg.Contains("numeric-separate") && msg.Contains("separate AC/DC NumericRange")),
            null), Times.Once);
    }
}
