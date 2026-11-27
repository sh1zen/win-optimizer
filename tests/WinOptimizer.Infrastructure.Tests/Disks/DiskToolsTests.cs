using System.Security.Cryptography;
using System.Xml.Linq;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Disks;
using WinOptimizer.Infrastructure.Features.Disks;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Disks;

public sealed class DiskToolsTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("win-optimizer-disks-");
    private readonly Mock<IProcessExecutor> _processes = new(MockBehavior.Strict);
    private readonly TestShadowCatalog _shadow = new();
    private Func<string, IFileLockSession> _openLockSession = path =>
        throw new InvalidOperationException($"Unexpected lock session for '{path}'.");
    private WindowsDiskToolsService Service => new(
        _processes.Object, new ShadowVersions(_shadow), new DiskFileDeletion(_openLockSession));

    [Theory]
    [InlineData(@"C:\folder\..\file.txt")]
    [InlineData(@"C:\folder\file.txt:stream")]
    [InlineData(@"C:\folder\NUL.txt")]
    [InlineData(@"C:\folder\a*.txt")]
    [InlineData(@"C:\folder\name.")]
    [InlineData(@"\\?\C:\file")]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"\\\")]
    [InlineData(@"C:file.txt")]
    [InlineData(@"C:\")]
    public void RejectsAmbiguousOrNonlocalPaths(string input) =>
        Assert.Throws<ArgumentException>(() => DiskPaths.LocalPath(input));

    [Fact]
    public async Task CalculatesAndComparesFileSha256()
    {
        var path = Path.Join(_temporary.FullName, "file.txt");
        var data = "test input"u8.ToArray();
        await File.WriteAllBytesAsync(path, data);
        var expected = Convert.ToHexString(SHA256.HashData(data));

        Assert.Equal(new DiskHash(expected, true), await Service.HashAsync(path, expected.ToLowerInvariant(), default));
        Assert.Equal(new DiskHash(expected, false), await Service.HashAsync(path, new string('0', 64), default));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.HashAsync(path, "not a SHA-256", default));
    }

    [Fact]
    public async Task FolderDigestUsesRelativeNamesContentAndEmptyDirectories()
    {
        var first = _temporary.CreateSubdirectory("first").FullName;
        var second = _temporary.CreateSubdirectory("second").FullName;
        Directory.CreateDirectory(Path.Join(first, "empty"));
        await File.WriteAllTextAsync(Path.Join(first, "file.txt"), "data");
        await File.WriteAllTextAsync(Path.Join(second, "file.txt"), "data");
        Directory.CreateDirectory(Path.Join(second, "empty"));

        var original = await Service.HashAsync(first, null, default);
        Assert.Equal(original, await Service.HashAsync(second, null, default));
        Directory.Delete(Path.Join(second, "empty"));
        Assert.NotEqual(original, await Service.HashAsync(second, null, default));

        Directory.CreateDirectory(Path.Join(second, "empty"));
        File.Move(Path.Join(second, "file.txt"), Path.Join(second, "renamed.txt"));
        Assert.NotEqual(original, await Service.HashAsync(second, null, default));
        File.Move(Path.Join(second, "renamed.txt"), Path.Join(second, "file.txt"));
        await File.WriteAllTextAsync(Path.Join(second, "file.txt"), "changed data");
        Assert.NotEqual(original, await Service.HashAsync(second, null, default));
    }

    [Fact]
    public async Task DeletesOnlyTheChosenReadOnlyFile()
    {
        var target = Path.Join(_temporary.FullName, "selected.txt");
        var neighbor = Path.Join(_temporary.FullName, "neighbor.txt");
        await File.WriteAllTextAsync(target, "delete");
        await File.WriteAllTextAsync(neighbor, "keep");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            await Service.DeleteFileAsync(target, default);
            Assert.False(File.Exists(target));
            Assert.Equal("keep", await File.ReadAllTextAsync(neighbor));
        }
        finally
        {
            if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task UnlockingAnUnusedFilePreservesItsContents()
    {
        var target = Path.Join(_temporary.FullName, "unused.txt");
        await File.WriteAllTextAsync(target, "keep");
        var session = SetUpLockSession(target);

        Assert.False(await Service.UnlockFileAsync(target, default));
        Assert.Equal("keep", await File.ReadAllTextAsync(target));
        Assert.Equal(1, session.OpenCount);
        Assert.Equal(1, session.ReadCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task ReportsTheLockOwnerAndRefusesToCloseItsOwnProcess()
    {
        var target = Path.Join(_temporary.FullName, "locked.txt");
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        var lockOwner = new DiskProcess(Environment.ProcessId, "Test host");
        var session = SetUpLockSession(target, lockOwner);

        var owners = await Service.GetFileLocksAsync(target, default);
        Assert.Equal(lockOwner, Assert.Single(owners));
        await Assert.ThrowsAsync<IOException>(() => Service.UnlockFileAsync(target, default));
        Assert.True(File.Exists(target));
        Assert.Equal(2, session.OpenCount);
        Assert.Equal(2, session.ReadCount);
        Assert.Equal(2, session.DisposeCount);
    }

    [Fact]
    public async Task PermissionResetUsesTheExactSelectedPathAndReportsToolErrors()
    {
        var folder = _temporary.CreateSubdirectory("folder with spaces").FullName;
        _processes.Setup(processes => processes.ExecuteAsync(Path.Join(Environment.SystemDirectory, "icacls.exe"),
            $"\"{folder}\" /reset /L /Q", default)).ReturnsAsync(new ProcessExecutionResult { ExitCode = 5, StandardError = "ACL denied" });

        var error = await Assert.ThrowsAsync<IOException>(() => Service.RepairPermissionsAsync(folder, default));

        Assert.Contains("ACL denied", error.Message);
        _processes.VerifyAll();
    }

    [Fact]
    public async Task RestoresAMissingFolderFromTheSelectedShadowCopyWithoutOverwriting()
    {
        var destination = _temporary.CreateSubdirectory("destination").FullName;
        var original = Path.Join(_temporary.FullName, "documents");
        var volume = Path.GetPathRoot(original)!;
        var snapshotRoot = _temporary.CreateSubdirectory("snapshot").FullName + Path.DirectorySeparatorChar;
        var snapshotFolder = Directory.CreateDirectory(Path.Join(snapshotRoot, Path.GetRelativePath(volume, original))).FullName;
        await File.WriteAllTextAsync(Path.Join(snapshotFolder, "notes.txt"), "earlier content");
        Directory.CreateDirectory(Path.Join(snapshotFolder, "empty"));
        var copy = new ShadowCopy(Guid.NewGuid(), new DateTime(2026, 1, 1), snapshotRoot);
        _shadow.Copy = copy;
        _shadow.VolumeId = DiskPaths.VolumeId(volume);

        var version = Assert.Single(await Service.GetShadowVersionsAsync(original, default));
        Assert.Equal(new DiskShadowVersion(copy.Id, copy.Created, true), version);
        string restored = await Service.RestoreShadowVersionAsync(original, version, destination, default);
        Assert.Equal(Path.Join(destination, "documents"), restored);
        Assert.True(Directory.Exists(Path.Join(restored, "empty")));
        Assert.Equal("earlier content", await File.ReadAllTextAsync(Path.Join(restored, "notes.txt")));
        await Assert.ThrowsAsync<IOException>(() => Service.RestoreShadowVersionAsync(original, version, destination, default));
        Assert.Equal(restored, Assert.Single(Directory.EnumerateFileSystemEntries(destination)));
    }

    [Fact]
    public async Task AnalysisAndOptimizationUseWindowsMediaAwareMode()
    {
        var root = Path.GetPathRoot(_temporary.FullName)!;
        var drive = root[..2];
        var tool = Path.Join(Environment.SystemDirectory, "defrag.exe");
        _processes.Setup(processes => processes.ExecuteAsync(tool, $"{drive} /A /V", default))
            .ReturnsAsync(new ProcessExecutionResult { StandardOutput = "analysis" });
        _processes.Setup(processes => processes.ExecuteAsync(tool, $"{drive} /O /U /V", default))
            .ReturnsAsync(new ProcessExecutionResult { StandardOutput = "optimization" });

        Assert.Equal("analysis", await Service.OptimizeAsync(root, analyzeOnly: true, default));
        Assert.Equal("optimization", await Service.OptimizeAsync(root, analyzeOnly: false, default));
        _processes.VerifyAll();
    }

    [Fact]
    public void RecoveryArgumentsPreserveSpacesAndTrailingBackslash()
    {
        Assert.Equal("C: E: /extensive /n \"\\Users\\Name\\My Photos\\\\\"",
            DiskFileRecovery.Arguments(@"C:\", @"E:\", DiskRecoveryMode.Extensive, @"\Users\Name\My Photos\"));
        Assert.Equal("C: E: /regular", DiskFileRecovery.Arguments(@"C:\", @"E:\", DiskRecoveryMode.Regular, null));
    }

    [Theory]
    [InlineData(@"/n other")]
    [InlineData(@"C:\file.txt")]
    [InlineData(@"\Users\..\file.txt")]
    [InlineData("a\" /extensive")]
    public void RecoveryRejectsFiltersThatCanAlterTheCommand(string filter) =>
        Assert.Throws<ArgumentException>(() => DiskFileRecovery.Arguments(@"C:\", @"E:\", DiskRecoveryMode.Regular, filter));

    [Fact]
    public async Task RecoveryRejectsTheSourceVolumeAsDestinationBeforeLaunching() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Service.RecoverDeletedFilesAsync(@"C:\", @"c:\",
            DiskRecoveryMode.Regular, null, default));

    [Fact]
    public void RecoveryResolvesTheCliFromTheRegisteredPackageManifest()
    {
        const string manifest = """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
                xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
              <Applications>
                <Application Executable="other.exe">
                  <Extensions>
                    <uap3:Extension Category="windows.appExecutionAlias" Executable="cli\WinFR.exe">
                      <uap3:AppExecutionAlias>
                        <desktop:ExecutionAlias Alias="winfr.exe" />
                      </uap3:AppExecutionAlias>
                    </uap3:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;
        Assert.Equal(Path.Combine(_temporary.FullName, "cli", "WinFR.exe"),
            DiskFileRecovery.ResolveExecutable(_temporary.FullName, XDocument.Parse(manifest)));
        Assert.Throws<IOException>(() => DiskFileRecovery.ResolveExecutable(_temporary.FullName,
            XDocument.Parse(manifest.Replace(@"cli\WinFR.exe", @"..\WinFR.exe", StringComparison.Ordinal))));
    }

    [Fact]
    public void ReadsUsbBlockerOnlyForTheSelectedDevice()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <EventData>
                <Data Name="ProcessId">1234</Data>
                <Data Name="ProcessName">\Device\HarddiskVolume1\apps\editor.exe</Data>
                <Data Name="DeviceInstance">USB\VID_1234</Data>
              </EventData>
            </Event>
            """;
        Assert.Equal(new DiskProcess(1234, "editor.exe"), UsbDiskEjection.ReadBlocker(xml, @"usb\vid_1234"));
        Assert.Null(UsbDiskEjection.ReadBlocker(xml, @"usb\vid_5555"));
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    /// <summary>Provides deterministic lock data for the selected file without opening a native session.</summary>
    private TestLockSession SetUpLockSession(string target, params DiskProcess[] owners)
    {
        var session = new TestLockSession(target, owners);
        _openLockSession = session.Open;
        return session;
    }

    /// <summary>Tracks reads and disposal for the requested file and rejects shutdown in read-only cases.</summary>
    private sealed class TestLockSession(string target, IReadOnlyList<DiskProcess> owners) : IFileLockSession
    {
        public int OpenCount { get; private set; }
        public int ReadCount { get; private set; }
        public int DisposeCount { get; private set; }

        public IFileLockSession Open(string path)
        {
            Assert.Equal(target, path);
            OpenCount++;
            return this;
        }

        public IReadOnlyList<DiskProcess> GetLocks()
        {
            ReadCount++;
            return owners;
        }

        public void ForceShutdown() => throw new InvalidOperationException("The operation must leave the process running.");
        public void Dispose() => DisposeCount++;
    }

    private sealed class TestShadowCatalog : IShadowCopyCatalog
    {
        public string? VolumeId { get; set; }
        public ShadowCopy? Copy { get; set; }

        public IEnumerable<ShadowCopy> List(string volumeId, CancellationToken ct)
        {
            Assert.Equal(VolumeId, volumeId);
            return [Assert.IsType<ShadowCopy>(Copy)];
        }
    }
}
