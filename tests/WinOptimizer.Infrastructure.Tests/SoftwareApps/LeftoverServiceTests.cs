using Microsoft.Win32;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Extensions.DI;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.SoftwareApps;

/// <summary>Uses the real scan/backup/delete/recovery flow with an in-memory registry and fake MSI inventory.</summary>
public sealed class LeftoverServiceTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "leftover-test-" + Guid.NewGuid().ToString("N"));
    private readonly UninstallResidueServiceTests.RegistryStore _registry = new();
    private readonly Mock<IUninstallAccountSessions> _sessions = new();
    private readonly Mock<IUninstallScanFolders> _folders = new();
    private readonly IInstallerInventory _msi;
    private readonly Mock<ICleanerBackupVault> _vault = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly WindowsLeftoverSource _source;
    private readonly LeftoverService _service;
    private readonly HashSet<string> _products = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _complete = true, _busy;
    private string Cache => Path.Combine(_root, "Windows", "Installer");
    private string PackageCache => Path.Combine(_root, "ProgramData", "Package Cache");
    private static readonly string Arp = @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\Tool";

    public LeftoverServiceTests()
    {
        Directory.CreateDirectory(_root);
        _folders.SetupGet(source => source.WindowsDirectory).Returns(Path.Combine(_root, "Windows"));
        _folders.SetupGet(source => source.ApplicationDirectories).Returns([Path.Combine(_root, "ProgramData")]);
        _folders.SetupGet(source => source.ShortcutDirectories).Returns([]);
        _sessions.Setup(source => source.GetAccounts()).Returns([new(Sid, "Fixture user", true)]);
        _sessions.Setup(source => source.Open(It.IsAny<string?>())).Returns((string? sid) =>
        {
            var session = new Mock<ICleanerRegistrySession>();
            session.SetupGet(source => source.UserPrefix).Returns(@"HKEY_USERS\" + (sid ?? Sid));
            session.SetupGet(source => source.Reader).Returns(_registry);
            session.SetupGet(source => source.Editor).Returns(_registry);
            session.Setup(source => source.GetFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns(Path.Combine(_root, "Profile"));
            return session.Object;
        });
        _msi = new InstallerFixture(() => new InstallerReferences(
            new(_products, StringComparer.OrdinalIgnoreCase), new(_files, StringComparer.OrdinalIgnoreCase),
            new(StringComparer.OrdinalIgnoreCase), _complete, _busy, []));
        _vault.SetupGet(source => source.RootDirectory).Returns(Path.Combine(_root, "vault"));
        _vault.Setup(source => source.EnsureDirectory(It.IsAny<string>())).Callback((string path) => Directory.CreateDirectory(path));
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Returns((byte[] bytes) => bytes);
        _vault.Setup(source => source.Unseal(It.IsAny<byte[]>())).Returns((byte[] bytes) => bytes);
        _mode.Setup(source => source.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        _source = new(_sessions.Object, _folders.Object, _msi);
        _service = new(_source, _sessions.Object, _folders.Object, Mock.Of<ICleanerShortcutReader>(), _vault.Object, _mode.Object);
    }

    private string Installer(string name, bool package = false, int age = 90)
    {
        var path = Path.Combine(package ? PackageCache : Cache, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "isolated fixture payload");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-age));
        return path;
    }

    private void Registration(string path = "", bool view32 = false)
    {
        path = string.IsNullOrEmpty(path) ? Arp : path;
        _registry.Set(path, view32,
            new("DisplayName", "Obsolete fixture app", RegistryValueKind.String),
            new("InstallLocation", Path.Combine(_root, "Gone"), RegistryValueKind.String),
            new("DisplayIcon", Path.Combine(_root, "Gone", "app.exe"), RegistryValueKind.String),
            new("UninstallString", "\"" + Path.Combine(_root, "Gone", "remove.exe") + "\"", RegistryValueKind.String));
    }

    /// <summary>Creates a stale registration with aged, non-executable remnants inside a supported installation root.</summary>
    private string InstallationRemnant(string? directory = null, string registration = "")
    {
        directory ??= Path.Combine(_root, "ProgramData", "ObsoleteTool");
        registration = string.IsNullOrEmpty(registration) ? Arp : registration;
        Registration(registration);
        _registry.Set(registration, false,
            new("InstallLocation", directory, RegistryValueKind.String),
            new("DisplayIcon", Path.Combine(directory, "app.exe"), RegistryValueKind.String),
            new("UninstallString", "\"" + Path.Combine(directory, "remove.exe") + "\"", RegistryValueKind.String));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");
        File.WriteAllText(file, "saved fixture configuration");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-90));
        File.SetLastAccessTimeUtc(file, DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
        return directory;
    }

    [Fact]
    public async Task InstallationRemnantIsSelectableBackedUpDeletedAndRestoredWithItsRegistration()
    {
        var directory = InstallationRemnant();
        var child = Path.Combine(directory, "Empty");
        Directory.CreateDirectory(child);
        Directory.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
        var scan = await _service.ScanAsync();
        var item = Assert.Single(scan.Items);
        Assert.Equal(LeftoverKind.Folder, item.Kind); Assert.True(item.CanRemove);
        Assert.Equal(directory, item.Location); Assert.True(item.Bytes > 0);
        var result = await _service.RemoveAsync([item.Id]);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Errors); Assert.NotNull(result.BackupId);
        Assert.False(Directory.Exists(directory)); Assert.False(_registry.KeyExists(Arp, false));
        var backups = new UninstallBackupStore(_vault.Object, new(_sessions.Object, _folders.Object, Mock.Of<ICleanerShortcutReader>()));
        var recovery = await backups.RestoreAsync(result.BackupId.Value, default);
        Assert.Empty(recovery.Errors); Assert.True(Directory.Exists(child)); Assert.True(_registry.KeyExists(Arp, false));
        Assert.Equal("saved fixture configuration", File.ReadAllText(Path.Combine(directory, "settings.json")));
    }

    [Theory]
    [InlineData("remaining.exe")]
    [InlineData("library.dll")]
    [InlineData("launch.ps1")]
    [InlineData("repair.msi")]
    public async Task InstallationFoldersWithRunnablePayloadsAreExcluded(string name)
    {
        var directory = InstallationRemnant();
        var payload = Path.Combine(directory, name);
        File.WriteAllText(payload, "fixture payload");
        File.SetLastWriteTimeUtc(payload, DateTime.UtcNow.AddDays(-90));
        File.SetLastAccessTimeUtc(payload, DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(File.Exists(payload));
    }

    [Fact]
    public async Task ActiveAndHiddenInstallationsProtectOverlappingRemnantFolders()
    {
        var directory = InstallationRemnant();
        _registry.Set(Arp + "Other", false,
            new("SystemComponent", 1, RegistryValueKind.DWord),
            new("InstallLocation", directory, RegistryValueKind.String));
        Assert.Empty((await _service.ScanAsync()).Items);
    }

    [Fact]
    public async Task RecentRemnantsAndDirectoriesWithoutStaleOwnershipAreExcluded()
    {
        var directory = InstallationRemnant();
        File.SetLastAccessTimeUtc(Path.Combine(directory, "settings.json"), DateTime.UtcNow);
        Assert.Empty((await _service.ScanAsync()).Items);
        _registry.Remove(Arp, false);
        File.SetLastAccessTimeUtc(Path.Combine(directory, "settings.json"), DateTime.UtcNow.AddDays(-90));
        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task AccountAppDataInstallationRemnantsUseTheirOwningAccountBoundary()
    {
        var directory = Path.Combine(_root, "Profile", "ObsoleteTool");
        var registration = @"HKEY_USERS\" + Sid + "\\" + UninstallResources.UninstallBranch + "\\Tool";
        InstallationRemnant(directory, registration);
        var scan = await _service.ScanAsync();
        Assert.Equal(directory, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Empty(result.Errors); Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task RequiredMsiAndIncompleteCoverageExcludeInstallationFolders()
    {
        var directory = InstallationRemnant();
        _files.Add(Path.Combine(directory, "settings.json"));
        Assert.Empty((await _service.ScanAsync()).Items);
        _files.Clear(); _complete = false;
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items); Assert.NotEmpty(scan.Notices);
    }

    [Fact]
    public async Task InstallationBecomingActiveDuringBackupPreventsFolderDeletion()
    {
        var directory = InstallationRemnant();
        var scan = await _service.ScanAsync();
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Callback(() =>
            File.WriteAllText(Path.Combine(directory, "app.exe"), "new installation")).Returns((byte[] bytes) => bytes);
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([Assert.Single(scan.Items).Id]));
        Assert.True(File.Exists(Path.Combine(directory, "settings.json"))); Assert.True(_registry.KeyExists(Arp, false));
    }

    [Fact]
    public async Task SharedApplicationRootsAndPathsOutsideInstallationLocationsAreExcluded()
    {
        InstallationRemnant(Path.Combine(_root, "Unrelated"));
        Assert.Empty((await _service.ScanAsync()).Items);
        InstallationRemnant(Path.Combine(_root, "ProgramData", "Microsoft", "Shared"));
        Assert.Empty((await _service.ScanAsync()).Items);
    }

    [Fact]
    public async Task UnresolvedActiveDirectoryMetadataExcludesFolderCleanup()
    {
        InstallationRemnant();
        _registry.Set(Arp + "Active", false, new CleanerRegistryValue("InstallLocation", "%UNRESOLVED%\\Active", RegistryValueKind.String));
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains("installation-directory metadata"));
    }

    [Fact]
    public async Task DeepInstallationTreesAreExcludedWithAnActionableNotice()
    {
        var directory = InstallationRemnant();
        var path = directory;
        for (int depth = 0; depth < 17; depth++)
        {
            path = Path.Combine(path, "Nested");
            Directory.CreateDirectory(path);
        }
        foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items); Assert.Contains(scan.Notices, notice => notice.Contains("bounded leftover scan"));
    }

    [Fact]
    public async Task MissingExecutablesOutsideTheRecordedFolderDoNotEstablishFolderOwnership()
    {
        InstallationRemnant();
        _registry.Set(Arp, false, new CleanerRegistryValue("DisplayIcon", Path.Combine(_root, "Gone", "other.exe"), RegistryValueKind.String));
        Assert.Empty((await _service.ScanAsync()).Items);
    }

    [Fact]
    public async Task LinksInsideAnInstallationRemnantExcludeTheWholeFolder()
    {
        // Junctions require a local NTFS volume; the checkout can be on a projected development drive.
        var temporary = Path.Combine(Path.GetTempPath(), "leftover-link-" + Guid.NewGuid().ToString("N"));
        var applicationRoot = Path.Combine(temporary, "Applications");
        _folders.SetupGet(source => source.ApplicationDirectories).Returns([applicationRoot]);
        var directory = InstallationRemnant(Path.Combine(applicationRoot, "ObsoleteTool"));
        var outside = Path.Combine(temporary, "Outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(directory, "Linked");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "/c", "mklink", "/J", link, outside }
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd());
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
            var scan = await _service.ScanAsync();
            Assert.Empty(scan.Items); Assert.Contains(scan.Notices, notice => notice.Contains("links or junctions"));
            Assert.True(Directory.Exists(outside));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(temporary), StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("leftover-link-", Path.GetFileName(temporary));
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    public void DependencyCompositionResolvesLeftoverServiceWithoutStartingAnInventory()
    {
        var services = new ServiceCollection();
        services.AddSoftwareAppServices();
        services.AddSingleton(_sessions.Object); services.AddSingleton(_folders.Object);
        services.AddSingleton(_vault.Object); services.AddSingleton(_mode.Object);
        services.AddSingleton(Mock.Of<ICleanerShortcutReader>());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<LeftoverService>(provider.GetRequiredService<ILeftoverService>());
        _sessions.Verify(source => source.Open(It.IsAny<string?>()), Times.Never);
        _sessions.Verify(source => source.GetAccounts(), Times.Never);
    }

    [Fact]
    public async Task ScanIncludesStaleRegistrationsAndAllInstallerKinds_AndProtectsRecentFiles()
    {
        Registration();
        Installer("old.msi"); Installer("old.msp"); Installer("old.exe", package: true);
        var recent = Installer("recent.msi", age: 1);
        var scan = await _service.ScanAsync();
        Assert.Equal(4, scan.Items.Count);
        Assert.Equal(4, scan.Items.Count(item => item.CanRemove));
        Assert.Equal(new[] { LeftoverKind.Registration, LeftoverKind.Msi, LeftoverKind.Msp, LeftoverKind.Exe }, scan.Items.Select(item => item.Kind).Distinct().Order());
        Assert.DoesNotContain(scan.Items, item => item.Location == recent);
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task ReferencedProductPatchAndHiddenUserBundleCachesAreExcluded()
    {
        var product = Installer("product.msi"); var patch = Installer("patch.msp"); var bundle = Installer("bundle.exe", package: true);
        _files.UnionWith([product, patch]);
        _registry.Set(@"HKEY_USERS\" + Sid + @"\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hidden", true,
            new("DisplayName", "Hidden dependency", RegistryValueKind.String), new("SystemComponent", 1, RegistryValueKind.DWord),
            new("BundleCachePath", bundle, RegistryValueKind.String));
        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.Equal(Path.GetFullPath(bundle), CleanerFileDeletion.ResolveReferencePath(bundle.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task IncompleteOrBusyInstallerInventoryProtectsEveryUnreferencedFile(bool complete, bool busy)
    {
        _complete = complete; _busy = busy; Installer("orphan.msi");
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.NotEmpty(scan.Notices);
        Assert.DoesNotContain(scan.Notices, notice => notice.Contains("Run as administrator"));
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([Guid.NewGuid()]));
    }

    [Fact]
    public async Task PendingRestartAndUnreadableAccountProtectInstallerFiles()
    {
        Installer("orphan.msp");
        _registry.Set(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", false);
        _sessions.Setup(source => source.Open(Sid)).Throws(new UnauthorizedAccessException("Offline fixture hive is unavailable"));
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains("Offline fixture hive"));
    }

    [Fact]
    public async Task LiveMsiRegistrationAndExistingApplicationDirectoryArePreserved()
    {
        var product = "{12345678-1234-1234-1234-123456789012}";
        var msiPath = Arp[..Arp.LastIndexOf('\\')] + "\\" + product;
        Registration(msiPath);
        _registry.Set(msiPath, false, new CleanerRegistryValue("WindowsInstaller", 1, RegistryValueKind.DWord));
        _products.Add(product);
        Assert.Empty((await _service.ScanAsync()).Items);
        Registration();
        Directory.CreateDirectory(Path.Combine(_root, "Gone"));
        Assert.Empty((await _service.ScanAsync()).Items);
    }

    [Fact]
    public async Task InternalWindowsInstallerPayloadsAndExecutableResourcesStayOutsideTheScan()
    {
        var nested = Installer(@"Internal\payload.msi");
        var executable = Installer("icon-resource.exe");
        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(File.Exists(nested)); Assert.True(File.Exists(executable));
    }

    [Fact]
    public async Task InstallerReferenceAppearingDuringBackupPreventsDeletion()
    {
        var path = Installer("repair.msi");
        var scan = await _service.ScanAsync();
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Callback(() => _files.Add(path)).Returns((byte[] bytes) => bytes);
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([scan.Items[0].Id]));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task BusyInstallationProtectsStaleRegistrationsAndMalformedCacheReferencesProtectFiles()
    {
        Registration(); _busy = true;
        Assert.Empty((await _service.ScanAsync()).Items);
        _busy = false;
        _registry.Remove(Arp, false);
        _registry.Set(Arp, false, new CleanerRegistryValue("BundleCachePath", 123, RegistryValueKind.DWord));
        Installer("unverified.msi");
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains("invalid registry value type"));
    }

    [Fact]
    public async Task OversizedRegistrationIsPreservedAsAWholeAndReportsItsCoverageGap()
    {
        Registration();
        _registry.Set(Arp, false, Enumerable.Range(0, 4097).Select(index =>
            new CleanerRegistryValue("FixtureValue" + index, "value", RegistryValueKind.String)).ToArray());
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains("too large"));
        Assert.Equal(4101, _registry.GetValues(Arp, false, default).Count);
    }

    [Fact]
    public async Task CleanupBacksUpSelectedRegistrySubtreesAndInstallerFiles_AndRestoresThroughSharedRecovery()
    {
        Registration();
        _registry.Set(Arp + @"\Child", false, new CleanerRegistryValue("Payload", new byte[] { 1, 2 }, RegistryValueKind.Binary));
        var selectedFile = Installer("selected.msi");
        var keep = Installer("unselected.msp");
        var scan = await _service.ScanAsync();
        var selected = scan.Items.Where(item => item.Location != keep).Select(item => item.Id).ToArray();
        var result = await _service.RemoveAsync(selected);
        Assert.Equal(2, result.Removed); Assert.Empty(result.Errors); Assert.NotNull(result.BackupId);
        Assert.False(File.Exists(selectedFile)); Assert.True(File.Exists(keep)); Assert.False(_registry.KeyExists(Arp, false));
        var backups = new UninstallBackupStore(_vault.Object, new(_sessions.Object, _folders.Object, Mock.Of<ICleanerShortcutReader>()));
        Assert.Single(backups.List(default));
        var restored = await backups.RestoreAsync(result.BackupId.Value, default);
        Assert.Empty(restored.Errors); Assert.True(File.Exists(selectedFile)); Assert.True(_registry.KeyExists(Arp, false));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(_registry.Value(Arp + @"\Child", false, "Payload")!.Data));
    }

    [Fact]
    public async Task BackupFailurePreventsEveryDeletion()
    {
        Registration(); var path = Installer("kept.msi");
        var scan = await _service.ScanAsync();
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Throws(new IOException("Fixture backup failed"));
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync(scan.Items.Select(item => item.Id).ToArray()));
        Assert.True(File.Exists(path)); Assert.True(_registry.KeyExists(Arp, false));
    }

    [Fact]
    public async Task FreshOwnershipAndRegistryChangesInvalidateIssuedSelections()
    {
        var file = Installer("newly-required.msi");
        var scan = await _service.ScanAsync();
        _files.Add(file);
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([scan.Items[0].Id]));
        Assert.True(File.Exists(file));
        Registration(); scan = await _service.ScanAsync();
        _registry.Set(Arp, false, new CleanerRegistryValue("NewValue", "external change", RegistryValueKind.String));
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([Assert.Single(scan.Items).Id]));
        Assert.True(_registry.KeyExists(Arp, false));
    }

    [Fact]
    public async Task UnknownExpiredAndBuilderSelectionsNeverMutateResources()
    {
        var file = Installer("kept.msi"); var old = await _service.ScanAsync(); await _service.ScanAsync();
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([old.Items[0].Id]));
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([Guid.NewGuid()]));
        var current = await _service.ScanAsync();
        _mode.SetupGet(source => source.CurrentMode).Returns(WinOptimizerMode.Builder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveAsync([current.Items[0].Id]));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task CancelledScanCannotIssueAnActionableCleanupPlan()
    {
        Installer("kept.msi");
        var previous = await _service.ScanAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ScanAsync(cancellation.Token));
        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([previous.Items[0].Id]));
    }

    [Theory]
    [InlineData("outside.msi", false)]
    [InlineData("outside.exe", false)]
    [InlineData("cache.txt", true)]
    public void InstallerRecoveryBoundaryRejectsOtherLocationsAndFileTypes(string name, bool inside)
    {
        var path = Path.Combine(inside ? Cache : _root, name);
        var root = new UninstallFileRoot(path, null, null, Cache, true, InstallerCache: true);
        Assert.Throws<IOException>(() => UninstallResources.ValidateRoot(root, _sessions.Object, _folders.Object));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("leftover-test-", Path.GetFileName(full));
        Directory.Delete(full, true);
    }

    private sealed class InstallerFixture(Func<InstallerReferences> read) : IInstallerInventory
    {
        public InstallerReferences Read(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return read(); }
    }
}
