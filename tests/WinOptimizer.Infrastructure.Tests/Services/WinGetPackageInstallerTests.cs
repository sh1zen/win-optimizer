using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises package commands through a fake interactive-user process boundary.</summary>
public class WinGetPackageInstallerTests : IDisposable
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winget-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WinGetComSession _comSession;
    private readonly WinGetPackageInstaller _sut;

    public WinGetPackageInstallerTests()
    {
        var executable = Path.Combine(_root, "Microsoft", "WindowsApps", "winget.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "process boundary fixture");
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(true);
        _mockInteractiveUserService.Setup(s => s.HasInteractiveUserToken).Returns(true);
        _mockInteractiveUserService.Setup(s => s.GetInteractiveUserFolderPath(Environment.SpecialFolder.LocalApplicationData)).Returns(_root);
        _comSession = new WinGetComSession(_mockLogService.Object);

        _sut = new WinGetPackageInstaller(
            _comSession,
            _mockTaskProgressService.Object,
            _mockLogService.Object,
            _mockLocalization.Object,
            _mockInteractiveUserService.Object,
            _mockFileSystemService.Object);

        // Default setup: localization returns the key as-is
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);
        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>(), It.IsAny<object[]>()))
            .Returns((string key, object[] args) => key);
    }

    // --- IsWinGetInstalledAsync ---

    [Fact]
    public async Task IsWinGetInstalledAsync_WhenExeExistsOnFileSystem_ReturnsTrue()
    {
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        (await _sut.IsWinGetInstalledAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsWinGetInstalledAsync_WhenExeDoesNotExist_FallsBackToCom()
    {
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _comSession.ComInitTimedOut = true;
        (await _sut.IsWinGetInstalledAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsWinGetInstalledAsync_WhenCancelled_ReturnsFalseOrThrows()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _comSession.ComInitTimedOut = true;

        // Act & Assert: should return false (catches all exceptions)
        // or might throw OperationCanceledException depending on timing
        try
        {
            var result = await _sut.IsWinGetInstalledAsync(cts.Token);
            // Method catches all exceptions and returns false
            result.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
            // Acceptable: cancellation may propagate before the catch block
        }
    }

    // --- InstallPackageAsync ---

    [Fact]
    public async Task SuccessfulUninstallExitWithPersistentExactPresenceIsFailure()
    {
        var queries = 0;
        _mockFileSystemService.Setup(fs => fs.FileExists(It.IsAny<string>())).Returns(true);
        _mockInteractiveUserService.Setup(user => user.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<Action<string>?>()))
            .Returns((string _, string args, Action<string>? _, Action<string>? _, CancellationToken _, int _, Action<string>? _) =>
            {
                if (args.StartsWith("list ", StringComparison.Ordinal))
                {
                    Assert.Contains("--exact --id Test.Persistent ", args);
                    queries++;
                }
                return Task.FromResult(new InteractiveProcessResult(0, "Test.Persistent is installed", ""));
            });

        Assert.False(await _sut.UninstallPackageAsync("Test.Persistent"));

        Assert.Equal(21, queries);
        _mockTaskProgressService.Verify(progress => progress.UpdateProgress(100, It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task InstallPackageAsync_WithNullOrEmptyPackageId_ThrowsArgumentException(string? packageId)
    {
        // Act & Assert
        var act = () => _sut.InstallPackageAsync(packageId!);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("packageId");
    }

    [Fact]
    public async Task InstallPackageAsync_WhenWinGetNotInstalled_ReturnsWinGetNotAvailable()
    {
        // Arrange: ensure IsWinGetInstalledAsync returns false
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _comSession.ComInitTimedOut = true;

        // Act: this will call IsWinGetInstalledAsync which should return false
        // when winget is not on the system
        var result = await _sut.InstallPackageAsync("Test.Package");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(InstallFailureReason.WinGetNotAvailable);
        result.ErrorMessage.Should().Contain("WinGet CLI not found");
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "My App")]
    [InlineData(true, null)]
    [InlineData(true, "My App")]
    public async Task PackageCommand_ReportsPrerequisitesWithTheEffectiveDisplayName(bool uninstall, string? displayName)
    {
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _comSession.ComInitTimedOut = true;
        if (uninstall) await _sut.UninstallPackageAsync("Test.Package", displayName: displayName);
        else await _sut.InstallPackageAsync("Test.Package", displayName: displayName);
        _mockTaskProgressService.Verify(t => t.UpdateProgress(10, It.IsAny<string>()), Times.Once);
        _mockLocalization.Verify(l => l.GetString(uninstall
            ? "Progress_WinGet_CheckingPrerequisitesUninstall" : "Progress_WinGet_CheckingPrerequisites",
            It.Is<object[]>(args => args.Length == 1 && Equals(args[0], displayName ?? "Test.Package"))), Times.Once);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, unchecked((int)0x8A150030))]
    public async Task PackageCommand_PreservesProgressTranscriptAndRemovalVerification(bool uninstall, int exitCode)
    {
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        var details = new List<TaskProgressDetail>();
        _mockTaskProgressService.Setup(t => t.UpdateDetailedProgress(It.IsAny<TaskProgressDetail>()))
            .Callback<TaskProgressDetail>(details.Add);
        _mockInteractiveUserService.Setup(s => s.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<Action<string>?>()))
            .Returns((string file, string arguments, Action<string>? stdout, Action<string>? stderr, CancellationToken ct, int timeout, Action<string>? transient) =>
            {
                if (arguments.StartsWith("list ")) return Task.FromResult(new InteractiveProcessResult(WinGetExitCodes.PackageNotFound, "", ""));
                stdout?.Invoke(uninstall ? "Uninstalling package..." : "Installing package...");
                stdout?.Invoke("native detail");
                stderr?.Invoke("native warning");
                transient?.Invoke("52%");
                stdout?.Invoke("█████ 100%");
                stdout?.Invoke(uninstall ? "Successfully uninstalled" : "Successfully installed");
                return Task.FromResult(new InteractiveProcessResult(exitCode, "", ""));
            });
        var success = uninstall ? await _sut.UninstallPackageAsync("Test.Package", displayName: "My App")
            : (await _sut.InstallPackageAsync("Test.Package", displayName: "My App")).Success;
        Assert.True(success);
        Assert.StartsWith("Command: ", details[0].TerminalOutput);
        Assert.StartsWith("Start Time: ", details[1].TerminalOutput);
        Assert.Equal("---", details[2].TerminalOutput);
        Assert.Equal(uninstall ? 60 : 70, details[3].Progress);
        Assert.Equal(uninstall ? "Progress_Uninstalling" : "Progress_Installing", details[3].StatusText);
        Assert.Contains(details, detail => detail.TerminalOutput == "native detail");
        Assert.Contains(details, detail => detail.TerminalOutput == "native warning");
        Assert.Contains(details, detail => detail.TerminalOutput == "52%" && detail.IsProgressIndicator);
        Assert.DoesNotContain(details, detail => detail.TerminalOutput == "█████ 100%");
        Assert.Contains(details, detail => detail.Progress == 100 && detail.StatusText ==
            (uninstall ? "Progress_WinGet_UninstalledSuccess" : "Progress_WinGet_InstalledSuccess"));
        Assert.Equal("---", details[^3].TerminalOutput);
        Assert.StartsWith("End Time: ", details[^2].TerminalOutput);
        Assert.Equal($"Process return value: \"{exitCode}\" (0x{exitCode:X8})", details[^1].TerminalOutput);
        _mockInteractiveUserService.Verify(s => s.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), It.Is<string>(args => args.StartsWith("list ")),
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), Timeout.Infinite, It.IsAny<Action<string>?>()),
            uninstall ? Times.Once() : Times.Never());
        _mockInteractiveUserService.Verify(s => s.RunProcessAsInteractiveUserAsync(It.IsAny<string>(),
            It.Is<string>(args => args.StartsWith(uninstall ? "uninstall " : "install ")),
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), Timeout.Infinite,
            It.IsAny<Action<string>?>()), Times.Once);
    }

    /// <summary>Exercises the CLI adapter's timeout contract without starting a native process.</summary>
    [Theory]
    [InlineData(0, Timeout.Infinite)]
    [InlineData(Timeout.Infinite, Timeout.Infinite)]
    [InlineData(10000, Timeout.Infinite)]
    public async Task CliTimeoutPreservesUnlimitedAndBoundedInteractiveExecution(int requested, int expected)
    {
        _mockInteractiveUserService.Setup(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), "test",
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), expected, It.IsAny<Action<string>?>()))
            .ReturnsAsync(new InteractiveProcessResult(0, "done", ""));

        var result = await WinGetCliRunner.RunAsync("test", timeoutMs: requested, interactiveUserService: _mockInteractiveUserService.Object);

        Assert.Equal("done", result.StandardOutput);
        _mockInteractiveUserService.Verify(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), "test",
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), expected, It.IsAny<Action<string>?>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 50, WinGetCliRunner.TerminationReason.IdleTimeout, false)]
    [InlineData(0, 50, WinGetCliRunner.TerminationReason.IdleTimeout, true)]
    [InlineData(50, 0, WinGetCliRunner.TerminationReason.WallClockTimeout, false)]
    [InlineData(50, 0, WinGetCliRunner.TerminationReason.WallClockTimeout, true)]
    public async Task InteractiveCommandsEnforceTheSharedDeadline(int wallTimeout, int idleTimeout, WinGetCliRunner.TerminationReason expected, bool throwsCancellation)
    {
        var clock = new DeadlineClock();
        _mockInteractiveUserService.Setup(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), "test",
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), Timeout.Infinite, It.IsAny<Action<string>?>()))
            .Returns((string file, string args, Action<string>? output, Action<string>? error, CancellationToken ct, int timeout, Action<string>? progress) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(Math.Max(wallTimeout, idleTimeout)));
                Assert.True(ct.IsCancellationRequested);
                if (throwsCancellation) ct.ThrowIfCancellationRequested();
                return Task.FromResult(new InteractiveProcessResult(-1, "partial", ""));
            });

        var result = await WinGetCliRunner.RunAsync("test", timeoutMs: wallTimeout, idleTimeoutMs: idleTimeout,
            interactiveUserService: _mockInteractiveUserService.Object, timeProvider: clock);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(expected, result.Termination);
    }

    [Fact]
    public async Task InteractiveOutputCannotRenewTheWallClockDeadline()
    {
        var clock = new DeadlineClock();
        _mockInteractiveUserService.Setup(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), "test",
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), Timeout.Infinite, It.IsAny<Action<string>?>()))
            .Returns((string file, string args, Action<string>? output, Action<string>? error, CancellationToken ct, int timeout, Action<string>? progress) =>
            {
                for (var index = 0; index < 10; index++)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(40));
                    ct.ThrowIfCancellationRequested();
                    output!("tick");
                }
                return Task.FromResult(new InteractiveProcessResult(0, "", ""));
            });

        var result = await WinGetCliRunner.RunAsync("test", timeoutMs: 200, idleTimeoutMs: 1000,
            interactiveUserService: _mockInteractiveUserService.Object, timeProvider: clock);

        Assert.Equal(WinGetCliRunner.TerminationReason.WallClockTimeout, result.Termination);
    }

    [Theory]
    [InlineData("output", false)]
    [InlineData("error", false)]
    [InlineData("progress", false)]
    [InlineData("output", true)]
    [InlineData("error", true)]
    [InlineData("progress", true)]
    public async Task AllInteractiveOutputChannelsRenewTheIdleDeadline(string channel, bool outputStops)
    {
        var clock = new DeadlineClock();
        List<string> received = [];
        _mockInteractiveUserService.Setup(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), "test",
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), Timeout.Infinite, It.IsAny<Action<string>?>()))
            .Returns((string file, string args, Action<string>? output, Action<string>? error, CancellationToken ct, int timeout, Action<string>? progress) =>
            {
                var report = channel == "output" ? output : channel == "error" ? error : progress;
                for (var index = 0; index < 8; index++)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(160));
                    ct.ThrowIfCancellationRequested();
                    report!("tick");
                }
                if (outputStops)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(999));
                    ct.ThrowIfCancellationRequested();
                    clock.Advance(TimeSpan.FromMilliseconds(1));
                    ct.ThrowIfCancellationRequested();
                }
                return Task.FromResult(new InteractiveProcessResult(0, "done", ""));
            });

        var result = await WinGetCliRunner.RunAsync("test", received.Add, received.Add, timeoutMs: 0, idleTimeoutMs: 1000,
            interactiveUserService: _mockInteractiveUserService.Object, onProgressLine: received.Add, timeProvider: clock);

        Assert.Equal(outputStops ? -1 : 0, result.ExitCode);
        Assert.Equal(outputStops ? WinGetCliRunner.TerminationReason.IdleTimeout : WinGetCliRunner.TerminationReason.None, result.Termination);
        Assert.Equal(8, received.Count);
    }

    /// <summary>Advances one-shot command timers explicitly, independently of thread scheduling.</summary>
    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<DeadlineTimer> _timers = [];
        private TimeSpan _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DeadlineTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers) timer.FireIfDue();
        }

        /// <summary>Tracks one-shot cancellation deadlines and stops firing after disposal.</summary>
        private sealed class DeadlineTimer(DeadlineClock clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan? _due;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Command deadlines use one-shot timers.");
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._elapsed + dueTime;
                return true;
            }

            public void FireIfDue()
            {
                if (_due is not { } due || due > clock._elapsed) return;
                _due = null;
                callback(state);
            }

            public void Dispose() { _disposed = true; _due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task InteractiveCallerCancellationHasItsOwnTerminationReason()
    {
        using var cancellation = new CancellationTokenSource();
        _mockInteractiveUserService.Setup(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), "test",
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), Timeout.Infinite, It.IsAny<Action<string>?>()))
            .Returns((string file, string args, Action<string>? output, Action<string>? error, CancellationToken ct, int timeout, Action<string>? progress) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new InteractiveProcessResult(-1, "partial", ""));
            });

        var result = await WinGetCliRunner.RunAsync("test", cancellationToken: cancellation.Token,
            interactiveUserService: _mockInteractiveUserService.Object);

        Assert.Equal(WinGetCliRunner.TerminationReason.Cancelled, result.Termination);
        Assert.Equal("partial", result.StandardOutput);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(0, WinGetExitCodes.FailedToOpenAllSources)]
    [InlineData(WinGetExitCodes.ExecUninstallCommandFailed, 1)]
    [InlineData(WinGetExitCodes.ExecUninstallCommandFailed, -1)]
    [InlineData(WinGetExitCodes.ExecUninstallCommandFailed, WinGetExitCodes.FailedToOpenAllSources)]
    [InlineData(WinGetExitCodes.ExecUninstallCommandFailed, unchecked((int)0x8A150013))]
    public async Task VerificationErrorsCannotProveRemoval(int uninstallExit, int inventoryExit)
    {
        SetupUninstallVerification(uninstallExit, _ => Task.FromResult(new InteractiveProcessResult(inventoryExit, "", "")));

        Assert.False(await _sut.UninstallPackageAsync("Test.Package"));

        _mockTaskProgressService.Verify(service => service.UpdateProgress(100, It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WinGetExitCodes.ExecUninstallCommandFailed)]
    public async Task UnreadableVerificationCannotProveRemoval(int uninstallExit)
    {
        SetupUninstallVerification(uninstallExit, _ => Task.FromException<InteractiveProcessResult>(new IOException("Inventory unavailable")));

        Assert.False(await _sut.UninstallPackageAsync("Test.Package"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationCancellationPropagatesEvenIfTheProcessReturnsAbsence(bool throwCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        SetupUninstallVerification(WinGetExitCodes.ExecUninstallCommandFailed, token =>
        {
            cancellation.Cancel();
            return throwCancellation ? Task.FromCanceled<InteractiveProcessResult>(token)
                : Task.FromResult(new InteractiveProcessResult(WinGetExitCodes.PackageNotFound, "", ""));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.UninstallPackageAsync("Test.Package", cancellationToken: cancellation.Token));

        _mockTaskProgressService.Verify(service => service.UpdateProgress(100, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerificationUsesAnExactPackageIdentityAndRetainsTheSource()
    {
        SetupUninstallVerification(WinGetExitCodes.ExecUninstallCommandFailed,
            _ => Task.FromResult(new InteractiveProcessResult(WinGetExitCodes.PackageNotFound, "", "")));

        Assert.True(await _sut.UninstallPackageAsync("Test.Package", source: "winget"));

        _mockInteractiveUserService.Verify(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(),
            "list --exact --id Test.Package --accept-source-agreements --disable-interactivity --source winget",
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), Timeout.Infinite, It.IsAny<Action<string>?>()), Times.Once);
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150019))]
    [InlineData(unchecked((int)0x8A150015))]
    [InlineData(unchecked((int)0x8A15002A))]
    public async Task GenuineCliErrorsCannotReportSuccessfulInstallation(int exitCode)
    {
        SetupUninstallVerification(exitCode, _ => throw new InvalidOperationException("Installation must not query uninstall status"));

        Assert.False((await _sut.InstallPackageAsync("Test.Package")).Success);

        _mockTaskProgressService.Verify(service => service.UpdateProgress(100, It.IsAny<string>()), Times.Never);
    }

    /// <summary>Routes uninstall and subsequent inventory commands through the fake process boundary.</summary>
    private void SetupUninstallVerification(int uninstallExit, Func<CancellationToken, Task<InteractiveProcessResult>> inventory)
    {
        _mockFileSystemService.Setup(service => service.FileExists(It.IsAny<string>())).Returns(true);
        _mockInteractiveUserService.Setup(service => service.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>?>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<int>(), It.IsAny<Action<string>?>()))
            .Returns((string file, string arguments, Action<string>? stdout, Action<string>? stderr, CancellationToken ct, int timeout, Action<string>? progress) =>
                arguments.StartsWith("list ") ? inventory(ct) : Task.FromResult(new InteractiveProcessResult(uninstallExit, "", "")));
    }

    // --- UninstallPackageAsync ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task UninstallPackageAsync_WithNullOrEmptyPackageId_ThrowsArgumentException(string? packageId)
    {
        // Act & Assert
        var act = () => _sut.UninstallPackageAsync(packageId!);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("packageId");
    }

    [Fact]
    public async Task UninstallPackageAsync_WhenWinGetNotInstalled_ReturnsFalse()
    {
        // Arrange: ensure IsWinGetInstalledAsync returns false
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        _mockFileSystemService.Setup(f => f.FileExists(It.IsAny<string>())).Returns(false);
        _comSession.ComInitTimedOut = true;

        // Act
        var result = await _sut.UninstallPackageAsync("Test.Package");

        result.Should().BeFalse();
        _mockTaskProgressService.Verify(t => t.UpdateProgress(0, It.IsAny<string>()), Times.AtLeastOnce);
    }

    // --- PackageInstallResult model tests ---

    [Fact]
    public void PackageInstallResult_Succeeded_HasCorrectProperties()
    {
        var result = PackageInstallResult.Succeeded();

        result.Success.Should().BeTrue();
        result.FailureReason.Should().Be(InstallFailureReason.None);
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void PackageInstallResult_Failed_HasCorrectProperties()
    {
        var result = PackageInstallResult.Failed(InstallFailureReason.PackageNotFound, "Not found");

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(InstallFailureReason.PackageNotFound);
        result.ErrorMessage.Should().Be("Not found");
    }
    /// <summary>Removes the fixture executable placeholder; no native process is launched.</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
