using System.Text;
using System.Text.Json;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises the production history writer and reader with an isolated filesystem boundary.</summary>
public sealed class ChangeHistoryServiceTests
{
    [Fact]
    public async Task RecentPagesReadNewestReceiptsAndContinueWithoutReplayingEarlierRows()
    {
        _service.LogSettingChange("First setting", "Privacy", "on", "off");
        using (_service.BeginBatch("Reviewed configuration")) _service.LogAppChange("Second app", AppChangeKind.Removed);
        var latest = await _service.ReadRecentAsync("ChangeHistory.jsonl", pageSize: 1);
        var receipt = Assert.Single(latest.Entries);
        Assert.Equal("Second app", receipt.Name); Assert.Equal("Reviewed configuration", receipt.Batch);
        Assert.True(latest.HasMore);
        var previous = await _service.ReadRecentAsync("ChangeHistory.jsonl", latest.NextOffset, 1);
        Assert.Equal("First setting", Assert.Single(previous.Entries).Name);
        Assert.False(previous.HasMore);
    }

    private readonly Mock<IFileSystemService> _files = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<ILogService> _log = new();
    private readonly Dictionary<string, string> _contents = [];
    private readonly ChangeHistoryService _service;
    private static string PathFor(string name) => Path.Combine(WinOptimizer.Core.Features.Common.Constants.AppDataPaths.History, name);
    private ChangeHistoryEntry[] Entries => _contents.GetValueOrDefault(PathFor("ChangeHistory.jsonl"), "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<ChangeHistoryEntry>(line, LogPageReader.JsonOptions)!).ToArray();

    public ChangeHistoryServiceTests()
    {
        _files.Setup(files => files.DirectoryExists(It.IsAny<string>())).Returns(true);
        _files.Setup(files => files.FileExists(It.IsAny<string>())).Returns((string path) => _contents.ContainsKey(path));
        _files.Setup(files => files.GetFiles(It.IsAny<string>(), "ChangeHistory.jsonl", SearchOption.TopDirectoryOnly))
            .Returns(() => _contents.Keys.Where(path => Path.GetFileName(path) == "ChangeHistory.jsonl").ToArray());
        _files.Setup(files => files.AppendAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()))
            .Callback<string, string, Encoding>((path, text, _) => _contents[path] = _contents.GetValueOrDefault(path, "") + text);
        _files.Setup(files => files.OpenReadShared(It.IsAny<string>()))
            .Returns((string path) => new MemoryStream(Encoding.UTF8.GetBytes(_contents[path])));
        _files.Setup(files => files.GetLastWriteTime(It.IsAny<string>())).Returns(new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc));
        _service = new(_files.Object, _localization.Object, _log.Object);
    }

    [Theory]
    [InlineData("Privacy")]
    [InlineData(null)]
    public void SettingChangesPreserveNamesGroupsValuesAndTimestamp(string? group)
    {
        _service.LogSettingChange("Telemetry \"setting\"", group, "Enabled\nwith details", "Disabled — 漢字");
        var entry = Assert.Single(Entries);
        Assert.Equal(HistoryEntryKind.SettingChange, entry.Kind);
        Assert.Equal("Telemetry \"setting\"", entry.Name);
        Assert.Equal(group, entry.Group);
        Assert.Equal("Enabled\nwith details", entry.Before);
        Assert.Equal("Disabled — 漢字", entry.After);
        Assert.NotNull(entry.Timestamp);
        Assert.Null(entry.Batch);
    }

    [Fact]
    public void SettingActionIsDistinctFromAValueChange()
    {
        _service.LogSettingAction("Clean Temp Files", "System");
        var entry = Assert.Single(Entries);
        Assert.Equal(HistoryEntryKind.SettingAction, entry.Kind);
        Assert.Equal("Clean Temp Files", entry.Name);
        Assert.Null(entry.Before);
        Assert.Null(entry.After);
    }

    [Theory]
    [InlineData(AppChangeKind.Installed, "ChangeHistory_AppInstalled", "App installed", HistoryEntryKind.AppInstalled)]
    [InlineData(AppChangeKind.Removed, "ChangeHistory_AppRemoved", "App removed", HistoryEntryKind.AppRemoved)]
    public void ApplicationChangesStoreTheirKindAndOriginalLocalizedLabel(AppChangeKind kind, string key, string label, HistoryEntryKind expected)
    {
        _localization.Setup(service => service.GetString(key)).Returns(label);
        _service.LogAppChange("Example App", kind);
        var entry = Assert.Single(Entries);
        Assert.Equal(expected, entry.Kind);
        Assert.Equal("Example App", entry.Name);
        Assert.Equal(label, entry.ActionLabel);
    }

    [Fact]
    public void EmptyBatchesPerformNoFilesystemWork_AndNestedBatchesShareTheirOuterIdentifier()
    {
        using (_service.BeginBatch("Empty")) { }
        _files.VerifyNoOtherCalls();
        using (_service.BeginBatch("Outer"))
        using (_service.BeginBatch("Inner"))
        {
            _service.LogSettingAction("First", null);
            _service.LogSettingAction("Second", null);
        }
        Assert.All(Entries, entry => Assert.Equal("Outer", entry.Batch));
        Assert.NotNull(Entries[0].BatchId);
        Assert.Equal(Entries[0].BatchId, Entries[1].BatchId);
        _service.LogSettingAction("After batch", null);
        Assert.Null(Entries[2].BatchId);
        Assert.Null(Entries[2].Batch);
    }

    [Fact]
    public void BatchScopeCanBeDisposedTwiceWithoutEndingAnotherBatch()
    {
        var first = _service.BeginBatch("First");
        first.Dispose(); first.Dispose();
        using (_service.BeginBatch("Second")) _service.LogSettingAction("Action", null);
        Assert.Equal("Second", Assert.Single(Entries).Batch);
    }

    [Fact]
    public void FirstWriteCreatesTheDirectoryAndOneStructuredReceipt()
    {
        _files.Setup(files => files.DirectoryExists(It.IsAny<string>())).Returns(false);
        _service.LogSettingAction("Action", null);
        _files.Verify(files => files.CreateDirectory(It.IsAny<string>()), Times.Once);
        Assert.Single(Entries);
        _files.Verify(files => files.AppendAllText(It.Is<string>(path => path.EndsWith("ChangeHistory.jsonl")), It.IsAny<string>(), It.IsAny<Encoding>()), Times.Once);
    }

    [Fact]
    public void WriteFailuresAndLocalizationFailuresDoNotBlockActualOperations()
    {
        _files.Setup(files => files.AppendAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>())).Throws(new IOException("disk full"));
        _service.LogSettingChange("Name", "Group", "before", "after");
        _service.LogSettingAction("Name", null);
        _service.LogAppChange("App", AppChangeKind.Installed);
        _service.GetFilePath();
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Throws(new InvalidOperationException("localization unavailable"));
        _service.LogAppChange("App", AppChangeKind.Removed);
        _log.Verify(log => log.Log(LogLevel.Warning, It.Is<string>(message => message.Contains("ChangeHistoryService")), It.IsAny<Exception?>()), Times.AtLeast(5));
    }

    [Fact]
    public void GetFilePathEnsuresAnEmptyStructuredFileWithoutAHeader()
    {
        var path = _service.GetFilePath();
        Assert.Equal(PathFor("ChangeHistory.jsonl"), path);
        Assert.Empty(_contents[path]);
    }

    [Fact]
    public async Task OnlyTheStructuredHistoryFileIsListedAndReadable()
    {
        var other = PathFor("ChangeHistory.txt");
        _contents[other] = "Unrecognized content";
        _service.LogSettingChange("First setting", "Group", "before", "after");
        _service.LogSettingAction("Second setting", "Group");
        Assert.Equal("ChangeHistory.jsonl", Assert.Single(await _service.GetFilesAsync()).FileName);
        var first = await _service.ReadAsync("ChangeHistory.jsonl", pageSize: 1);
        Assert.Equal("First setting", Assert.Single(first.Entries).Name);
        Assert.True(first.HasMore);
        var next = await _service.ReadAsync("ChangeHistory.jsonl", first.NextOffset, pageSize: 1);
        Assert.Equal("Second setting", Assert.Single(next.Entries).Name);
        Assert.False(next.HasMore);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReadAsync("ChangeHistory.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReadRecentAsync("ChangeHistory.txt"));
        Assert.Equal("Unrecognized content", _contents[other]);
    }

    [Fact]
    public async Task ReadFailuresAreReportedRatherThanReturnedAsAnEmptyHistory()
    {
        _files.Setup(files => files.OpenReadShared(It.IsAny<string>())).Throws(new IOException("unavailable history"));
        await Assert.ThrowsAsync<IOException>(() => _service.ReadAsync("ChangeHistory.jsonl"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ReadAsync("../ChangeHistory.jsonl"));
    }

    [Fact]
    public async Task InaccessibleHistoryIsAnErrorWhileAMissingDirectoryIsEmpty()
    {
        _files.Setup(files => files.GetFiles(It.IsAny<string>(), "ChangeHistory.jsonl", SearchOption.TopDirectoryOnly))
            .Throws(new UnauthorizedAccessException("history access denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetFilesAsync());
        _files.Setup(files => files.GetFiles(It.IsAny<string>(), "ChangeHistory.jsonl", SearchOption.TopDirectoryOnly))
            .Throws(new DirectoryNotFoundException());
        Assert.Empty(await _service.GetFilesAsync());
    }

    [Fact]
    public async Task ListingAnEmptyHistoryDoesNotCreateFiles()
    {
        Assert.Empty(await _service.GetFilesAsync());
        _files.Verify(files => files.AppendAllText(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Encoding>()), Times.Never);
        _files.Verify(files => files.CreateDirectory(It.IsAny<string>()), Times.Never);
    }
}
