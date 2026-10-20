using System.Xml.Linq;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Services;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.UI.Features.AdvancedTools.Services;
using Xunit;

namespace WinOptimizer.IntegrationTests.Configuration;

/// <summary>Exercises answer-file generation with real catalogue data and isolated OS boundaries.</summary>
public class AutounattendPowerAndNotificationsTests
{
    [Fact]
    public async Task CurrentSelections_PreservesCustomRegistryValues()
    {
        var setting = UpdateOptimizations.GetUpdateOptimizations().Settings
            .Single(setting => setting.RegistrySettings.Any(registry => registry.ValueName == "DODownloadMode"));
        var definitions = new Dictionary<string, IEnumerable<SettingDefinition>> { [FeatureIds.Update] = [setting] };
        var states = new Dictionary<string, SettingStateResult>
        {
            [setting.Id] = new()
            {
                Success = true, CurrentValue = ComboBoxConstants.CustomStateIndex,
                RawValues = new Dictionary<string, object?> { ["DODownloadMode"] = 42 }
            }
        };

        var script = await GenerateScriptAsync(CreateGenerator(definitions, states));

        script.Should().Contain("-Name 'DODownloadMode' -Type 'DWord' -Value 42");
    }

    [Fact]
    public async Task CurrentSelections_PreservesSeparatePowerModesAndRawNumericUnits()
    {
        var power = PowerOptimizations.GetPowerOptimizations().Settings
            .Where(setting => setting.Id is "power-display-timeout" or "power-harddisk-timeout").ToArray();
        var definitions = new Dictionary<string, IEnumerable<SettingDefinition>> { [FeatureIds.Power] = power };
        var states = power.ToDictionary(setting => setting.Id, setting => new SettingStateResult
        {
            Success = true,
            CurrentValue = 7,
            RawValues = new Dictionary<string, object?> { ["ACValue"] = 1200, ["DCValue"] = 300, ["PowerCfgValue"] = 1200 }
        });
        var generator = CreateGenerator(definitions, states);

        var script = await GenerateScriptAsync(generator);

        foreach (var setting in power)
            script.Should().Contain($"G=\"{setting.PowerCfgSettings![0].SettingGuid}\"; AC=1200; DC=300");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configuration_EmitsCorrectNotificationPolicyCommands(bool showNotifications)
    {
        var setting = UpdateOptimizations.GetUpdateOptimizations().Settings
            .Single(setting => setting.Id == "updates-notification-level");
        var definitions = new Dictionary<string, IEnumerable<SettingDefinition>> { [FeatureIds.Update] = new[] { setting } };
        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    [FeatureIds.Update] = new ConfigSection
                    {
                        Items = new[] { new ConfigurationItem { Id = setting.Id, IsSelected = showNotifications } }
                    }
                }
            }
        };

        var script = await GenerateScriptAsync(CreateGenerator(definitions), config);

        const string path = "'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate'";
        if (showNotifications)
        {
            script.Should().Contain($"Remove-RegistryValue -Path {path} -Name 'SetUpdateNotificationLevel'");
            script.Should().Contain($"Remove-RegistryValue -Path {path} -Name 'UpdateNotificationLevel'");
        }
        else
        {
            script.Should().Contain($"Set-RegistryValue -Path {path} -Name 'SetUpdateNotificationLevel' -Type 'DWord' -Value 1");
            script.Should().Contain($"Set-RegistryValue -Path {path} -Name 'UpdateNotificationLevel' -Type 'DWord' -Value 2");
        }
    }

    /// <summary>Uses production generation and parsing while faking system discovery and script execution.</summary>
    private static AutounattendXmlGeneratorService CreateGenerator(
        IReadOnlyDictionary<string, IEnumerable<SettingDefinition>> definitions,
        Dictionary<string, SettingStateResult>? states = null)
    {
        var registry = new Mock<ICompatibleSettingsRegistry>();
        registry.Setup(service => service.GetAllFilteredSettings()).Returns(definitions);
        var discovery = new Mock<ISystemSettingsDiscoveryService>();
        discovery.Setup(service => service.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(states ?? new());
        var query = new Mock<IPowerSettingsQueryService>();
        query.Setup(service => service.GetActivePowerPlanAsync())
            .ReturnsAsync(new PowerPlan { Guid = "381b4222-f694-41f0-9685-ff5bb260df2e", Name = "Balanced" });
        query.Setup(service => service.GetAllPowerSettingsACDCAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, (int? acValue, int? dcValue)>());
        var runner = new Mock<IPowerShellRunner>();
        runner.Setup(service => service.ValidateScriptSyntaxAsync(It.IsAny<string>(), default))
            .Returns(Task.CompletedTask);
        runner.Setup(service => service.ValidateXmlSyntaxAsync(It.IsAny<string>(), default))
            .Callback<string, CancellationToken>((xml, _) => XDocument.Parse(xml)).Returns(Task.CompletedTask);
        var log = Mock.Of<ILogService>();
        var builder = new AutounattendScriptBuilder(query.Object, Mock.Of<IHardwareDetectionService>(), log,
            new ComboBoxResolver(discovery.Object), runner.Object);
        return new AutounattendXmlGeneratorService(registry.Object, discovery.Object, log, builder, runner.Object,
            Mock.Of<ISelectedAppsProvider>());
    }

    /// <summary>Reads the generated XML and removes the test-owned file on every exit.</summary>
    private static async Task<string> GenerateScriptAsync(
        AutounattendXmlGeneratorService generator, UnifiedConfigurationFile? config = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"winoptimizer-unattend-{Guid.NewGuid():N}.xml");
        try
        {
            if (config == null)
                await generator.GenerateFromCurrentSelectionsAsync(path, Array.Empty<ConfigurationItem>());
            else
                await generator.GenerateFromConfigAsync(config, path);
            var xml = XDocument.Load(path);
            return xml.DescendantNodes().OfType<XCData>().Single(node => node.Value.Contains("param(")).Value;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
