using System.Text;
using FluentAssertions;
using WinOptimizer.Infrastructure.Features.AdvancedTools.ScriptSections;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class ScriptPreambleSectionTests
{

    [Fact]
    public void AppendLoggingSetup_ContainsLogFunctionAndModeDetection()
    {
        var sb = new StringBuilder();

        ScriptPreambleSection.AppendLoggingSetup(sb);

        var output = sb.ToString();

        output.Should().Contain("$LogPath");
        output.Should().Contain("Optimizations.txt");
        output.Should().Contain("function Write-Log");
        output.Should().Contain("[string]$Message");
        output.Should().Contain("$UserCustomizations");
        output.Should().Contain("User Customizations Only");
        output.Should().Contain("System Customizations");
    }

    [Fact]
    public void AppendHelperFunctions_ContainsUserRegistryBinaryAndProcessHelpers()
    {
        var sb = new StringBuilder();

        ScriptPreambleSection.AppendHelperFunctions(sb);

        var output = sb.ToString();

        output.Should().Contain("function Get-TargetUser");
        output.Should().Contain("function Get-UserSID");
        output.Should().Contain("function Set-RegistryValue");
        output.Should().Contain("function Remove-RegistryValue");
        output.Should().Contain("function Remove-RegistryKey");
        output.Should().Contain("function New-RegistryKey");
        output.Should().Contain("function Set-BinaryBit");
        output.Should().Contain("function Set-BinaryByte");
        output.Should().Contain("function Start-ProcessAsUser");
    }

    [Fact]
    public void AppendStartProcessAsUser_ContainsInteropAndSessionDetection()
    {
        var sb = new StringBuilder();

        ScriptPreambleSection.AppendStartProcessAsUser(sb);

        var output = sb.ToString();

        output.Should().Contain("DllImport");
        output.Should().Contain("advapi32.dll");
        output.Should().Contain("CreateProcessAsUserW");
        output.Should().Contain("WTSGetActiveConsoleSessionId");
    }
}
