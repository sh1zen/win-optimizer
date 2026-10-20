using System.Text.Json;
using WinOptimizer.Core.Features.Security;
using WinOptimizer.IntegrationTests.Helpers;
using Xunit;

namespace WinOptimizer.IntegrationTests.Localization;

/// <summary>Validates enum-derived security labels that static literal-key discovery cannot inspect.</summary>
public sealed class SecurityLocalizationTests
{
    [Fact]
    public void EverySupportedLanguageIncludesAllSecurityLabels()
    {
        var directory = Path.Combine(TestContext.SolutionDir, "src", "WinOptimizer.UI", "Features", "Common", "Localization");
        var keys = Enum.GetNames<SecurityCheckId>().SelectMany(id => new[] { "Security_Check_" + id, "Security_Description_" + id, "Security_Action_" + id })
            .Concat(Enum.GetNames<SecurityArea>().Select(area => "Security_Area_" + area))
            .Concat(Enum.GetNames<SecurityCheckState>().Select(state => "Security_Status_" + state)).ToArray();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var key in keys)
                Assert.True(document.RootElement.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString()),
                    $"Missing {key} in {Path.GetFileName(file)}.");
        }
    }
}
