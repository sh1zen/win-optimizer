using System.Text;
using FluentAssertions;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.ScriptSections;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class AppRemovalScriptSectionTests
{
    private readonly AppRemovalScriptSection _sut = new();

    [Fact]
    public void AppendScriptsDirectorySetup_ChecksAndCreatesTheScriptsDirectory()
    {
        var sb = new StringBuilder();

        _sut.AppendScriptsDirectorySetup(sb);

        var output = sb.ToString();

        output.Should().Contain("$scriptsDir");
        output.Should().Contain("$env:ProgramData\\win-optimizer\\Scripts");
        output.Should().Contain("New-Item");
        output.Should().Contain("-ItemType Directory");
        output.Should().Contain("Test-Path");
    }

    [Fact]
    public void AppendScriptsDirectorySetup_UsesProvidedIndent()
    {
        var sb = new StringBuilder();

        _sut.AppendScriptsDirectorySetup(sb, "        ");

        sb.ToString().Should().Contain("        $scriptsDir");
    }

    [Fact]
    public void AppendScriptsDirectorySetup_DefaultIndentIsEmpty()
    {
        var sb = new StringBuilder();

        _sut.AppendScriptsDirectorySetup(sb);

        var lines = sb.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("$scriptsDir");
    }

    [Fact]
    public async Task AppendBloatRemovalScriptAsync_RegularApps_EmitsRemovalAndScheduledTask()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem
            {
                Id = "windows-app-cortana",
                AppxPackageName = ["Microsoft.549981C3F5F10"]
            }
        };

        await _sut.AppendBloatRemovalScriptAsync(sb, apps, "    ");

        var output = sb.ToString();

        output.Should().Contain("WINDOWS APPS REMOVAL");
        output.Should().Contain("BloatRemoval");
        output.Should().Contain("Register-ScheduledTask");
        output.Should().Contain("win-optimizer");
    }

    [Fact]
    public async Task AppendBloatRemovalScriptAsync_EdgeApp_EmitsEdgeRemoval()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem
            {
                Id = "windows-app-edge",
                AppxPackageName = ["Microsoft.Edge"]
            }
        };

        await _sut.AppendBloatRemovalScriptAsync(sb, apps, "    ");

        var output = sb.ToString();
        output.Should().Contain("EdgeRemoval");
    }

    [Fact]
    public async Task AppendBloatRemovalScriptAsync_OneDriveApp_EmitsOneDriveRemoval()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem
            {
                Id = "windows-app-onedrive",
                AppxPackageName = ["Microsoft.OneDrive"]
            }
        };

        await _sut.AppendBloatRemovalScriptAsync(sb, apps, "    ");

        var output = sb.ToString();
        output.Should().Contain("OneDriveRemoval");
    }

    [Fact]
    public async Task AppendBloatRemovalScriptAsync_Capability_IncludedInScript()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem
            {
                Id = "windows-cap-wordpad",
                CapabilityName = "Microsoft.Windows.WordPad~~~~0.0.1.0"
            }
        };

        await _sut.AppendBloatRemovalScriptAsync(sb, apps, "    ");

        var output = sb.ToString();
        output.Should().Contain("BloatRemoval");
    }

    [Fact]
    public async Task AppendBloatRemovalScriptAsync_OptionalFeature_IncludedInScript()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem
            {
                Id = "windows-opt-ie",
                OptionalFeatureName = "Internet-Explorer-Optional-amd64"
            }
        };

        await _sut.AppendBloatRemovalScriptAsync(sb, apps, "    ");

        var output = sb.ToString();
        output.Should().Contain("BloatRemoval");
    }

    [Fact]
    public async Task AppendBloatRemovalScriptAsync_AppWithMultiplePackages_IncludesAllPackages()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem
            {
                Id = "windows-app-xbox",
                AppxPackageName = ["Microsoft.GamingApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay"]
            }
        };

        await _sut.AppendBloatRemovalScriptAsync(sb, apps, "    ");

        var output = sb.ToString();
        output.Should().Contain("BloatRemoval");
    }

    [Fact]
    public async Task AppendBloatRemovalScriptAsync_MixedApps_EmitsAllSections()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem { Id = "windows-app-cortana", AppxPackageName = ["Microsoft.549981C3F5F10"] },
            new ConfigurationItem { Id = "windows-app-edge", AppxPackageName = ["Microsoft.Edge"] },
            new ConfigurationItem { Id = "windows-app-onedrive", AppxPackageName = ["Microsoft.OneDrive"] }
        };

        await _sut.AppendBloatRemovalScriptAsync(sb, apps, "    ");

        var output = sb.ToString();
        output.Should().Contain("BloatRemoval");
        output.Should().Contain("EdgeRemoval");
        output.Should().Contain("OneDriveRemoval");
    }

    [Fact]
    public void AppendWinOptimizerInstallerScriptContent_ContainsDownloadUrl()
    {
        var sb = new StringBuilder();

        _sut.AppendWinOptimizerInstallerScriptContent(sb);

        sb.ToString().Should().Contain("https://raw.githubusercontent.com/sh1zen/win-optimizer/main/win-optimizer.ps1");
    }

    [Fact]
    public void AppendWinOptimizerInstallerScriptContent_ContainsShortcutCreationAndErrorHandling()
    {
        var sb = new StringBuilder();

        _sut.AppendWinOptimizerInstallerScriptContent(sb);

        var output = sb.ToString();

        output.Should().Contain("Install WinOptimizer.lnk");
        output.Should().Contain("WScript.Shell");
        output.Should().Contain("CreateShortcut");
        output.Should().Contain("try {");
        output.Should().Contain("catch {");
    }

    [Fact]
    public void AppendWinOptimizerInstallerScriptContent_UsesProvidedIndent()
    {
        var sb = new StringBuilder();

        _sut.AppendWinOptimizerInstallerScriptContent(sb, "        ");

        sb.ToString().Should().Contain("        # Create desktop shortcut for win-optimizer installer");
    }
}
