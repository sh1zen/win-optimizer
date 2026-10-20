using System.Text;
using FluentAssertions;
using WinOptimizer.Infrastructure.Features.AdvancedTools.ScriptSections;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class SpecialFeatureScriptSectionTests
{
    [Fact]
    public void AppendUserCustomizationsScheduledTask_ContainsRegistrationPathAndErrorHandling()
    {
        var sb = new StringBuilder();

        SpecialFeatureScriptSection.AppendUserCustomizationsScheduledTask(sb, "    ");

        var output = sb.ToString();

        output.Should().Contain("USER CUSTOMIZATIONS SCHEDULED TASK");
        output.Should().Contain("Register-ScheduledTask");
        output.Should().Contain("WinOptimizerUserCustomizations");
        output.Should().Contain("Optimizations.ps1");
        output.Should().Contain("try {");
        output.Should().Contain("} catch {");
    }

    [Fact]
    public void AppendUserCustomizationsScheduledTask_UsesCorrectIndent()
    {
        var sb = new StringBuilder();

        SpecialFeatureScriptSection.AppendUserCustomizationsScheduledTask(sb, "        ");

        var output = sb.ToString();
        output.Should().Contain("        Write-Log");
        output.Should().Contain("        try {");
    }

    [Fact]
    public void AppendCleanStartMenuSection_ContainsSectionHeader()
    {
        var sb = new StringBuilder();

        SpecialFeatureScriptSection.AppendCleanStartMenuSection(sb, "    ");

        sb.ToString().Should().Contain("START MENU LAYOUT");
    }

    [Fact]
    public void AppendCleanStartMenuSection_ContainsBothWindowsBranches()
    {
        var sb = new StringBuilder();

        SpecialFeatureScriptSection.AppendCleanStartMenuSection(sb, "    ");

        var output = sb.ToString();

        output.Should().Contain("$buildNumber");
        output.Should().Contain("OSVersion.Version.Build");
        output.Should().Contain("$buildNumber -ge 22000");
        output.Should().Contain("ConfigureStartPins");
        output.Should().Contain("{\"pinnedList\":[]}");
        output.Should().Contain("LayoutModification.xml");
        output.Should().Contain("LayoutModificationTemplate");
    }

    [Fact]
    public void AppendCleanStartMenuSection_ContainsXmlContent()
    {
        var sb = new StringBuilder();

        SpecialFeatureScriptSection.AppendCleanStartMenuSection(sb, "");

        var output = sb.ToString();
        output.Should().Contain("<?xml version=\"1.0\"");
        output.Should().Contain("StartLayoutCollection");
    }

    [Fact]
    public void AppendCleanStartMenuSection_UsesProvidedIndent()
    {
        var sb = new StringBuilder();

        SpecialFeatureScriptSection.AppendCleanStartMenuSection(sb, "INDENT");

        var output = sb.ToString();
        output.Should().Contain("INDENTWrite-Log");
    }
}
