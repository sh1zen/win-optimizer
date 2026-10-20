using System.Runtime.InteropServices;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Exercises shortcut discovery, native target reading and cleanup against isolated Windows files.</summary>
public sealed class CleanerShortcutScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cleaner-shortcuts-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly Mock<ICleanerShortcutReader> _reader = new();
    private readonly TestCleanerRegistrySessions _accounts;
    private readonly CleanerFileScanner _scanner;

    public CleanerShortcutScanTests()
    {
        Directory.CreateDirectory(_root);
        _user.Setup(user => user.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns((Environment.SpecialFolder folder) => Path.Combine(_root, folder.ToString()));
        _accounts = new(_user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>());
        _scanner = CreateScanner(_reader.Object);
    }

    private CleanerFileScanner CreateScanner(ICleanerShortcutReader reader) => new(_accounts,
        Mock.Of<IAppxPackageSource>(), Mock.Of<ICleanerProcessMonitor>(), reader, Mock.Of<ICleanerSystemFolders>());

    private string Write(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "shortcut evidence");
        return path;
    }

    /// <summary>Simulates NTFS access-time updates deterministically during the normal reader call.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccessTimeChangesDoNotHideBrokenLinksOrPreventCleanup(bool backup)
    {
        var path = Write(@"DesktopDirectory\broken.lnk");
        var target = Path.Combine(_root, "missing.exe");
        // Shortcut repair has no age requirement, including files with future access timestamps.
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(1));
        _reader.Setup(reader => reader.ReadTarget(path)).Returns(() =>
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(2));
            return target;
        });

        var result = await _scanner.ScanAsync(CleanerArea.Shortcuts, new() { BackupBeforeCleaning = backup }, default);

        var finding = Assert.Single(result.Findings);
        result.Notices.Should().BeEmpty();
        var evidence = Assert.IsType<CleanerFileTarget>(finding.Target);
        File.GetLastAccessTimeUtc(path).Should().BeAfter(evidence.CutoffUtc);
        var backups = new CleanerBackupService(new TestCleanerBackupVault(Path.Combine(_root, "vault")),
            _scanner, Mock.Of<ICleanerRegistryRecovery>());
        var saved = backup ? await backups.CreateAsync([finding], default) : null;
        _scanner.Clean(evidence);
        File.Exists(path).Should().BeFalse();
        if (saved is not null)
        {
            var restored = await backups.RestoreAsync(saved.Id, default);
            restored.Errors.Should().BeEmpty();
            restored.Restored.Should().Be(1);
            File.ReadAllText(path).Should().Be("shortcut evidence");
        }
    }

    /// <summary>Checks the real COM reader with generated links, without installed programs or machine-local targets.</summary>
    [Fact]
    public async Task NativeShortcutReaderFindsMissingFilesAndFoldersAndPreservesValidLinks()
    {
        var valid = Write("existing.txt");
        var validFolder = Path.Combine(_root, "existing-folder");
        Directory.CreateDirectory(validFolder);
        var desktop = Path.Combine(_root, "DesktopDirectory");
        Directory.CreateDirectory(desktop);
        var missingFile = CreateLink(Path.Combine(desktop, "missing-file.lnk"), Path.Combine(_root, "missing.txt"));
        var missingFolder = CreateLink(Path.Combine(desktop, "missing-folder.lnk"), Path.Combine(_root, "missing-folder"));
        var validFileLink = CreateLink(Path.Combine(desktop, "valid-file.lnk"), valid);
        var validFolderLink = CreateLink(Path.Combine(desktop, "valid-folder.lnk"), validFolder);
        var scanner = CreateScanner(new WindowsCleanerShortcutReader());

        var result = await scanner.ScanAsync(CleanerArea.Shortcuts, new(), default);

        result.Notices.Should().BeEmpty();
        result.CheckedItems.Should().Be(4);
        result.Findings.Select(finding => finding.Detail).Should().BeEquivalentTo(missingFile, missingFolder);
        foreach (var finding in result.Findings) scanner.Clean((CleanerFileTarget)finding.Target);
        File.Exists(missingFile).Should().BeFalse();
        File.Exists(missingFolder).Should().BeFalse();
        File.Exists(validFileLink).Should().BeTrue();
        File.Exists(validFolderLink).Should().BeTrue();
        File.Exists(valid).Should().BeTrue();
        Directory.Exists(validFolder).Should().BeTrue();
    }

    [Fact]
    public async Task UnavailableDesktopDoesNotPreventStartMenuOrRecentDiscovery()
    {
        _user.Setup(user => user.GetInteractiveUserFolderPath(Environment.SpecialFolder.DesktopDirectory))
            .Throws(new IOException("Desktop unavailable"));
        var menu = Write(@"StartMenu\broken.lnk");
        var recent = Write(@"ApplicationData\Microsoft\Windows\Recent\broken.lnk");
        _reader.Setup(reader => reader.ReadTarget(It.IsAny<string>())).Returns(Path.Combine(_root, "missing.exe"));

        var result = await _scanner.ScanAsync(CleanerArea.Shortcuts, new(), default);

        result.Findings.Select(finding => finding.Detail).Should().BeEquivalentTo(menu, recent);
        result.Notices.Should().ContainSingle().Which.Should().Contain("Desktop unavailable");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedShortcutContentsOrTargetStillBlockCleanup(bool changeContents)
    {
        var path = Write(@"DesktopDirectory\broken.lnk");
        _reader.Setup(reader => reader.ReadTarget(path)).Returns(Path.Combine(_root, "missing.exe"));
        var result = await _scanner.ScanAsync(CleanerArea.Shortcuts, new(), default);
        var evidence = Assert.IsType<CleanerFileTarget>(Assert.Single(result.Findings).Target);
        if (changeContents) File.AppendAllText(path, "changed");
        else _reader.Setup(reader => reader.ReadTarget(path)).Returns(Path.Combine(_root, "different-missing.exe"));

        Assert.Throws<IOException>(() => _scanner.Clean(evidence));
        File.Exists(path).Should().BeTrue();
    }

    /// <summary>Creates real Shell links using Windows' scripting boundary and promptly releases its COM objects.</summary>
    private static string CreateLink(string path, string target)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            dynamic link = ((dynamic)shell).CreateShortcut(path);
            try { link.TargetPath = target; link.Save(); }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
