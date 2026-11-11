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
}
