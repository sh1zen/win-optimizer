using WinOptimizer.Core.Features.Utilities;
using Xunit;

namespace WinOptimizer.Core.Tests.Utilities;

public sealed class UtilityValidationTests
{
    [Theory]
    [InlineData("PATH", "%USERPROFILE%\\bin;C:\\Tools", true)]
    [InlineData("TEST", "", false)]
    public void VariablesPreserveRawValues(string name, string value, bool expandable)
    {
        var variable = new EnvironmentVariableEntry(name, value, expandable);
        variable.Validate();
        Assert.Equal(value, variable.Value);
        Assert.Equal(expandable, variable.Expandable);
    }

    [Theory]
    [InlineData("", "value")]
    [InlineData("BAD=NAME", "value")]
    [InlineData("BAD\nNAME", "value")]
    [InlineData("TEST", "bad\0value")]
    public void InvalidVariablesAreRejected(string name, string value) =>
        Assert.Throws<ArgumentException>(() => new EnvironmentVariableEntry(name, value, false).Validate());

    [Theory]
    [InlineData("# comment\r\n127.0.0.1 localhost local.test # note\r\n::1 localhost\r\n")]
    [InlineData("\n# 0.0.0.0 disabled.test\n0.0.0.0 ads.example.test\n")]
    public void HostsAcceptsIpv4Ipv6AliasesAndDisabledLines(string content) => HostsSyntax.Validate(content);

    [Theory]
    [InlineData("not-an-ip example.test")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1 https://example.test")]
    [InlineData("127.0.0.1 *.example.test")]
    [InlineData("fe80::1%3 scoped.test")]
    [InlineData("127.0.0.1 host\0.test")]
    public void HostsRejectsInvalidMappings(string content) => Assert.Throws<ArgumentException>(() => HostsSyntax.Validate(content));

    [Fact]
    public void HostsReportsTheFailingLine()
    {
        var error = Assert.Throws<ArgumentException>(() => HostsSyntax.Validate("# comment\n127.0.0.1 localhost\ninvalid host"));
        Assert.Contains("Line 3", error.Message);
    }
}
