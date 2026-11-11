using System.Text;
using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Helpers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class RegistryCommandEmitterTests
{
    private readonly Mock<IComboBoxResolver> _comboBoxResolver = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly RegistryCommandEmitter _sut;

    public RegistryCommandEmitterTests()
    {
        _sut = new RegistryCommandEmitter(_comboBoxResolver.Object, _logService.Object);
    }

    // ---------------------------------------------------------------
    // EmitRegistryValue - Binary with BitMask
    // ---------------------------------------------------------------

    [Fact]
    public void EmitRegistryValue_BinaryBitMask_EmitsSetBinaryBit()
    {
        var sb = new StringBuilder();
        var regSetting = new RegistrySetting
        {
            KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
            ValueName = "BinaryVal",
            ValueType = RegistryValueKind.Binary,
            BinaryByteIndex = 3,
            BitMask = 0x04,
            RecommendedValue = null,
            DefaultValue = null
        };

        _sut.EmitRegistryValue(sb, regSetting, true, "Bit Setting", "'HKLM:\\Software\\Test'", "BinaryVal", "");

        var output = sb.ToString();
        output.Should().Contain("Set-BinaryBit");
        output.Should().Contain("-ByteIndex 3");
        output.Should().Contain("-BitMask 0x04");
        output.Should().Contain("-SetBit $True");
    }

    // ---------------------------------------------------------------
    // EmitRegistryValue - Binary with ModifyByteOnly
    // ---------------------------------------------------------------

    [Fact]
    public void EmitRegistryValue_BinaryModifyByteOnly_EmitsSetBinaryByte()
    {
        var sb = new StringBuilder();
        var regSetting = new RegistrySetting
        {
            KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
            ValueName = "ByteVal",
            ValueType = RegistryValueKind.Binary,
            BinaryByteIndex = 5,
            ModifyByteOnly = true,
            RecommendedValue = null,
            DefaultValue = null
        };

        _sut.EmitRegistryValue(sb, regSetting, 255, "Byte Setting", "'HKLM:\\Software\\Test'", "ByteVal", "");

        var output = sb.ToString();
        output.Should().Contain("Set-BinaryByte");
        output.Should().Contain("-ByteIndex 5");
        output.Should().Contain("-ByteValue 0xFF");
    }

    // ---------------------------------------------------------------
    // EmitRegistryValue - Binary without BitMask or ModifyByteOnly
    // ---------------------------------------------------------------

    [Fact]
    public void EmitRegistryValue_BinaryWithByteIndex_NoSpecialHandling_EmitsSetRegistryValue()
    {
        var sb = new StringBuilder();
        var regSetting = new RegistrySetting
        {
            KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
            ValueName = "PlainBinary",
            ValueType = RegistryValueKind.Binary,
            BinaryByteIndex = 0,
            RecommendedValue = null,
            DefaultValue = null
        };

        var bytes = new byte[] { 0x01, 0x02 };
        _sut.EmitRegistryValue(sb, regSetting, bytes, "Plain Binary", "'HKLM:\\Software\\Test'", "PlainBinary", "");

        var output = sb.ToString();
        output.Should().Contain("Set-RegistryValue");
        output.Should().Contain("'Binary'");
    }

    // ---------------------------------------------------------------
    // EmitRegistryValueFromDefinition - ModifyByteOnly
    // ---------------------------------------------------------------

    [Fact]
    public void EmitRegistryValueFromDefinition_ModifyByteOnly_ByteValue_FormatsCorrectly()
    {
        var sb = new StringBuilder();
        var regSetting = new RegistrySetting
        {
            KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
            ValueName = "Val",
            ValueType = RegistryValueKind.Binary,
            BinaryByteIndex = 2,
            ModifyByteOnly = true,
            RecommendedValue = null,
            DefaultValue = null
        };

        _sut.EmitRegistryValueFromDefinition(sb, regSetting, (byte)0xAB, isEnabled: true,
            "Def Byte", "'HKLM:\\Software\\Test'", "Val", "");

        var output = sb.ToString();
        output.Should().Contain("Set-BinaryByte");
        output.Should().Contain("0xAB");
    }

    [Fact]
    public void EmitRegistryValueFromDefinition_ModifyByteOnly_IntValue_CastsToByte()
    {
        var sb = new StringBuilder();
        var regSetting = new RegistrySetting
        {
            KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
            ValueName = "Val",
            ValueType = RegistryValueKind.Binary,
            BinaryByteIndex = 0,
            ModifyByteOnly = true,
            RecommendedValue = null,
            DefaultValue = null
        };

        _sut.EmitRegistryValueFromDefinition(sb, regSetting, 15, isEnabled: true,
            "Def Int", "'HKLM:\\Software\\Test'", "Val", "");

        var output = sb.ToString();
        output.Should().Contain("Set-BinaryByte");
        output.Should().Contain("0x0F");
    }

    [Fact]
    public void EmitRegistryValueFromDefinition_ModifyByteOnly_UnknownType_Defaults0x00()
    {
        var sb = new StringBuilder();
        var regSetting = new RegistrySetting
        {
            KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
            ValueName = "Val",
            ValueType = RegistryValueKind.Binary,
            BinaryByteIndex = 0,
            ModifyByteOnly = true,
            RecommendedValue = null,
            DefaultValue = null
        };

        _sut.EmitRegistryValueFromDefinition(sb, regSetting, "unknown", isEnabled: true,
            "Def Unknown", "'HKLM:\\Software\\Test'", "Val", "");

        var output = sb.ToString();
        output.Should().Contain("0x00");
    }

    // ---------------------------------------------------------------
    // AppendToggleCommandsFiltered - Key-Based Setting (ValueName null)
    // ---------------------------------------------------------------

    [Fact]
    public void AppendToggleCommandsFiltered_NullValueName_EnabledValueNull_EmitsRemoveRegistryKey()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-setting", "Test", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test\\Key",
                ValueName = null,
                ValueType = RegistryValueKind.String,
                EnabledValue = [null],
                DisabledValue = [""],
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem { Id = "test-setting", IsSelected = true, InputType = InputType.Toggle };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        sb.ToString().Should().Contain("Remove-RegistryKey");
    }

    [Fact]
    public void AppendToggleCommandsFiltered_NullValueName_EnabledValueEmptyString_EmitsNewRegistryKeyAndDefault()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-setting", "Test", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test\\Key",
                ValueName = null,
                ValueType = RegistryValueKind.String,
                EnabledValue = [""],
                DisabledValue = [null],
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem { Id = "test-setting", IsSelected = true, InputType = InputType.Toggle };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        var output = sb.ToString();
        output.Should().Contain("New-RegistryKey");
        output.Should().Contain("(Default)");
    }

    [Fact]
    public void AppendToggleCommandsFiltered_NullValueName_EnabledValueNonNull_EmitsNewRegistryKey()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-setting", "Test", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test\\Key",
                ValueName = null,
                ValueType = RegistryValueKind.String,
                EnabledValue = ["exists"],
                DisabledValue = [null],
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem { Id = "test-setting", IsSelected = true, InputType = InputType.Toggle };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        var output = sb.ToString();
        output.Should().Contain("New-RegistryKey");
        output.Should().NotContain("(Default)");
    }

    // ---------------------------------------------------------------
    // AppendToggleCommandsFiltered - CustomStateValues path
    // ---------------------------------------------------------------

    [Fact]
    public void AppendToggleCommandsFiltered_WithCustomStateValues_UsesExactValue()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-setting", "Test", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
                ValueName = "Val",
                ValueType = RegistryValueKind.DWord,
                EnabledValue = [1],
                DisabledValue = [0],
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem
        {
            Id = "test-setting",
            IsSelected = true,
            InputType = InputType.Toggle,
            CustomStateValues = new Dictionary<string, object> { { "Val", 42 } }
        };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        sb.ToString().Should().Contain("42");
    }

    [Fact]
    public void AppendToggleCommandsFiltered_WithCustomStateValueNull_SkipsEntry()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-setting", "Test", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
                ValueName = "Val",
                ValueType = RegistryValueKind.DWord,
                EnabledValue = [1],
                DisabledValue = [0],
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem
        {
            Id = "test-setting",
            IsSelected = true,
            InputType = InputType.Toggle,
            CustomStateValues = new Dictionary<string, object> { { "Val", null! } }
        };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        sb.ToString().Trim().Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // AppendToggleCommandsFiltered - Null definition value = deletion
    // ---------------------------------------------------------------

    [Fact]
    public void AppendToggleCommandsFiltered_NullDefinitionValue_EmitsRemoveRegistryValue()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-setting", "Test", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
                ValueName = "Val",
                ValueType = RegistryValueKind.DWord,
                EnabledValue = [null],
                DisabledValue = [0],
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem { Id = "test-setting", IsSelected = true, InputType = InputType.Toggle };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        sb.ToString().Should().Contain("Remove-RegistryValue");
    }

    // ---------------------------------------------------------------
    // AppendToggleCommandsFiltered - Empty string definition value
    // ---------------------------------------------------------------

    [Fact]
    public void AppendToggleCommandsFiltered_EmptyStringValue_EmitsSetRegistryValueWithEmptyString()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-setting", "Test", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
                ValueName = "Val",
                ValueType = RegistryValueKind.String,
                EnabledValue = [""],
                DisabledValue = ["something"],
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem { Id = "test-setting", IsSelected = true, InputType = InputType.Toggle };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        var output = sb.ToString();
        output.Should().Contain("Set-RegistryValue");
        output.Should().Contain("'String'");
        output.Should().Contain("-Value ''");
    }

    // ---------------------------------------------------------------
    // AppendSelectionCommandsFiltered
    // ---------------------------------------------------------------

    [Fact]
    public void AppendSelectionCommandsFiltered_PowerPlanSelection_SkipsEntirely()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("power-plan-selection", "Power Plan", Array.Empty<RegistrySetting>());
        var configItem = new ConfigurationItem
        {
            Id = "power-plan-selection",
            InputType = InputType.Selection,
            SelectedIndex = 0
        };

        _sut.AppendSelectionCommandsFiltered(sb, setting, configItem, isHkcu: false);

        sb.ToString().Trim().Should().BeEmpty();
    }

    [Fact]
    public void AppendSelectionCommandsFiltered_WithCustomStateValues_AppliesValues()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-selection", "Test Selection", new[]
        {
            new RegistrySetting
            {
                KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
                ValueName = "SelVal",
                ValueType = RegistryValueKind.DWord,
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem
        {
            Id = "test-selection",
            InputType = InputType.Selection,
            CustomStateValues = new Dictionary<string, object> { { "SelVal", 3 } }
        };

        _sut.AppendSelectionCommandsFiltered(sb, setting, configItem, isHkcu: false);

        sb.ToString().Should().Contain("Set-RegistryValue");
        sb.ToString().Should().Contain("3");
    }

    [Fact]
    public void AppendSelectionCommandsFiltered_NoValueMappingsOrCustomState_LogsWarning()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-selection", "Test Selection", Array.Empty<RegistrySetting>());
        var configItem = new ConfigurationItem
        {
            Id = "test-selection",
            InputType = InputType.Selection
        };

        _sut.AppendSelectionCommandsFiltered(sb, setting, configItem, isHkcu: false);

        _logService.Verify(l => l.Log(
            LogLevel.Warning,
            It.Is<string>(s => s.Contains("test-selection")),
            null), Times.Once);
    }

    // ---------------------------------------------------------------
    // AppendToggleCommandsFiltered - ApplyPerMonitor wraps in ForEach
    // ---------------------------------------------------------------

    [Fact]
    public void AppendToggleCommandsFiltered_ApplyPerMonitor_WrapsInForEachObject()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-monitor", "Monitor Setting", new[]
        {
            new RegistrySetting
            {
                KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore",
                ValueName = "AutoColorManagementEnabled",
                ValueType = RegistryValueKind.DWord,
                EnabledValue = [1],
                DisabledValue = [0],
                ApplyPerMonitor = true,
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem { Id = "test-monitor", IsSelected = true, InputType = InputType.Toggle };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        var output = sb.ToString();
        output.Should().Contain("Get-ChildItem");
        output.Should().Contain("ForEach-Object");
        output.Should().Contain("$_.PSPath");
        output.Should().Contain("AutoColorManagementEnabled");
    }

    [Fact]
    public void AppendToggleCommandsFiltered_ApplyPerNetworkInterface_WrapsInForEachObject()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-interface", "Interface Setting", new[]
        {
            new RegistrySetting
            {
                KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces",
                ValueName = "TcpAckFrequency",
                ValueType = RegistryValueKind.DWord,
                EnabledValue = [null],
                DisabledValue = [1],
                ApplyPerNetworkInterface = true,
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem { Id = "test-interface", IsSelected = true, InputType = InputType.Toggle };

        _sut.AppendToggleCommandsFiltered(sb, setting, configItem, isHkcu: false);

        var output = sb.ToString();
        output.Should().Contain("Get-ChildItem");
        output.Should().Contain("ForEach-Object");
        output.Should().Contain("$_.PSPath");
        output.Should().Contain("Remove-RegistryValue");
    }

    // ---------------------------------------------------------------
    // ApplyResolvedValues - ApplyPerMonitor wraps in ForEach
    // ---------------------------------------------------------------

    [Fact]
    public void AppendSelectionCommandsFiltered_ApplyPerMonitor_WrapsInForEachObject()
    {
        var sb = new StringBuilder();
        var setting = CreateSettingDefinition("test-monitor-sel", "Monitor Selection", new[]
        {
            new RegistrySetting
            {
                KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\MonitorDataStore",
                ValueName = "SomeValue",
                ValueType = RegistryValueKind.DWord,
                ApplyPerMonitor = true,
                RecommendedValue = null,
                DefaultValue = null
            }
        });
        var configItem = new ConfigurationItem
        {
            Id = "test-monitor-sel",
            InputType = InputType.Selection,
            CustomStateValues = new Dictionary<string, object> { { "SomeValue", 1 } }
        };

        _sut.AppendSelectionCommandsFiltered(sb, setting, configItem, isHkcu: false);

        var output = sb.ToString();
        output.Should().Contain("Get-ChildItem");
        output.Should().Contain("ForEach-Object");
        output.Should().Contain("$_.PSPath");
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static SettingDefinition CreateSettingDefinition(
        string id, string description, IReadOnlyList<RegistrySetting> registrySettings)
    {
        return new SettingDefinition
        {
            Id = id,
            Name = id,
            Description = description,
            RegistrySettings = registrySettings
        };
    }
}
