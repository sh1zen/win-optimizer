using System.Diagnostics;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Cleaner.Services;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;
using Xunit.Abstractions;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Measures the production scan/backup/delete path against isolated small-file workloads.</summary>
public sealed class CleanerFilePerformanceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cleaner-performance-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(CleanerArea.Privacy)]
    [InlineData(CleanerArea.JunkFiles)]
    public async Task SmallFileWorkloadPreservesEveryFindingAndVerifiedBackup(CleanerArea area)
    {
        const int count = 1000;
        var user = new Mock<IInteractiveUserService>();
        user.Setup(service => service.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(_root);
        var sessions = new WindowsCleanerRegistrySessions(user.Object, Mock.Of<ICleanerRegistryProfiles>(), Mock.Of<ICleanerRegistryHiveAccess>());
        var folders = new Mock<ICleanerSystemFolders>();
        folders.Setup(service => service.GetFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(Path.Combine(_root, "system"));
        var scanner = new CleanerFileScanner(sessions, Mock.Of<IAppxPackageSource>(), Mock.Of<ICleanerProcessMonitor>(),
            Mock.Of<ICleanerShortcutReader>(), folders.Object);
        var vault = new TestCleanerBackupVault(Path.Combine(_root, "vault"));
        var registry = new CleanerRegistryScanner(sessions, vault);
        var backups = new CleanerBackupService(vault, scanner, registry);
        var backend = new WindowsCleanerBackend(scanner, registry,
            new RegistryHiveCompactor(sessions, vault, Mock.Of<IRegistryHiveOperations>()), Mock.Of<IProcessExecutor>());
        var service = new CleanerService(backend, backups);
        var relative = area == CleanerArea.Privacy ? @"Google\Chrome\User Data\Default\Cache" : "Temp";
        var paths = Enumerable.Range(0, count).Select(index => Path.Combine(_root, relative, (index % 16).ToString(), index + ".tmp")).ToArray();
        var old = DateTime.UtcNow.AddDays(-30);
        foreach (var path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[512]);
            File.SetLastWriteTimeUtc(path, old);
            File.SetLastAccessTimeUtc(path, old);
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        double discoveryMilliseconds = 0;
        var scanProgress = new Mock<IProgress<CleanerProgress>>();
        scanProgress.Setup(progress => progress.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(value =>
        {
            if (value.Total == count && value.Completed == 0) discoveryMilliseconds = watch.Elapsed.TotalMilliseconds;
        });
        var scan = await service.ScanAsync(new() { WindowsHistory = false, ApplicationPrivacyCaches = false, CertificateCache = false }, [area], scanProgress.Object, default);
        var scanMilliseconds = watch.Elapsed.TotalMilliseconds;
        Assert.Empty(scan.Notices);
        Assert.Equal(count, scan.Findings.Count);
        Assert.All(scan.Findings, finding => Assert.True(finding.CanClean));
        watch.Restart();
        double backupMilliseconds = 0;
        var applyProgress = new Mock<IProgress<CleanerProgress>>();
        applyProgress.Setup(progress => progress.Report(It.IsAny<CleanerProgress>())).Callback<CleanerProgress>(value =>
        {
            if (value.Total == count && value.Completed == 0) backupMilliseconds = watch.Elapsed.TotalMilliseconds;
        });
        var applied = await service.ApplyAsync(scan.Findings.Select(finding => finding.Id).ToArray(), applyProgress.Object, default);
        var applyMilliseconds = watch.Elapsed.TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Assert.Empty(applied.Errors);
        Assert.Equal(count, applied.Completed);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(count, Assert.Single(await backups.ListAsync(default)).FileCount);
        output.WriteLine($"{area}: {count} files; scan {scanMilliseconds:F1} ms; verified backup + cleanup {applyMilliseconds:F1} ms; allocated {allocatedBytes / 1048576d:F1} MiB.");
        output.WriteLine($"Discovery {discoveryMilliseconds:F1} ms; inspection {scanMilliseconds - discoveryMilliseconds:F1} ms; backup {backupMilliseconds:F1} ms; deletion {applyMilliseconds - backupMilliseconds:F1} ms.");
    }

    /// <summary>Removes only this fixture's generated files and verified backup payloads.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
