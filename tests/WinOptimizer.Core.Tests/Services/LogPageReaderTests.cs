using System.Text;
using System.Text.Json;
using WinOptimizer.Core.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Core.Tests.Services;

/// <summary>Exercises real cursor-based streaming without filesystem or provider dependencies.</summary>
public sealed class LogPageReaderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewestPagesCrossBlocksAndUnicodeWithoutRepeatingOrSkipping(bool trailingNewline)
    {
        var values = new[] { "first — 漢字", new string('x', 20000), "embedded\nnewline", "last" };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\uFEFF" + string.Join("\r\n", values.Select(value => JsonSerializer.Serialize(value))) + (trailingNewline ? "\r\n" : "")));
        long? cursor = null;
        foreach (var expected in values.Reverse())
        {
            var page = await LogPageReader.ReadNewestAsync(stream, cursor, stream.Length, 1,
                line => JsonSerializer.Deserialize<string>(line)!, CancellationToken.None);
            Assert.Equal(expected, Assert.Single(page.Entries));
            Assert.True(page.NextOffset < (cursor ?? stream.Length));
            cursor = page.NextOffset;
        }
        Assert.Equal(0, cursor);
    }

    [Fact]
    public async Task NewestReadsCountMalformedOversizedAndInvalidUtf8_AndRetainValidRows()
    {
        var content = Encoding.UTF8.GetBytes("\"old\"\ninvalid\n" + new string('x', 1024 * 1024 + 1) + "\n\"new\"\n");
        using var stream = new MemoryStream(content.Concat(new byte[] { 0xff, 0x0a }).ToArray());
        var page = await LogPageReader.ReadNewestAsync(stream, null, stream.Length, 10,
            line => JsonSerializer.Deserialize<string>(line)!, CancellationToken.None);
        Assert.Equal(new[] { "new", "old" }, page.Entries);
        Assert.Equal(3, page.UnreadableEntries);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task NewestReadsRejectTruncationAndCancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("one\ntwo\n"));
        await Assert.ThrowsAsync<IOException>(() => LogPageReader.ReadNewestAsync(stream, 100, stream.Length, 1, line => line, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LogPageReader.ReadNewestAsync(stream, null, stream.Length, 1, line => line, cancellation.Token));
    }

    [Fact]
    public async Task PagesPreserveUnicodeAndEscapedNewlinesWithoutRepeatingOrSkippingRecords()
    {
        var values = new[] { "first — 漢字", "embedded\nnewline", new string('x', 20000) };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\r\n", values.Select(value => JsonSerializer.Serialize(value)))));
        long offset = 0;
        foreach (var expected in values)
        {
            var page = await LogPageReader.ReadAsync(stream, offset, stream.Length, 1, line => JsonSerializer.Deserialize<string>(line)!, CancellationToken.None);
            Assert.Equal(expected, Assert.Single(page.Entries));
            Assert.True(page.NextOffset > offset);
            offset = page.NextOffset;
        }
        Assert.Equal(stream.Length, offset);
    }

    [Fact]
    public async Task MalformedAndOversizedRowsAreCounted_AndLaterRecordsRemainReadable()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("broken\n" + new string('x', 1024 * 1024 + 1) + "\n\"valid\"\n"));
        var page = await LogPageReader.ReadAsync(stream, 0, stream.Length, 10, line => JsonSerializer.Deserialize<string>(line)!, CancellationToken.None);
        Assert.Equal("valid", Assert.Single(page.Entries));
        Assert.Equal(2, page.UnreadableEntries);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task CancelledReadsDoNotReturnAPartialPage()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\"entry\"\n"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LogPageReader.ReadAsync(stream, 0, stream.Length, 10,
            line => JsonSerializer.Deserialize<string>(line)!, cancellation.Token));
    }

    [Fact]
    public async Task ATruncatedFileRejectsItsOldCursorAndSnapshot()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => LogPageReader.ReadAsync(stream, 20, stream.Length, 10, line => line, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => LogPageReader.ReadAsync(stream, 0, 20, 10, line => line, CancellationToken.None));
    }
}
