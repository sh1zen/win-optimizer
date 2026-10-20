using System.Collections.Generic;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class SettingLocalizationServiceTests
{
    private readonly Mock<ILocalizationService> _localizationService = new();
    private readonly Mock<ICompatibleSettingsRegistry> _compatibleSettingsRegistry = new();

    public SettingLocalizationServiceTests()
    {
        // Default: return the key wrapped in brackets to indicate "not found"
        _localizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => $"[{k}]");
    }

    private SettingLocalizationService CreateSut() => new(
        _localizationService.Object,
        _compatibleSettingsRegistry.Object);

    private SettingDefinition CreateTestSetting(
        string id = "test-setting",
        string name = "Test Setting",
        string description = "Test Description",
        string? groupName = "TestGroup",
        ComboBoxMetadata? comboBox = null,
        NumericRangeMetadata? numericRange = null,
        string? versionCompatibilityMessage = null,
        Dictionary<string, string>? crossGroupChildSettings = null) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        GroupName = groupName,
        ComboBox = comboBox,
        NumericRange = numericRange,
        VersionCompatibilityMessage = versionCompatibilityMessage,
        CrossGroupChildSettings = crossGroupChildSettings
    };

    [Fact]
    public void LocalizeSetting_GroupNameWithSpacesAndAmpersand_TriesCompactedFormat()
    {
        _localizationService.Setup(l => l.GetString("SettingGroup_PrivacySecurity"))
            .Returns("Privacy & Security Localized");

        var sut = CreateSut();
        var setting = CreateTestSetting(groupName: "Privacy & Security");

        var result = sut.LocalizeSetting(setting);

        result.GroupName.Should().Be("Privacy & Security Localized");
    }

    [Fact]
    public void LocalizeSetting_GroupName_FallsBackToSnakeCaseFormat()
    {
        // Compacted format returns [key] (not found)
        _localizationService.Setup(l => l.GetString("SettingGroup_PrivacySecurity"))
            .Returns("[SettingGroup_PrivacySecurity]");
        // Snake case format found
        _localizationService.Setup(l => l.GetString("SettingGroup_Privacy_Security"))
            .Returns("Privacy Security Snake");

        var sut = CreateSut();
        var setting = CreateTestSetting(groupName: "Privacy & Security");

        var result = sut.LocalizeSetting(setting);

        result.GroupName.Should().Be("Privacy Security Snake");
    }

    [Fact]
    public void LocalizeSetting_LocalizesVersionCompatibilityMessage_SimpleKey()
    {
        _localizationService.Setup(l => l.GetString("Compatibility_NotSupported"))
            .Returns("This setting is not supported");

        var sut = CreateSut();
        var setting = CreateTestSetting(versionCompatibilityMessage: "Compatibility_NotSupported");

        var result = sut.LocalizeSetting(setting);

        result.VersionCompatibilityMessage.Should().Be("This setting is not supported");
    }

    [Fact]
    public void LocalizeSetting_LocalizesVersionCompatibilityMessage_WithArgs()
    {
        _localizationService.Setup(l => l.GetString("Compatibility_RequiresVersion"))
            .Returns("Requires version {0} or higher");

        var sut = CreateSut();
        var setting = CreateTestSetting(versionCompatibilityMessage: "Compatibility_RequiresVersion|22H2");

        var result = sut.LocalizeSetting(setting);

        result.VersionCompatibilityMessage.Should().Be("Requires version 22H2 or higher");
    }

    [Fact]
    public void LocalizeSetting_VersionCompatibilityMessage_NonCompatibilityKey_NotLocalized()
    {
        var sut = CreateSut();
        var setting = CreateTestSetting(versionCompatibilityMessage: "Some raw message");

        var result = sut.LocalizeSetting(setting);

        result.VersionCompatibilityMessage.Should().Be("Some raw message");
    }

    [Fact]
    public void BuildCrossGroupInfoMessage_WhenNoCustomProperties_ReturnsNull()
    {
        var sut = CreateSut();
        var setting = CreateTestSetting();

        var result = sut.BuildCrossGroupInfoMessage(setting);

        result.Should().BeNull();
    }

    [Fact]
    public void BuildCrossGroupInfoMessage_WhenNoCrossGroupSettings_ReturnsNull()
    {
        var sut = CreateSut();
        var setting = CreateTestSetting(crossGroupChildSettings: new Dictionary<string, string>());

        var result = sut.BuildCrossGroupInfoMessage(setting);

        result.Should().BeNull();
    }

    [Fact]
    public void BuildCrossGroupInfoMessage_WhenChildSettingsExist_BuildsMessage()
    {
        var crossGroupSettings = new Dictionary<string, string>
        {
            ["privacy-child1"] = "Setting_Child1_Name"
        };

        _compatibleSettingsRegistry.Setup(r => r.GetFeatureIdForSetting("privacy-child1"))
            .Returns("Privacy");

        var childSetting = new SettingDefinition
        {
            Id = "privacy-child1",
            Name = "Child Setting 1",
            Description = "Child desc",
            GroupName = "Privacy_Group"
        };
        _compatibleSettingsRegistry.Setup(r => r.GetFilteredSettings("Privacy"))
            .Returns(new[] { childSetting });

        _localizationService.Setup(l => l.GetString("Setting_CrossGroupWarning_Header"))
            .Returns("Warning Header");
        _localizationService.Setup(l => l.GetString("Setting_Child1_Name"))
            .Returns("Localized Child");
        _localizationService.Setup(l => l.GetString("Feature_Privacy_Name"))
            .Returns("Privacy & Security");
        _localizationService.Setup(l => l.GetString("SettingGroup_Privacy_Group"))
            .Returns("Privacy Group Localized");

        var sut = CreateSut();
        var setting = CreateTestSetting(crossGroupChildSettings: crossGroupSettings);

        var result = sut.BuildCrossGroupInfoMessage(setting);

        result.Should().NotBeNull();
        result.Should().Contain("Warning Header");
        result.Should().Contain("Localized Child");
    }

    [Fact]
    public void BuildCrossGroupInfoMessage_WhenFeatureIdNotFound_SkipsSetting()
    {
        var crossGroupSettings = new Dictionary<string, string>
        {
            ["unknown-child1"] = "Setting_Unknown_Name"
        };

        _compatibleSettingsRegistry.Setup(r => r.GetFeatureIdForSetting("unknown-child1"))
            .Returns((string?)null);

        var sut = CreateSut();
        var setting = CreateTestSetting(crossGroupChildSettings: crossGroupSettings);

        var result = sut.BuildCrossGroupInfoMessage(setting);

        result.Should().BeNull();
    }

    [Fact]
    public void LocalizeSetting_ComboBoxOption_WithServiceOptionDisplayName_PreservesWarning()
    {
        // Mirrors WSearch: DisplayName = "ServiceOption_Disabled" (localization key), Warning set.
        _localizationService.Setup(l => l.GetString("ServiceOption_Disabled"))
            .Returns("Disabled");
        // No Setting_{id}_OptionWarning_{i} key -> fallback to raw Warning string.

        var sut = CreateSut();
        var setting = CreateTestSetting(id: "gaming-windows-search-service",
            comboBox: new ComboBoxMetadata
            {
                Options = new[]
                {
                    new WinOptimizer.Core.Features.Common.Models.ComboBoxOption
                    {
                        DisplayName = "ServiceOption_Disabled",
                        Warning = "WARNING: Disabling WSearch breaks Outlook search."
                    },
                    new WinOptimizer.Core.Features.Common.Models.ComboBoxOption
                    {
                        DisplayName = "ServiceOption_ManualRecommended"
                    }
                }
            });

        var result = sut.LocalizeSetting(setting);

        result.ComboBox.Should().NotBeNull();
        result.ComboBox!.Options![0].DisplayName.Should().Be("Disabled");
        result.ComboBox.Options[0].Warning.Should().Be("WARNING: Disabling WSearch breaks Outlook search.");
    }

    [Fact]
    public void LocalizeSetting_DoesNotModifyOriginalSetting()
    {
        _localizationService.Setup(l => l.GetString("Setting_test-setting_Name"))
            .Returns("Localized Name");

        var sut = CreateSut();
        var setting = CreateTestSetting(name: "Original");

        var result = sut.LocalizeSetting(setting);

        setting.Name.Should().Be("Original");
        result.Name.Should().Be("Localized Name");
    }
}
