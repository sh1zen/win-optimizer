using System.Text.Json;
using FluentAssertions;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.IntegrationTests.Helpers;
using Xunit;

namespace WinOptimizer.IntegrationTests.Configuration;

[Trait("Category", "Integration")]
public class ConfigRoundTripTests
{
    private static readonly JsonSerializerOptions Options = ConfigFileConstants.JsonOptions;

    [Fact]
    public void RoundTrip_FullConfig_PreservesSectionsAndTypedItemFields()
    {
        // Arrange
        var original = TestSettingFactory.CreateFullConfig();

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<UnifiedConfigurationFile>(json, Options);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Version.Should().Be(original.Version);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);

        deserialized.WindowsApps.IsIncluded.Should().Be(original.WindowsApps.IsIncluded);
        deserialized.WindowsApps.Items.Should().HaveCount(original.WindowsApps.Items.Count);

        deserialized.ExternalApps.IsIncluded.Should().Be(original.ExternalApps.IsIncluded);
        deserialized.ExternalApps.Items.Should().HaveCount(original.ExternalApps.Items.Count);

        deserialized.Customize.IsIncluded.Should().Be(original.Customize.IsIncluded);
        deserialized.Customize.Features.Should().HaveCount(original.Customize.Features.Count);

        deserialized.Optimize.IsIncluded.Should().Be(original.Optimize.IsIncluded);
        deserialized.Optimize.Features.Should().HaveCount(original.Optimize.Features.Count);

        var toggles = deserialized.Customize.Features["Explorer"].Items;
        toggles.Select(item => item.IsSelected).Should().Equal(true, false);
        deserialized.Optimize.Features["Privacy"].Items.Should().ContainSingle().Which.IsSelected.Should().BeNull();

        var selection = deserialized.Optimize.Features["Power"].Items.Should().ContainSingle().Which;
        selection.SelectedIndex.Should().Be(2);
        selection.InputType.Should().Be(InputType.Selection);
        selection.CustomStateValues.Should().NotBeNull().And.ContainKey("key1")
            .WhoseValue.ToString().Should().Be("value1");
        selection.PowerSettings.Should().NotBeNull().And.ContainKey("plan")
            .WhoseValue.ToString().Should().Be("high");

        var app = deserialized.WindowsApps.Items[0];
        var originalApp = original.WindowsApps.Items[0];
        app.AppxPackageName.Should().Equal(originalApp.AppxPackageName!);
        app.WinGetPackageId.Should().Be(originalApp.WinGetPackageId);
        app.CapabilityName.Should().Be(originalApp.CapabilityName);
    }

    [Fact]
    public void RoundTrip_NullProperties_OmittedFromJson()
    {
        // Arrange
        var item = TestSettingFactory.CreateToggleItem("t1", "Simple Toggle", true);
        // These should all be null and omitted from JSON
        item.AppxPackageName.Should().BeNull();

        var config = new UnifiedConfigurationFile
        {
            Customize = TestSettingFactory.CreateFeatureGroup(true, new Dictionary<string, ConfigSection>
            {
                ["Test"] = TestSettingFactory.CreateSection(true, item),
            }),
        };

        // Act
        var json = JsonSerializer.Serialize(config, Options);

        // Assert — null properties should not appear in JSON at all
        json.Should().NotContain("\"AppxPackageName\"");
        json.Should().NotContain("\"WinGetPackageId\"");
        json.Should().NotContain("\"CapabilityName\"");
        json.Should().NotContain("\"SelectedIndex\"");
        json.Should().NotContain("\"CustomStateValues\"");
        json.Should().NotContain("\"PowerSettings\"");
    }

    [Fact]
    public void RoundTrip_CaseInsensitive_DeserializesCorrectly()
    {
        // Arrange — manually construct JSON with different casing
        var json = """
        {
            "version": "2.0",
            "createdAt": "2025-06-15T12:00:00Z",
            "windowsApps": {
                "isIncluded": true,
                "items": [
                    {
                        "id": "app1",
                        "name": "Test App",
                        "isSelected": true,
                        "inputType": 0
                    }
                ]
            },
            "externalApps": { "isIncluded": false, "items": [] },
            "customize": { "isIncluded": false, "features": {} },
            "optimize": { "isIncluded": false, "features": {} }
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UnifiedConfigurationFile>(json, Options);

        // Assert
        config.Should().NotBeNull();
        config!.Version.Should().Be("2.0");
        config.WindowsApps.IsIncluded.Should().BeTrue();
        config.WindowsApps.Items.Should().HaveCount(1);
        config.WindowsApps.Items[0].Id.Should().Be("app1");
    }

    [Fact]
    public void RoundTrip_EmptyConfig_ProducesValidJson()
    {
        // Arrange
        var original = new UnifiedConfigurationFile();

        // Act
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<UnifiedConfigurationFile>(json, Options);

        // Assert
        json.Should().NotBeNullOrEmpty();
        deserialized.Should().NotBeNull();
        deserialized!.Version.Should().Be("2.0");
        deserialized.WindowsApps.Should().NotBeNull();
        deserialized.ExternalApps.Should().NotBeNull();
        deserialized.Customize.Should().NotBeNull();
        deserialized.Optimize.Should().NotBeNull();
    }
}
