using System.Text;
using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Helpers;
using WinOptimizer.Infrastructure.Features.AdvancedTools.ScriptSections;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class FeatureRegistryScriptSectionTests
{
    private readonly Mock<IComboBoxResolver> _comboBoxResolver = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly RegistryCommandEmitter _registryEmitter;
    private readonly FeatureRegistryScriptSection _sut;

    public FeatureRegistryScriptSectionTests()
    {
        _registryEmitter = new RegistryCommandEmitter(_comboBoxResolver.Object, _logService.Object);
        _sut = new FeatureRegistryScriptSection(_registryEmitter, _logService.Object);
    }

    [Fact]
    public void GetFeatureDisplayName_UnknownFeature_FallsBackToFeatureId()
    {
        var result = _sut.GetFeatureDisplayName("NonExistentFeature");

        result.Should().Be("NonExistentFeature Settings");
    }

    // ---------------------------------------------------------------
    // AppendFeatureGroupRegistryEntries - Empty feature group
    // ---------------------------------------------------------------

    [Fact]
    public void AppendFeatureGroupRegistryEntries_NoMatchingSettings_LogsWarning()
    {
        var sb = new StringBuilder();
        var featureGroup = CreateFeatureGroup(FeatureIds.Privacy, new[]
        {
            new ConfigurationItem
            {
                Id = "unknown-setting",
                IsSelected = true,
                InputType = InputType.Toggle
            }
        });

        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>();

        _sut.AppendFeatureGroupRegistryEntries(sb, featureGroup, allSettings, "Optimize", isHkcu: false, indent: "    ");

        _logService.Verify(l => l.Log(
            LogLevel.Warning,
            It.Is<string>(s => s.Contains(FeatureIds.Privacy)),
            null), Times.Once);
    }

    // ---------------------------------------------------------------
    // AppendFeatureGroupRegistryEntries - Scheduled tasks
    // ---------------------------------------------------------------

    [Fact]
    public void AppendFeatureGroupRegistryEntries_WithScheduledTask_EmitsTaskBatch()
    {
        var sb = new StringBuilder();
        var settingDef = new SettingDefinition
        {
            Id = "task-setting",
            Name = "Task Setting",
            Description = "Toggle a scheduled task",
            RegistrySettings = new[]
            {
                new RegistrySetting
                {
                    KeyPath = "HKEY_LOCAL_MACHINE\\Software\\Test",
                    ValueName = "TaskVal",
                    ValueType = RegistryValueKind.DWord,
                    EnabledValue = [1],
                    DisabledValue = [0],
                    RecommendedValue = null,
                    DefaultValue = null
                }
            },
            ScheduledTaskSettings = new[]
            {
                new ScheduledTaskSetting { Id = "task1", TaskPath = "\\Microsoft\\Windows\\Test\\Task", RecommendedState = null, DefaultState = null }
            }
        };

        var featureGroup = CreateFeatureGroup("TestFeature", new[]
        {
            new ConfigurationItem
            {
                Id = "task-setting",
                IsSelected = false,
                InputType = InputType.Toggle
            }
        });

        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            { "TestFeature", new[] { settingDef } }
        };

        _sut.AppendFeatureGroupRegistryEntries(sb, featureGroup, allSettings, "Optimize", isHkcu: false, indent: "    ");

        var output = sb.ToString();
        output.Should().Contain("$scheduledTasks");
        output.Should().Contain("schtasks");
        output.Should().Contain("/Disable");
    }

    // ---------------------------------------------------------------
    // AppendFeatureGroupRegistryEntries - Hibernation
    // ---------------------------------------------------------------

    [Fact]
    public void AppendFeatureGroupRegistryEntries_Hibernation_EmitsPowercfgHibernate()
    {
        var sb = new StringBuilder();
        var settingDef = new SettingDefinition
        {
            Id = "power-hibernation-enable",
            Name = "Hibernation",
            Description = "Enable or disable hibernation",
            RegistrySettings = new[]
            {
                new RegistrySetting
                {
                    KeyPath = "HKEY_LOCAL_MACHINE\\SYSTEM\\Test",
                    ValueName = "HibernateEnabled",
                    ValueType = RegistryValueKind.DWord,
                    EnabledValue = [1],
                    DisabledValue = [0],
                    RecommendedValue = null,
                    DefaultValue = null
                }
            }
        };

        var featureGroup = CreateFeatureGroup("TestFeature", new[]
        {
            new ConfigurationItem
            {
                Id = "power-hibernation-enable",
                IsSelected = true,
                InputType = InputType.Toggle
            }
        });

        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            { "TestFeature", new[] { settingDef } }
        };

        _sut.AppendFeatureGroupRegistryEntries(sb, featureGroup, allSettings, "Optimize", isHkcu: false, indent: "    ");

        var output = sb.ToString();
        output.Should().Contain("powercfg /hibernate on");
    }

    // ---------------------------------------------------------------
    // AppendFeatureGroupRegistryEntries - PowerCfgSettings-only setting is skipped
    // ---------------------------------------------------------------

    [Fact]
    public void AppendFeatureGroupRegistryEntries_PowerCfgOnlySettingDef_IsSkipped()
    {
        var sb = new StringBuilder();
        var settingDef = new SettingDefinition
        {
            Id = "powercfg-only",
            Name = "PowerCfg Only",
            Description = "A powercfg-only setting",
            RegistrySettings = Array.Empty<RegistrySetting>(),
            PowerCfgSettings = new[]
            {
                new PowerCfgSetting { SubgroupGuid = "sub", SettingGuid = "set", RecommendedValueAC = null, RecommendedValueDC = null, DefaultValueAC = null, DefaultValueDC = null }
            }
        };

        // Need an HKLM entry to pass the hasEntriesForCurrentHive check - but
        // since RegistrySettings is empty and we are in !isHkcu mode, it won't.
        // This setting effectively gets skipped from the feature entirely.
        var featureGroup = CreateFeatureGroup("TestFeature", new[]
        {
            new ConfigurationItem { Id = "powercfg-only", IsSelected = true, InputType = InputType.Toggle }
        });

        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            { "TestFeature", new[] { settingDef } }
        };

        _sut.AppendFeatureGroupRegistryEntries(sb, featureGroup, allSettings, "Optimize", isHkcu: false, indent: "");

        // No registry commands emitted because this setting has no registry settings matching the hive
        sb.ToString().Should().NotContain("Set-RegistryValue");
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static FeatureGroupSection CreateFeatureGroup(string featureId, ConfigurationItem[] items)
    {
        return new FeatureGroupSection
        {
            IsIncluded = true,
            Features = new Dictionary<string, ConfigSection>
            {
                {
                    featureId, new ConfigSection
                    {
                        IsIncluded = true,
                        Items = items
                    }
                }
            }
        };
    }

}
