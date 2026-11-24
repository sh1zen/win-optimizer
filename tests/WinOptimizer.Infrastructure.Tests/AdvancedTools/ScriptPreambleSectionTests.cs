using System.Text;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.AdvancedTools.ScriptSections;
using WinOptimizer.Infrastructure.Features.Common.Utilities;
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
    public async Task AppendStartProcessAsUser_CompilesInteropInWindowsPowerShell()
    {
        var sb = new StringBuilder();

        ScriptPreambleSection.AppendStartProcessAsUser(sb);

        // Run the emitted Add-Type command without invoking the native process launcher.
        sb.AppendLine("""
            $ErrorActionPreference = 'Stop'
            $compile = ${function:Start-ProcessAsUser}.Ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Type'
            }, $true)
            & ([scriptblock]::Create($compile.Extent.Text))
            [WinOptimizer.TL].GetMethod('CreateProcessAsUserW').Name
            [WinOptimizer.TL].GetMethod('WTSGetActiveConsoleSessionId').Name
            (New-Object WinOptimizer.TL+SI).GetType().FullName
            (New-Object WinOptimizer.TL+PI).GetType().FullName
            """);

        var runner = new PowerShellRunner(Mock.Of<IFileSystemService>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = await runner.RunScriptInMemoryAsync(sb.ToString(), ct: timeout.Token);

        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Should().Equal("CreateProcessAsUserW", "WTSGetActiveConsoleSessionId", "WinOptimizer.TL+SI", "WinOptimizer.TL+PI");
    }
}
