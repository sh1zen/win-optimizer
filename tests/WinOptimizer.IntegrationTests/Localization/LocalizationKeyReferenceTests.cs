using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using WinOptimizer.Core.Features.Common.Localization;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.IntegrationTests.Helpers;
using Xunit;

namespace WinOptimizer.IntegrationTests.Localization;

/// <summary>
/// Validates English resources for literal GetString calls and catalog-derived setting
/// names and descriptions, complementing per-language resource validation.
/// </summary>
public class LocalizationKeyReferenceTests
{
    private static readonly string SrcDir = Path.Combine(TestContext.SolutionDir, "src");

    private static readonly string EnJsonPath = Path.Combine(
        TestContext.SolutionDir, "src", "WinOptimizer.UI", "Features", "Common", "Localization", "en.json");

    // Matches a localization key passed as the FIRST argument to any GetString(...) call, on any
    // receiver (_localization.GetString("X"), localizationService.GetString("X", arg), etc.).
    // Only pure string literals are captured; dynamically-built keys (GetString(someVar),
    // GetString($"Setting_{id}")) are intentionally skipped here — those that come from the settings
    // catalog are covered by ComputedCatalogKeys_NameAndDescription_MustExist.
    private static readonly Regex GetStringLiteral = new(
        @"GetString\(\s*""([^""]+)""\s*[,)]", RegexOptions.Compiled);

    [Theory]
    [InlineData("localization.GetString(\"Button_Apply\")", "Button_Apply")]
    [InlineData("localization.GetString(\"Format\", value)", "Format")]
    [InlineData("localization.GetString( \"Button_Apply\" )", "Button_Apply")]
    [InlineData("localization.GetString(\"Logs_Topic_\" + topic)", null)]
    [InlineData("localization.GetString($\"Logs_Topic_{topic}\")", null)]
    [InlineData("localization.GetString(key)", null)]
    public void LiteralKeyDiscovery_RequiresACompleteLiteralArgument(string source, string? expectedKey)
    {
        var match = GetStringLiteral.Match(source);
        (match.Success ? match.Groups[1].Value : null).Should().Be(expectedKey);
    }

    private static HashSet<string> EnglishKeys()
    {
        var json = File.ReadAllText(EnJsonPath);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    /// <summary>
    /// The full settings catalog the app ships, enumerated directly from the public static
    /// provider methods (the same ones <c>CompatibleSettingsRegistry.GetKnownFeatureProviders()</c>
    /// wires up) — no DI, no reflection, no runtime registry needed.
    /// </summary>
    private static IReadOnlyList<SettingDefinition> AllSettings()
    {
        var groups = new[]
        {
            // Customize features
            ExplorerCustomizations.GetExplorerCustomizations(),
            StartMenuCustomizations.GetStartMenuCustomizations(),
            TaskbarCustomizations.GetTaskbarCustomizations(),
            WindowsThemeCustomizations.GetWindowsThemeCustomizations(),
            // Optimize features
            PowerOptimizations.GetPowerOptimizations(),
            GamingAndPerformanceOptimizations.GetGamingAndPerformanceOptimizations(),
            NotificationOptimizations.GetNotificationOptimizations(),
            PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations(),
            SoundOptimizations.GetSoundOptimizations(),
            UpdateOptimizations.GetUpdateOptimizations(),
        };

        return groups.SelectMany(g => g.Settings).ToList();
    }

    private static IEnumerable<string> AllCsFiles() =>
        Directory.EnumerateFiles(SrcDir, "*.cs", SearchOption.AllDirectories);

    /// <summary>Every literal GetString key must resolve to an English resource.</summary>
    [Fact]
    public void StaticLiteralLocalizationKeys_MustExistInEnglish()
    {
        var enKeys = EnglishKeys();

        // key -> set of files referencing it (for actionable failure output)
        var references = new Dictionary<string, SortedSet<string>>();

        foreach (var file in AllCsFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match m in GetStringLiteral.Matches(text))
            {
                var key = m.Groups[1].Value;
                if (!references.TryGetValue(key, out var files))
                {
                    files = new SortedSet<string>();
                    references[key] = files;
                }
                files.Add(Path.GetRelativePath(SrcDir, file));
            }
        }

        var missing = references
            .Where(kvp => !enKeys.Contains(kvp.Key))
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => $"  \"{kvp.Key}\"  <- {string.Join(", ", kvp.Value)}")
            .ToList();

        missing.Should().BeEmpty(because:
            "every localization key passed as a literal to GetString(\"...\") has no call-site " +
            "fallback — a missing key renders the raw \"[Key]\" string in the UI. " +
            "Add these keys to en.json (and the other language files), or fix the typo'd call site:\n" +
            string.Join("\n", missing));
    }

    /// <summary>
    /// Every shipped setting must have English name and description resources.
    /// Optional display keys can use the catalog's English fallback text.
    /// </summary>
    [Fact]
    public void ComputedCatalogKeys_NameAndDescription_MustExist()
    {
        var enKeys = EnglishKeys();
        var settings = AllSettings();
        settings.Should().NotBeEmpty(because: "the settings catalog must enumerate at least one feature group");

        var missing = new List<string>();
        foreach (var setting in settings)
        {
            var name = SettingLocalizationKeys.Name(setting);
            var desc = SettingLocalizationKeys.Description(setting);
            if (!enKeys.Contains(name)) missing.Add($"  {name}  (setting Id: {setting.Id})");
            if (!enKeys.Contains(desc)) missing.Add($"  {desc}  (setting Id: {setting.Id})");
        }

        missing.Should().BeEmpty(because:
            "every shipped setting's Name and Description key must exist in en.json:\n" +
            string.Join("\n", missing));
    }

}
