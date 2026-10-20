using System.Text;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public class WinGetOutputTests
{
    [Fact]
    public void CursorReset_PublishesFormattedProgressImmediatelyAndPromotesItOnceOnLineFeed()
    {
        var output = new StringBuilder();
        var events = new List<string>();
        var lines = new WinGetOutputLines(output,
            line => events.Add("output:" + line), line => events.Add("progress:" + line), line => line.ToUpperInvariant());
        lines.Append('a');
        lines.EmitProgress();
        Assert.Equal(new[] { "progress:A" }, events);
        lines.Append('\n');
        lines.Append('\n');
        lines.Append('b');
        lines.Complete();

        Assert.Equal(new[] { "progress:A", "output:A", "output:b" }, events);
        Assert.Equal($"A{Environment.NewLine}b{Environment.NewLine}", output.ToString());
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("ready\n\n", "ready\n", "output:ready")]
    [InlineData("one\r\ntwo\rthree\nlast", "one\ntwo\nthree\nlast\n", "progress:one|output:one|progress:two|output:three|output:last")]
    [InlineData("first\r\r\n\nend\r", "first\nend\n", "progress:first|output:first|progress:end")]
    [InlineData("unclosed", "unclosed\n", "output:unclosed")]
    [InlineData("\tX\0\n", "\tX\0\n", "output:\tX\0")]
    public async Task RedirectedOutput_PreservesTranscriptAndCallbackOrder(string input, string transcript, string callbacks)
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        var output = new StringBuilder();
        var events = new List<string>();

        await WinGetCliRunner.ReadStdoutCharByCharAsync(reader, output,
            line => events.Add("output:" + line), line => events.Add("progress:" + line));

        Assert.Equal(transcript.Replace("\n", Environment.NewLine), output.ToString());
        Assert.Equal(callbacks, string.Join('|', events));
    }
}
