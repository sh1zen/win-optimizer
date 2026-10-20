using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Core.Tests.Models;

public class ConfigurationItemTests
{
    [Fact]
    public void JsonRoundTrip_PreservesAllProperties()
    {
        var original = new ConfigurationItem
        {
            Id = "power-setting",
            Name = "Power Plan",
            IsSelected = true,
            InputType = InputType.Selection,
            SelectedIndex = 3,
            PowerPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e",
            PowerPlanName = "Balanced",
            CustomStateValues = new Dictionary<string, object>
            {
                { "key1", "value1" },
                { "key2", 42L },
            },
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ConfigurationItem>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.Name.Should().Be(original.Name);
        deserialized.IsSelected.Should().Be(original.IsSelected);
        deserialized.InputType.Should().Be(original.InputType);
        deserialized.SelectedIndex.Should().Be(original.SelectedIndex);
        deserialized.PowerPlanGuid.Should().Be(original.PowerPlanGuid);
        deserialized.PowerPlanName.Should().Be(original.PowerPlanName);
        deserialized.CustomStateValues.Should().ContainKey("key1");
    }

    [Fact]
    public void JsonDeserialization_HandlesUnknownProperties()
    {
        var json = """{"Id":"test","Name":"Test","UnknownProp":"value"}""";

        var item = JsonSerializer.Deserialize<ConfigurationItem>(json);

        item.Should().NotBeNull();
        item!.Id.Should().Be("test");
        item.Name.Should().Be("Test");
    }

    [Fact]
    public void JsonDeserialization_HandlesMissingProperties()
    {
        var json = """{"Id":"test"}""";

        var item = JsonSerializer.Deserialize<ConfigurationItem>(json);

        item.Should().NotBeNull();
        item!.Id.Should().Be("test");
        item.Name.Should().BeEmpty();
        item.IsSelected.Should().BeNull();
    }
}
