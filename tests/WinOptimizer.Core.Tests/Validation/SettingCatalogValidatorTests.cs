using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Validation;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using Xunit;
using Xunit.Abstractions;

namespace WinOptimizer.Core.Tests.Validation;

public class SettingCatalogValidatorTests
{
    private static readonly Regex UserHiveHeader = new(@"^\s*\[HKEY_CURRENT_USER\\", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex SystemHiveHeader = new(@"^\s*\[(HKEY_LOCAL_MACHINE|HKEY_CLASSES_ROOT|HKEY_USERS|HKEY_CURRENT_CONFIG)\\", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private readonly ITestOutputHelper _output;

    public SettingCatalogValidatorTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> AllGroups()
    {
        yield return new object[] { "GamingAndPerformance", GamingAndPerformanceOptimizations.GetGamingAndPerformanceOptimizations() };
        yield return new object[] { "Notification", NotificationOptimizations.GetNotificationOptimizations() };
        yield return new object[] { "Power", PowerOptimizations.GetPowerOptimizations() };
        yield return new object[] { "PrivacyAndSecurity", PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations() };
        yield return new object[] { "Sound", SoundOptimizations.GetSoundOptimizations() };
        yield return new object[] { "Update", UpdateOptimizations.GetUpdateOptimizations() };
        yield return new object[] { "Explorer", ExplorerCustomizations.GetExplorerCustomizations() };
        yield return new object[] { "StartMenu", StartMenuCustomizations.GetStartMenuCustomizations() };
        yield return new object[] { "Taskbar", TaskbarCustomizations.GetTaskbarCustomizations() };
        yield return new object[] { "WindowsTheme", WindowsThemeCustomizations.GetWindowsThemeCustomizations() };
    }

    [Theory]
    [MemberData(nameof(AllGroups))]
    public void Settings_satisfy_catalog_invariants(string featureName, SettingGroup group)
    {
        var violations = SettingCatalogValidator.Validate(group);

        foreach (var v in violations)
            _output.WriteLine($"[{featureName}] {v.SettingId}: {v.Message}");

        violations.Should().BeEmpty($"feature '{featureName}' has {violations.Count} catalog violation(s)");
        foreach (var setting in group.Settings)
            AssertAuthoredSetting(setting);
    }

    /// <summary>Checks authored metadata and export routing alongside the production flag validator.</summary>
    private static void AssertAuthoredSetting(SettingDefinition setting)
    {
        if (setting.InputType == InputType.Selection)
        {
            if (setting.Recommendation is not { LoadDynamicOptions: true })
            {
                setting.ComboBox.Should().NotBeNull($"{setting.Id} requires selection metadata");
                setting.ComboBox!.Options.Should().NotBeNullOrEmpty($"{setting.Id} requires static options");
            }
            if (setting.ComboBox?.Options is { } options)
                options.Select(option => option.DisplayName).Should().OnlyHaveUniqueItems($"{setting.Id} has duplicate option names");

            if (setting.PowerCfgSettings is not { Count: > 0 })
            {
                foreach (var registry in setting.RegistrySettings)
                    registry.RecommendedValue.Should().BeNull($"{setting.Id} selection recommendations belong to option mappings");
            }
        }

        if (setting.InputType is InputType.Toggle or InputType.CheckBox
            && setting.RegistrySettings.Count > 0 && !setting.IsSubjectivePreference)
        {
            (setting.RecommendedToggleState.HasValue || setting.RegistrySettings.Any(registry => registry.RecommendedValue is not null))
                .Should().BeTrue($"{setting.Id} requires a toggle recommendation");
        }

        foreach (var content in setting.RegContents.SelectMany(registry => new[] { registry.EnabledContent, registry.DisabledContent }))
        {
            if (string.IsNullOrEmpty(content)) continue;
            (UserHiveHeader.IsMatch(content) && SystemHiveHeader.IsMatch(content)).Should().BeFalse(
                $"{setting.Id} must split user and system registry hives into separate export blocks");
        }
        foreach (var script in setting.PowerShellScripts)
        foreach (var content in new[] { script.EnabledScript, script.DisabledScript, script.Script })
        {
            if (string.IsNullOrEmpty(content)) continue;
            if (content.Contains("HKCU:", System.StringComparison.OrdinalIgnoreCase)
                || content.Contains("HKEY_CURRENT_USER", System.StringComparison.OrdinalIgnoreCase))
                script.RunContext.Should().Be(RunContext.User, $"{setting.Id} must export HKCU scripts in the user's context");
        }
    }
}
