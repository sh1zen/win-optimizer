using System.Text.RegularExpressions;
using Moq;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools.WimServices;

/// <summary>Exercises ISO publication and cleanup through production code with fake file/process boundaries.</summary>
public sealed class IsoCreationTests
{
    private const string Media = @"C:\Example\Windows media";
    private const string Output = @"C:\Example\Output folder\Windows.iso";
    private const long ValidSize = 2 * 1024 * 1024;
    private readonly Mock<IFileSystemService> _files = new();
    private readonly Mock<IDismProcessRunner> _runner = new();
    private readonly Mock<IOscdimgToolManager> _tool = new();
    private readonly Mock<ILogService> _log = new();
    private readonly Dictionary<string, string> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly IsoService _service;
    private string? _stagedPath;
    private long _stagedSize = ValidSize;
    private Func<CancellationToken, Task<(int, string)>> _build = _ => Task.FromResult((0, ""));

    public IsoCreationTests()
    {
        _contents[Output] = "Previous valid ISO";
        _contents[Path.Combine(Media, "boot", "etfsboot.com")] = "BIOS boot";
        _contents[Path.Combine(Media, "efi", "microsoft", "boot", "efisys.bin")] = "UEFI boot";
        _files.Setup(files => files.CombinePath(It.IsAny<string[]>())).Returns((string[] paths) => Path.Combine(paths));
        _files.Setup(files => files.GetDirectoryName(It.IsAny<string>())).Returns((string path) => Path.GetDirectoryName(path));
        _files.Setup(files => files.GetExtension(It.IsAny<string>())).Returns((string path) => Path.GetExtension(path));
        _files.Setup(files => files.DirectoryExists(It.IsAny<string>())).Returns(true);
        _files.Setup(files => files.GetFiles(Media, "*", SearchOption.AllDirectories)).Returns([]);
        _files.Setup(files => files.FileExists(It.IsAny<string>())).Returns((string path) => _contents.ContainsKey(path));
        _files.Setup(files => files.GetFileSize(It.IsAny<string>())).Returns((string path) => path == _stagedPath ? _stagedSize : ValidSize);
        _files.Setup(files => files.DeleteFile(It.IsAny<string>())).Callback<string>(path => _contents.Remove(path));
        _files.Setup(files => files.ReplaceFile(It.IsAny<string>(), Output)).Callback<string, string>((source, destination) =>
        {
            Assert.Equal("Previous valid ISO", _contents[destination]);
            _contents[destination] = _contents[source];
            _contents.Remove(source);
        });
        _files.Setup(files => files.MoveFile(It.IsAny<string>(), Output)).Callback<string, string>((source, destination) =>
        {
            Assert.False(_contents.ContainsKey(destination));
            _contents.Add(destination, _contents[source]);
            _contents.Remove(source);
        });
        _tool.Setup(tool => tool.GetOscdimgPath()).Returns(@"C:\Example\Tools\oscdimg.exe");
        _tool.Setup(tool => tool.IsOscdimgAvailableAsync()).ReturnsAsync(true);
        _runner.Setup(runner => runner.CheckDiskSpaceAsync(Output, It.IsAny<long>(), "ISO creation")).ReturnsAsync(true);
        _runner.Setup(runner => runner.RunProcessWithProgressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string arguments, IProgress<TaskProgressDetail>? _, CancellationToken token) =>
            {
                _stagedPath = Regex.Matches(arguments, "\"([^\"]+)\"")[^1].Groups[1].Value;
                Assert.NotEqual(Output, _stagedPath);
                Assert.Equal(Path.GetDirectoryName(Output), Path.GetDirectoryName(_stagedPath));
                Assert.EndsWith(".iso", _stagedPath);
                Assert.False(_contents.ContainsKey(_stagedPath));
                _contents[_stagedPath] = "New ISO";
                return _build(token);
            });
        var text = new Mock<ILocalizationService>();
        text.Setup(localization => localization.GetString(It.IsAny<string>())).Returns((string key) => key);
        _service = new(_files.Object, _log.Object, text.Object, Mock.Of<IProcessExecutor>(), _runner.Object, _tool.Object);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulCreationPublishesOnlyTheValidatedArtifact(bool replacing)
    {
        if (!replacing) _contents.Remove(Output);
        var successReported = false;
        var progress = new InlineProgress(detail =>
        {
            if (detail.StatusText != "Progress_IsoCreatedSuccess") return;
            Assert.Equal("New ISO", _contents[Output]);
            Assert.False(_contents.ContainsKey(_stagedPath!));
            successReported = true;
        });
        Assert.True(await _service.CreateIsoAsync(Media, Output, progress));
        Assert.True(successReported);
        Assert.Equal("New ISO", _contents[Output]);
        _files.Verify(files => files.ReplaceFile(_stagedPath!, Output), replacing ? Times.Once : Times.Never);
        _files.Verify(files => files.MoveFile(_stagedPath!, Output), replacing ? Times.Never : Times.Once);
        _files.Verify(files => files.DeleteFile(Output), Times.Never);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("exception")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("small")]
    public async Task FailedOrInvalidCreationPreservesThePreviousIsoAndRemovesItsTemporaryFile(string failure)
    {
        _build = _ =>
        {
            Assert.Equal("Previous valid ISO", _contents[Output]);
            if (failure == "exception") throw new IOException("Tool failed.");
            if (failure == "missing") _contents.Remove(_stagedPath!);
            if (failure == "empty") _stagedSize = 0;
            if (failure == "small") _stagedSize = 1024;
            return Task.FromResult((failure == "exit" ? 1 : 0, ""));
        };
        Assert.False(await _service.CreateIsoAsync(Media, Output));
        AssertPreserved();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringCreationPreservesThePreviousIsoEvenIfTheToolReturnsSuccess(bool toolThrows)
    {
        using var cancellation = new CancellationTokenSource();
        _build = token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            if (toolThrows) token.ThrowIfCancellationRequested();
            return Task.FromResult((0, ""));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CreateIsoAsync(Media, Output, cancellationToken: cancellation.Token));
        AssertPreserved();
    }

