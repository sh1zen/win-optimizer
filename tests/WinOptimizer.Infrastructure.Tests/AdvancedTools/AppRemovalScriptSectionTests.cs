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
    public void AppendBloatRemovalScript_MixedApps_EmitsOneJobWithAllPackageIdentities()
    {
        var sb = new StringBuilder();
        var apps = new List<ConfigurationItem>
        {
            new ConfigurationItem { Id = "windows-app-cortana", AppxPackageName = ["Microsoft.549981C3F5F10"] },
            new ConfigurationItem { Id = "windows-app-edge", AppxPackageName = ["Microsoft.MicrosoftEdge.Stable"] },
            new ConfigurationItem { Id = "windows-app-onedrive", AppxPackageName = ["Microsoft.OneDriveSync"] }
        };

        _sut.AppendBloatRemovalScript(sb, apps, "    ");

        var output = sb.ToString();
        output.Should().Contain("'Microsoft.549981C3F5F10'").And.Contain("'Microsoft.MicrosoftEdge.Stable'")
            .And.Contain("'Microsoft.OneDriveSync'");
        System.Text.RegularExpressions.Regex.Matches(output, "Register-ScheduledTask").Count.Should().Be(1);
        output.Should().Contain("if ($LASTEXITCODE -ne 0)");
    }
}
