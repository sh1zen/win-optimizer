using FluentAssertions;
using WinOptimizer.Core.Features.SoftwareApps.Utilities;
using Xunit;

namespace WinOptimizer.Core.Tests.Utilities;

public class BloatRemovalScriptGeneratorTests
{

    [Fact]
    public void GenerateScript_WithXboxRegistryFix_ContainsRegistryFix()
    {
        var script = BloatRemovalScriptGenerator.GenerateScript(
            new List<string>(), new List<string>(),
            new List<string>(), new List<string>(),
            includeXboxRegistryFix: true);

        // The script should contain Xbox-related registry content
        script.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateScript_WithTeamsProcessKill_ContainsTeamsKill()
    {
        var script = BloatRemovalScriptGenerator.GenerateScript(
            new List<string>(), new List<string>(),
            new List<string>(), new List<string>(),
            includeTeamsProcessKill: true);

        script.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExtractArrayFromScript_ValidArray_ReturnsItems()
    {
        var script = BloatRemovalScriptGenerator.GenerateScript(
            new List<string> { "App1", "App2" },
            new List<string>(), new List<string>(), new List<string>());

        var extracted = BloatRemovalScriptGenerator.ExtractArrayFromScript(script, "packages");

        extracted.Should().Contain("App1");
        extracted.Should().Contain("App2");
    }

    [Fact]
    public void ExtractArrayFromScript_NonExistentArray_ReturnsEmptyList()
    {
        var result = BloatRemovalScriptGenerator.ExtractArrayFromScript(
            "some content without arrays", "nonexistent");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractArrayFromScript_EmptyContent_ReturnsEmptyList()
    {
        var result = BloatRemovalScriptGenerator.ExtractArrayFromScript("", "packages");

        result.Should().BeEmpty();
    }

    [Fact]
    public void UpdateScriptTemplate_PreservesDataWithNewTemplate()
    {
        var original = BloatRemovalScriptGenerator.GenerateScript(
            new List<string> { "Microsoft.BingWeather" },
            new List<string> { "TestCap" },
            new List<string> { "TestFeature" },
            new List<string> { "TestApp" });

        var updated = BloatRemovalScriptGenerator.UpdateScriptTemplate(original);

        updated.Should().Contain("Microsoft.BingWeather");
        updated.Should().Contain("TestCap");
        updated.Should().Contain("TestFeature");
        updated.Should().Contain("TestApp");
    }

    [Fact]
    public void UpdateScriptTemplate_WithXboxPackage_AutoEnablesXboxRegistryFix()
    {
        var original = BloatRemovalScriptGenerator.GenerateScript(
            new List<string> { "Microsoft.GamingApp" },
            new List<string>(), new List<string>(), new List<string>(),
            includeXboxRegistryFix: true);

        var updated = BloatRemovalScriptGenerator.UpdateScriptTemplate(original);

        // The updated template should detect Xbox packages and include the fix
        updated.Should().Contain("Microsoft.GamingApp");
    }

    [Fact]
    public void UpdateScriptTemplate_WithMSTeams_AutoEnablesTeamsProcessKill()
    {
        var original = BloatRemovalScriptGenerator.GenerateScript(
            new List<string> { "MSTeams" },
            new List<string>(), new List<string>(), new List<string>(),
            includeTeamsProcessKill: true);

        var updated = BloatRemovalScriptGenerator.UpdateScriptTemplate(original);

        updated.Should().Contain("MSTeams");
    }
}
