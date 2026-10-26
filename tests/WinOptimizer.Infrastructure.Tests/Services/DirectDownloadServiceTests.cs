using System.Net;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises the public download/install flow with local bytes and a fake installer boundary.</summary>
public sealed class DirectDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinOptimizer-download-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IFileSystemService> _files = new();
    private readonly Mock<IProcessExecutor> _processes = new();
    private readonly HttpClient _http = new(new DownloadHandler());
    private static ItemDefinition App => new()
    {
        Id = "fixture", Name = "Fixture", Description = "Fixture", ExternalApp = new() { DownloadUrl = "https://fixture.invalid/setup.exe" }
    };

    public DirectDownloadServiceTests()
    {
        Directory.CreateDirectory(_root);
        _files.Setup(fs => fs.GetTempPath()).Returns(_root);
        _files.Setup(fs => fs.CombinePath(It.IsAny<string[]>())).Returns((string[] parts) => Path.Combine(parts));
        _files.Setup(fs => fs.GetFileName(It.IsAny<string>())).Returns((string path) => Path.GetFileName(path));
        _files.Setup(fs => fs.GetExtension(It.IsAny<string>())).Returns((string path) => Path.GetExtension(path));
        _files.Setup(fs => fs.CreateDirectory(It.IsAny<string>())).Callback((string path) => Directory.CreateDirectory(path));
    }

    private DirectDownloadService CreateService() => new(Mock.Of<ILogService>(), Mock.Of<ILocalizationService>(),
        Mock.Of<IInteractiveUserService>(), _processes.Object, _files.Object, _http);

    [Fact]
    public async Task CancellationDuringSilentAttemptNeverRetriesOrLaunchesTheGui()
    {
        using var cancellation = new CancellationTokenSource();
        _processes.Setup(process => process.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), cancellation.Token))
            .Callback(() => cancellation.Cancel()).ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().DownloadAndInstallAsync(App, cancellationToken: cancellation.Token));

        _processes.Verify(process => process.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), cancellation.Token), Times.Once);
        _processes.Verify(process => process.ShellExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    [InlineData(1603, false)]
    [InlineData(null, false)]
    public async Task GuiFallbackWaitsAndUsesTheActualInstallerResult(int? exitCode, bool expected)
    {
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _processes.Setup(process => process.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult { ExitCode = 1 });
        _processes.Setup(process => process.ShellExecuteAsync(It.IsAny<string>(), null, true, CancellationToken.None))
            .Callback(() => launched.SetResult()).Returns(completed.Task);

        var pending = CreateService().DownloadAndInstallAsync(App);
        await launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        var downloaded = Assert.Single(Directory.GetFiles(_root, "setup.exe", SearchOption.AllDirectories));
        Assert.True(File.Exists(downloaded));
        completed.SetResult(exitCode);

        Assert.Equal(expected, await pending);
        _processes.Verify(process => process.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class DownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
    }
}
