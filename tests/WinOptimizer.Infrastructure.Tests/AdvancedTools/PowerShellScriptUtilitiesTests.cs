using FluentAssertions;
using Microsoft.Win32;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Helpers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class PowerShellScriptUtilitiesTests
{
    // ---------------------------------------------------------------
    // EscapePowerShellString
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("hello", "hello")]
    [InlineData("it's", "it''s")]
    [InlineData("it's a 'test'", "it''s a ''test''")]
    public void EscapePowerShellString_EscapesSingleQuotes(string? input, string? expected)
    {
        PowerShellScriptUtilities.EscapePowerShellString(input).Should().Be(expected);
    }

    [Fact]
    public void FormatValueForPowerShell_BinaryByteArray_ReturnsHexArrayLiteral()
    {
        var bytes = new byte[] { 0x0A, 0xFF, 0x00 };

        var result = PowerShellScriptUtilities.FormatValueForPowerShell(bytes, RegistryValueKind.Binary);

        result.Should().Be("@(0x0A,0xFF,0x00)");
    }

    [Fact]
    public void FormatValueForPowerShell_BinaryEmptyByteArray_ReturnsTypedEmptyArray()
    {
        // Empty REG_BINARY (e.g. taskbar-clean's Favorites) must emit a typed empty
        // array so Set-ItemProperty -Type Binary writes a zero-length value, not "@()".
        var result = PowerShellScriptUtilities.FormatValueForPowerShell(new byte[0], RegistryValueKind.Binary);

        result.Should().Be("([byte[]]@())");
    }

    [Fact]
    public void FormatValueForPowerShell_BinarySingleByte_ReturnsHexSingleElement()
    {
        byte value = 0xAB;

        var result = PowerShellScriptUtilities.FormatValueForPowerShell(value, RegistryValueKind.Binary);

        result.Should().Be("@(0xAB)");
    }
}