    [Fact]
    public async Task CancellationBeforePublicationPreservesThePreviousIso()
    {
        using var cancellation = new CancellationTokenSource();
        _files.Setup(files => files.GetFileSize(It.IsAny<string>())).Returns((string path) =>
        {
            if (path == _stagedPath) cancellation.Cancel();
            return ValidSize;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CreateIsoAsync(Media, Output, cancellationToken: cancellation.Token));
        AssertPreserved();
    }

    [Fact]
    public async Task AlreadyCancelledCreationDoesNotStartTheToolOrMutateFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CreateIsoAsync(Media, Output, cancellationToken: cancellation.Token));
        AssertPreserved();
        _tool.Verify(tool => tool.IsOscdimgAvailableAsync(), Times.Never);
        _runner.Verify(runner => runner.RunProcessWithProgressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<TaskProgressDetail>?>(), It.IsAny<CancellationToken>()), Times.Never);
        _files.Verify(files => files.DeleteFile(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationFailurePreservesExistingDataAndCleansTheTemporaryIso(bool replacing)
    {
        if (replacing) _files.Setup(files => files.ReplaceFile(It.IsAny<string>(), Output)).Throws(new IOException("Destination is locked."));
        else
        {
            _contents.Remove(Output);
            _files.Setup(files => files.MoveFile(It.IsAny<string>(), Output)).Throws(new IOException("Destination appeared while building."));
        }
        Assert.False(await _service.CreateIsoAsync(Media, Output));
        Assert.False(_contents.ContainsKey(_stagedPath!));
        Assert.Equal(replacing, _contents.ContainsKey(Output));
        if (replacing) Assert.Equal("Previous valid ISO", _contents[Output]);
        _files.Verify(files => files.DeleteFile(Output), Times.Never);
    }

    [Fact]
    public async Task FailedTemporaryCleanupReportsItsPathAndPreservesThePreviousIso()
    {
        _build = _ => Task.FromResult((1, ""));
        _files.Setup(files => files.DeleteFile(It.IsAny<string>())).Throws(new IOException("Temporary file is locked."));
        Assert.False(await _service.CreateIsoAsync(Media, Output));
        Assert.Equal("Previous valid ISO", _contents[Output]);
        _log.Verify(log => log.LogWarning(It.Is<string>(message => message.Contains(_stagedPath!) && message.Contains("locked"))), Times.Once);
    }

    /// <summary>Checks receipts and retained data rather than only the process return code.</summary>
    private void AssertPreserved()
    {
        Assert.Equal("Previous valid ISO", _contents[Output]);
        if (_stagedPath is not null) Assert.False(_contents.ContainsKey(_stagedPath));
        _files.Verify(files => files.DeleteFile(Output), Times.Never);
        _files.Verify(files => files.ReplaceFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _files.Verify(files => files.MoveFile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private sealed class InlineProgress(Action<TaskProgressDetail> report) : IProgress<TaskProgressDetail>
    {
        public void Report(TaskProgressDetail value) => report(value);
    }
}
