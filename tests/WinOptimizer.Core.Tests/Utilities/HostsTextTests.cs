using WinOptimizer.Core.Features.Utilities;
using Xunit;

namespace WinOptimizer.Core.Tests.Utilities;

/// <summary>Exercises the lossless line editing used by the structured hosts editor.</summary>
public sealed class HostsTextTests
{
    private const string Content = "# file header\r\n\r\n  192.168.1.1\trouter.test alias.test  # First floor\r\n\t# ::1 localhost # disabled\n# prose comment\r0.0.0.0 ads.test";

    [Fact]
    public void ParsesMappingsAliasesAndDisabledLinesWithoutRewritingSource()
    {
        var snapshot = HostsText.Parse(Content);
        Assert.False(snapshot.HasInvalidLines); Assert.Equal(3, snapshot.Entries.Count);
        var first = snapshot.Entries[0];
        Assert.Equal("192.168.1.1", first.Address); Assert.Equal("router.test alias.test", first.HostNames);
        Assert.Equal("First floor", first.Comment); Assert.True(first.IsEnabled);
        Assert.Equal("  192.168.1.1\trouter.test alias.test  # First floor\r\n", first.Source);
        Assert.Equal(first.Source, Content.Substring(first.Start, first.Source.Length));
        Assert.False(snapshot.Entries[1].IsEnabled); Assert.Equal("::1", snapshot.Entries[1].Address);
        Assert.Equal("0.0.0.0 ads.test", snapshot.Entries[2].Source);
    }

    [Fact]
    public void ToggleRoundTripPreservesWhitespaceCommentsAndMixedLineEndings()
    {
        var original = HostsText.Parse(Content).Entries[0];
        var disabled = HostsText.SetEnabled(Content, original, false);
        Assert.Equal(Content.Replace("  192.168.1.1", "  # 192.168.1.1"), disabled);
        var entry = HostsText.Parse(disabled).Entries[0];
        Assert.False(entry.IsEnabled);
        Assert.Equal(Content, HostsText.SetEnabled(disabled, entry, true));
    }

    [Fact]
    public void EditAndRemoveTouchOnlyTheSelectedLine()
    {
        var entry = HostsText.Parse(Content).Entries[1];
        var edited = HostsText.Edit(Content, entry, "2001:db8::1", "server.test mirror.test", "Updated", true);
        Assert.Equal(Content.Replace(entry.Source, "2001:db8::1\tserver.test mirror.test # Updated\n"), edited);
        Assert.Equal(Content.Replace(entry.Source, ""), HostsText.Remove(Content, entry));
        var last = HostsText.Parse(Content).Entries[^1];
        Assert.Equal(Content[..last.Start], HostsText.Remove(Content, last));
    }

    [Theory]
    [InlineData("", "127.0.0.1\tlocalhost\r\n")]
    [InlineData("# header\n", "# header\n127.0.0.1\tlocalhost\n")]
    [InlineData("# header\r", "# header\r127.0.0.1\tlocalhost\r")]
    [InlineData("# header\r\n", "# header\r\n127.0.0.1\tlocalhost\r\n")]
    [InlineData("# header", "# header\r\n127.0.0.1\tlocalhost\r\n")]
    public void AddUsesExistingLineEndingAndSeparatesAnUnterminatedLastLine(string content, string expected) =>
        Assert.Equal(expected, HostsText.Add(content, "127.0.0.1", "localhost", "", true));

    [Fact]
    public void InvalidActiveLinesAreReportedAndPreservedWhileCommentsStayComments()
    {
        const string content = "# documentation\nnot-an-ip example.test\n# not-an-ip disabled.test\n127.0.0.1 valid.test\n";
        var snapshot = HostsText.Parse(content);
        Assert.True(snapshot.HasInvalidLines); Assert.Single(snapshot.Entries);
        Assert.Equal(content.Replace("127.0.0.1 valid.test", "# 127.0.0.1 valid.test"),
            HostsText.SetEnabled(content, snapshot.Entries[0], false));
        Assert.True(HostsText.Parse("# null\0comment").HasInvalidLines);
        Assert.False(HostsText.Parse("# documentation only\n\n").HasInvalidLines);
    }

    [Theory]
    [InlineData("not-an-ip", "example.test", "")]
    [InlineData("127.0.0.1", "", "")]
    [InlineData("fe80::1%3", "example.test", "")]
    [InlineData("127.0.0.1", "*.example.test", "")]
    [InlineData("127.0.0.1", "https://example.test", "")]
    [InlineData("127.0.0.1", "example.test\n0.0.0.0 injected.test", "")]
    [InlineData("127.0.0.1", "example.test # hidden", "")]
    [InlineData("127.0.0.1", "example.test", "comment\r\ninjection")]
    [InlineData("127.0.0.1", "example.test", "comment\0")]
    public void DialogFieldsAreValidatedEvenForDisabledEntries(string address, string names, string comment) =>
        Assert.Throws<ArgumentException>(() => HostsText.FormatEntry(address, names, comment, false));

    [Fact]
    public void EditsRejectStaleSourcePositions()
    {
        var entry = HostsText.Parse(Content).Entries[0];
        Assert.Throws<ArgumentException>(() => HostsText.Remove("# changed\n" + Content, entry));
    }
}
