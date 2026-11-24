using WinOptimizer.Core.Features.SoftwareApps.Utilities;
using Xunit;

namespace WinOptimizer.Core.Tests.Utilities;

public class BloatRemovalScriptGeneratorTests
{
    [Fact]
    public void ScriptRefreshPreservesPackageCapabilityAndFeatureSelections()
    {
        var original = BloatRemovalScriptGenerator.GenerateScript(
            ["Example.App", "Another.App"], ["Example.Capability"], ["ExampleFeature"]);

        var updated = BloatRemovalScriptGenerator.UpdateScriptTemplate(original);

        Assert.Equal(new[] { "Example.App", "Another.App" }, BloatRemovalScriptGenerator.ExtractArrayFromScript(updated, "packages"));
        Assert.Equal(new[] { "Example.Capability" }, BloatRemovalScriptGenerator.ExtractArrayFromScript(updated, "capabilities"));
        Assert.Equal(new[] { "ExampleFeature" }, BloatRemovalScriptGenerator.ExtractArrayFromScript(updated, "optionalFeatures"));
    }

    [Fact]
    public void MissingDataArrayReturnsAnEmptySelection()
    {
        Assert.Empty(BloatRemovalScriptGenerator.ExtractArrayFromScript("# no selection", "packages"));
    }
}
