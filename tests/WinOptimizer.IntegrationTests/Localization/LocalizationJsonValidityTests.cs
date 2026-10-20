using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using WinOptimizer.IntegrationTests.Helpers;
using Xunit;

namespace WinOptimizer.IntegrationTests.Localization;

public class LocalizationJsonValidityTests
{
    private static readonly Regex FormatItem = new(
        @"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]*)?\}(?!\})", RegexOptions.Compiled);

    private static readonly string LocalizationFolder =
        Path.Combine(TestContext.SolutionDir, "src", "WinOptimizer.UI", "Features", "Common", "Localization");

    public static IEnumerable<object[]> AllJsonFiles()
    {
        foreach (var file in Directory.GetFiles(LocalizationFolder, "*.json"))
        {
            yield return [Path.GetFileName(file)];
        }
    }

    /// <summary>Translations preserve the arguments and format specifiers consumed by String.Format.</summary>
    [Theory]
    [MemberData(nameof(AllJsonFiles))]
    public void LocalizationFile_PreservesEnglishCompositeFormats(string fileName)
    {
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(LocalizationFolder, "en.json")))!;
        var translated = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(LocalizationFolder, fileName)))!;

        using var assertions = new AssertionScope(fileName);
        foreach (var (key, value) in translated)
        {
            if (!english.TryGetValue(key, out var source))
                continue; // Key coverage is asserted separately for every resource file.

            var expectedItems = FormatItem.Matches(source).Select(match => match.Value).Order().ToArray();
            var actualItems = FormatItem.Matches(value).Select(match => match.Value).Order().ToArray();
            actualItems.Should().Equal(expectedItems, $"{key} must preserve English format arguments");
            if (expectedItems.Length == 0)
                continue;

            Action parseFormat = () => CompositeFormat.Parse(value);
            parseFormat.Should().NotThrow($"{key} must be a valid composite format");
        }
    }

    /// <summary>Each language has nonempty string values and the same keys as English.</summary>
    [Theory]
    [MemberData(nameof(AllJsonFiles))]
    public void LocalizationFile_HasValidStringsAndEnglishKeys(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(LocalizationFolder, fileName));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object, $"{fileName} root must be a JSON object");

        using var assertions = new AssertionScope(fileName);
        var properties = doc.RootElement.EnumerateObject().ToArray();
        properties.Select(property => property.Name).Should().OnlyHaveUniqueItems(
            $"{fileName} must define each localization key exactly once");
        foreach (var property in properties)
        {
            property.Value.ValueKind.Should().Be(JsonValueKind.String, $"key '{property.Name}' must have a string value");
            if (property.Value.ValueKind == JsonValueKind.String)
                property.Value.GetString().Should().NotBeNullOrWhiteSpace($"key '{property.Name}' should not be empty");
        }

        if (fileName != "en.json")
        {
            var englishKeys = GetKeys(Path.Combine(LocalizationFolder, "en.json"));
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            englishKeys.Except(keys).Should().BeEmpty($"{fileName} must contain every English key");
            keys.Except(englishKeys).Should().BeEmpty($"{fileName} must only contain English keys");
        }
    }

    [Theory]
    [MemberData(nameof(AllJsonFiles))]
    public void LocalizationFile_ShouldNotContainUnicodeEscapes(string fileName)
    {
        var filePath = Path.Combine(LocalizationFolder, fileName);
        var lines = File.ReadAllLines(filePath);
        var pattern = new Regex(@"\\u[0-9a-fA-F]{4}");

        var offenders = lines
            .Select((line, idx) => (Line: line, Number: idx + 1))
            .Where(x => pattern.IsMatch(x.Line))
            .Select(x => $"line {x.Number}: {x.Line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty(
            because: $"{fileName} must not contain \\uXXXX unicode escape sequences — write the actual character directly (e.g. & instead of \\u0026, ê instead of \\u00ea). The only allowed escape is \\n for newlines.");
    }

    [Theory]
    [MemberData(nameof(AllJsonFiles))]
    public void LocalizationFile_ShouldNotContainEscapedDoubleQuotes(string fileName)
    {
        var filePath = Path.Combine(LocalizationFolder, fileName);
        var lines = File.ReadAllLines(filePath);
        var pattern = new Regex(@"\\""");

        var offenders = lines
            .Select((line, idx) => (Line: line, Number: idx + 1))
            .Where(x => pattern.IsMatch(x.Line))
            .Select(x => $"line {x.Number}: {x.Line.Trim()}")
            .ToList();

        offenders.Should().BeEmpty(
            because: $"{fileName} must not contain escaped ASCII double quotes (\\\") inside string values — use single quotes (') or typographic curly quotes (\u201C \u201D \u201E) directly instead.");
    }

    private static HashSet<string> GetKeys(string filePath)
    {
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet();
    }
}
