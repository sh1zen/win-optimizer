using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.IntegrationTests.Helpers;
using Xunit;

namespace WinOptimizer.IntegrationTests.Configuration;

/// <summary>
/// Validates that all embedded .win-optimizer config files conform to the ConfigurationItem
/// schema — catching type mismatches (e.g. string vs string[]) that would cause
/// deserialization failures or silent data loss at runtime.
/// </summary>
[Trait("Category", "Integration")]
public class ConfigSchemaValidationTests
{
    private static readonly string ConfigDir = Path.Combine(
        TestContext.SolutionDir,
        "src", "WinOptimizer.Application", "Configuration", "Resources");

    /// <summary>
    /// Maps each ConfigurationItem property to its expected JSON token types.
    /// This is the source of truth — if the model changes, update this map.
    /// </summary>
    private static readonly Dictionary<string, HashSet<JsonValueKind>> ExpectedPropertyTypes = new()
    {
        ["Id"] = new() { JsonValueKind.String },
        ["Name"] = new() { JsonValueKind.String },
        ["IsSelected"] = new() { JsonValueKind.True, JsonValueKind.False, JsonValueKind.Null },
        ["InputType"] = new() { JsonValueKind.Number },
        ["AppxPackageName"] = new() { JsonValueKind.Array, JsonValueKind.Null },
        ["WinGetPackageId"] = new() { JsonValueKind.String, JsonValueKind.Null },
        ["CapabilityName"] = new() { JsonValueKind.String, JsonValueKind.Null },
        ["OptionalFeatureName"] = new() { JsonValueKind.String, JsonValueKind.Null },
        ["SelectedIndex"] = new() { JsonValueKind.Number, JsonValueKind.Null },
        ["CustomStateValues"] = new() { JsonValueKind.Object, JsonValueKind.Null },
        ["PowerSettings"] = new() { JsonValueKind.Object, JsonValueKind.Null },
        ["PowerPlanGuid"] = new() { JsonValueKind.String, JsonValueKind.Null },
        ["PowerPlanName"] = new() { JsonValueKind.String, JsonValueKind.Null },
    };

    public static IEnumerable<object[]> ConfigFiles()
    {
        foreach (var file in Directory.GetFiles(ConfigDir, "*.win-optimizer"))
        {
            yield return new object[] { Path.GetFileName(file) };
        }
    }

    /// <summary>Validates every item contract in one traversal of each embedded preset.</summary>
    [Theory]
    [MemberData(nameof(ConfigFiles))]
    public void ConfigFile_ItemsConformToSchema(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(ConfigDir, fileName));
        using var doc = JsonDocument.Parse(json);
        var errors = new List<string>();
        var knownProperties = GetConfigurationItemPropertyNames();

        void CheckItem(JsonElement item, string location)
        {
            ValidateItemProperties(item, location, errors);
            foreach (var prop in item.EnumerateObject())
            {
                if (!knownProperties.Contains(prop.Name))
                    errors.Add($"{location}: unrecognized property '{prop.Name}' (not in ConfigurationItem model)");
            }

            foreach (var required in new[] { "Id", "Name", "InputType" })
            {
                if (!item.TryGetProperty(required, out _))
                    errors.Add($"{location}: missing required property '{required}'");
            }

            if (item.TryGetProperty("AppxPackageName", out var packages) && packages.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var package in packages.EnumerateArray())
                {
                    if (package.ValueKind != JsonValueKind.String)
                        errors.Add($"{location}.AppxPackageName[{index}]: expected String, got {package.ValueKind}");
                    index++;
                }
            }
        }

        EnumerateAllItems(doc.RootElement, fileName, CheckItem);

        errors.Should().BeEmpty("all configuration items must conform to the schema:\n" + string.Join("\n", errors));
    }

    private void ValidateItemProperties(JsonElement item, string location, List<string> errors)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (ExpectedPropertyTypes.TryGetValue(prop.Name, out var allowedTypes))
            {
                if (!allowedTypes.Contains(prop.Value.ValueKind))
                {
                    errors.Add(
                        $"{location}.{prop.Name}: expected [{string.Join("|", allowedTypes)}], " +
                        $"got {prop.Value.ValueKind}");
                }
            }
        }
    }

    private void EnumerateAllItems(JsonElement root, string fileName, Action<JsonElement, string> visitor)
    {
        foreach (var sectionName in new[] { "WindowsApps", "ExternalApps" })
        {
            if (root.TryGetProperty(sectionName, out var section) &&
                section.TryGetProperty("Items", out var items))
            {
                int idx = 0;
                foreach (var item in items.EnumerateArray())
                {
                    var itemId = item.TryGetProperty("Id", out var idProp) ? idProp.GetString() : $"[{idx}]";
                    visitor(item, $"{fileName} > {sectionName} > {itemId}");
                    idx++;
                }
            }
        }

        foreach (var groupName in new[] { "Customize", "Optimize" })
        {
            if (root.TryGetProperty(groupName, out var group) &&
                group.TryGetProperty("Features", out var features))
            {
                foreach (var feature in features.EnumerateObject())
                {
                    if (!feature.Value.TryGetProperty("Items", out var items))
                        continue;
                    int idx = 0;
                    foreach (var item in items.EnumerateArray())
                    {
                        var itemId = item.TryGetProperty("Id", out var idProp) ? idProp.GetString() : $"[{idx}]";
                        visitor(item, $"{fileName} > {groupName}.{feature.Name} > {itemId}");
                        idx++;
                    }
                }
            }
        }
    }

    private static HashSet<string> GetConfigurationItemPropertyNames()
    {
        return typeof(ConfigurationItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
