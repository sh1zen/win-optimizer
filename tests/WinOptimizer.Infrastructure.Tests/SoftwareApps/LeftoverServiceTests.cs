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
        var installationRoots = new[] { Path.Combine(_root, "ProgramFiles"), Path.Combine(_root, "ProgramFilesX86") };
        _folders.SetupGet(source => source.InstallationDirectories).Returns(installationRoots);
        _folders.SetupGet(source => source.ApplicationDirectories).Returns([.. installationRoots, Path.Combine(_root, "ProgramData")]);
        _folders.SetupGet(source => source.ShortcutDirectories).Returns([]);
        _sessions.Setup(source => source.GetAccounts()).Returns([new(Sid, "Fixture user", true)]);
        _sessions.Setup(source => source.Open(It.IsAny<string?>())).Returns((string? sid) =>
        {
            var session = new Mock<ICleanerRegistrySession>();
            session.SetupGet(source => source.UserPrefix).Returns(@"HKEY_USERS\" + (sid ?? Sid));
            session.SetupGet(source => source.Reader).Returns(_registry);
            session.SetupGet(source => source.Editor).Returns(_registry);
            var profile = Path.Combine(_root, sid == Sid || sid == null ? "Profile" : "Profile-" + sid);
            session.Setup(source => source.GetFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns((Environment.SpecialFolder folder) => folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => Path.Combine(profile, "AppData", "Local"),
                Environment.SpecialFolder.ApplicationData => Path.Combine(profile, "AppData", "Roaming"),
                _ => profile
            });
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

    private string Installer(string name, bool package = false, int age = 90, string? cacheRoot = null)
    {
        var path = Path.Combine(cacheRoot ?? (package ? PackageCache : Cache), name);
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
        return AgedInstallation(directory);
    }

    /// <summary>Creates aged installation data independently of uninstall registration metadata.</summary>
    private static string AgedInstallation(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");
        File.WriteAllText(file, "saved fixture configuration");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-90));
        File.SetLastAccessTimeUtc(file, DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
        return directory;
    }

    /// <summary>Creates an old, empty installation leaf without inventing an uninstall record.</summary>
    private static string AgedEmptyInstallation(string directory)
    {
        Directory.CreateDirectory(directory);
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
        return directory;
    }

    [Theory]
    [InlineData("ProgramFiles")]
    [InlineData("ProgramFilesX86")]
    [InlineData(@"Profile\AppData\Local\Programs")]
    public async Task EmptyInstallationLeavesAreDiscoveredBackedUpDeletedAndRestored(string location)
    {
        var directory = AgedEmptyInstallation(Path.Combine(_root, location, "RetiredTool"));

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Notices);
        var item = Assert.Single(scan.Items);
        Assert.Equal(directory, item.Location); Assert.Equal(LeftoverKind.Folder, item.Kind); Assert.Equal(0, item.Bytes);
        Assert.Contains("empty installation directory", item.Reason);
        var result = await _service.RemoveAsync([item.Id]);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Errors); Assert.False(Directory.Exists(directory));
        Assert.True(Directory.Exists(Path.GetDirectoryName(directory)));
        var backups = new UninstallBackupStore(_vault.Object, new(_sessions.Object, _folders.Object, Mock.Of<ICleanerShortcutReader>()));
        Assert.Empty((await backups.RestoreAsync(result.BackupId!.Value, default)).Errors);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task EmptyPreviousArchitectureFolderDoesNotRemoveTheCurrentInstallation()
    {
        var previous = AgedEmptyInstallation(Path.Combine(_root, "ProgramFilesX86", "Tool"));
        var current = Path.Combine(_root, "ProgramFiles", "Tool");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "app.exe"), "installed application");
        _registry.Set(Arp, false, new("DisplayName", "Tool 2.0", RegistryValueKind.String),
            new CleanerRegistryValue("InstallLocation", current, RegistryValueKind.String));

        var scan = await _service.ScanAsync();

        Assert.Equal(previous, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Errors);
        Assert.True(File.Exists(Path.Combine(current, "app.exe")));
    }

    [Theory]
    [InlineData("location")]
    [InlineData("name-only")]
    [InlineData("repair-command")]
    public async Task ActiveOwnershipProtectsEmptyInstallationLeaves(string evidence)
    {
        var directory = AgedEmptyInstallation(Path.Combine(_root, "ProgramFiles", "Tool"));
        _registry.Set(Arp, false, new CleanerRegistryValue("DisplayName", "Tool", RegistryValueKind.String));
        if (evidence == "location") _registry.Set(Arp, false, new CleanerRegistryValue("InstallLocation", directory, RegistryValueKind.String));
        if (evidence == "repair-command") _registry.Set(Arp, false,
            new CleanerRegistryValue("DisplayName", "Other application", RegistryValueKind.String),
            new CleanerRegistryValue("RepairPath", $"launcher.exe --data=\"{directory}\"", RegistryValueKind.String));

        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("ModifiableWindowsApps")]
    [InlineData("Common Files")]
    [InlineData("Windows Mail")]
    [InlineData("Microsoft Support")]
    [InlineData("Uninstall Information")]
    [InlineData("Temp")]
    public async Task WindowsAndSharedEmptyDirectoriesAreProtected(string name)
    {
        var directory = AgedEmptyInstallation(Path.Combine(_root, "ProgramFiles", name));
        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public async Task ProtectedAttributesExcludeEmptyInstallationLeaves(FileAttributes protection)
    {
        var directory = AgedEmptyInstallation(Path.Combine(_root, "ProgramFiles", "ProtectedFolder"));
        File.SetAttributes(directory, File.GetAttributes(directory) | protection);
        try { Assert.Empty((await _service.ScanAsync()).Items); }
        finally { File.SetAttributes(directory, FileAttributes.Directory); }
    }

    [Fact]
    public async Task EmptyDataFoldersAndFoldersContainingEmptyChildrenAreNotInstallationRemnants()
    {
        foreach (var location in new[] { "ProgramData", @"Profile\AppData\Local", @"Profile\AppData\Roaming", @"Profile\AppData\LocalLow" })
            AgedEmptyInstallation(Path.Combine(_root, location, "EmptyData"));
        var parent = Path.Combine(_root, "ProgramFiles", "Container");
        AgedEmptyInstallation(Path.Combine(parent, "EmptyChild"));
        Directory.SetLastWriteTimeUtc(parent, DateTime.UtcNow.AddDays(-90));

        Assert.Empty((await _service.ScanAsync()).Items);
    }

    [Fact]
    public async Task NewContentsDuringBackupInvalidateAnEmptyDirectorySelection()
    {
        var directory = AgedEmptyInstallation(Path.Combine(_root, "ProgramFiles", "Tool"));
        var scan = await _service.ScanAsync();
        var newFile = Path.Combine(directory, "settings.json");
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Callback(() => File.WriteAllText(newFile, "new data"))
            .Returns((byte[] bytes) => bytes);

        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([Assert.Single(scan.Items).Id]));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public async Task UnreadableUnrelatedProfileDoesNotHideVerifiedEmptyInstallationLeaves()
    {
        var directory = AgedEmptyInstallation(Path.Combine(_root, "ProgramFiles", "RetiredTool"));
        _sessions.Setup(source => source.Open(Sid)).Throws(new UnauthorizedAccessException("Fixture profile unavailable"));
        var scan = await _service.ScanAsync();
        Assert.Equal(directory, Assert.Single(scan.Items).Location);
        Assert.Contains(scan.Notices, notice => notice.Contains("Fixture profile unavailable"));
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
    [InlineData("InstallLocation")]
    [InlineData("DisplayIcon")]
    public async Task MissingOptionalMetadataDoesNotHideVerifiedInstallationRemnants(string absentField)
    {
        var directory = InstallationRemnant();
        using (var key = _registry.OpenKey(Arp, false, true)) key.DeleteValue(absentField);

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Notices);
        Assert.Equal(directory, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("InstallLocation", RegistryValueKind.DWord, 1)]
    [InlineData("DisplayIcon", RegistryValueKind.DWord, 1)]
    [InlineData("UninstallString", RegistryValueKind.DWord, 1)]
    [InlineData("QuietUninstallString", RegistryValueKind.DWord, 1)]
    [InlineData("ModifyPath", RegistryValueKind.DWord, 1)]
    [InlineData("RepairPath", RegistryValueKind.DWord, 1)]
    [InlineData("BundleCachePath", RegistryValueKind.DWord, 1)]
    [InlineData("InstallSource", RegistryValueKind.DWord, 1)]
    [InlineData("DisplayName", RegistryValueKind.DWord, 1)]
    [InlineData("ParentKeyName", RegistryValueKind.DWord, 1)]
    [InlineData("ReleaseType", RegistryValueKind.DWord, 1)]
    [InlineData("WindowsInstaller", RegistryValueKind.String, "1")]
    [InlineData("SystemComponent", RegistryValueKind.String, "1")]
    public async Task MalformedRegistrationMetadataIsReportedAndPreserved(string field, RegistryValueKind kind, object value)
    {
        var directory = Path.Combine(_root, "ProgramFiles", "RemovedTool");
        Registration();
        _registry.Set(Arp, false,
            new("InstallLocation", directory, RegistryValueKind.String),
            new("DisplayIcon", Path.Combine(directory, "app.exe"), RegistryValueKind.String),
            new("UninstallString", $"\"{Path.Combine(directory, "remove.exe")}\"", RegistryValueKind.String),
            new CleanerRegistryValue(field, value, kind));

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Items);
        Assert.True(_registry.KeyExists(Arp, false));
        Assert.Contains(scan.Notices, notice => notice.Contains(field == "InstallLocation"
            ? "installation-directory metadata" : field + " has an invalid registry value type"));
    }

    [Fact]
    public async Task MalformedRegistrationStillProtectsItsRecordedEmptyInstallationDirectory()
    {
        var directory = AgedEmptyInstallation(Path.Combine(_root, "ProgramFiles", "RecordedTool"));
        Registration();
        _registry.Set(Arp, false,
            new("InstallLocation", directory, RegistryValueKind.String),
            new("DisplayIcon", Path.Combine(directory, "app.exe"), RegistryValueKind.String),
            new("UninstallString", $"\"{Path.Combine(directory, "remove.exe")}\"", RegistryValueKind.String),
            new CleanerRegistryValue("ParentKeyName", 1, RegistryValueKind.DWord));

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Items);
        Assert.True(Directory.Exists(directory));
        Assert.Contains(scan.Notices, notice => notice.Contains("ParentKeyName has an invalid registry value type"));
    }

    [Fact]
    public async Task MissingIconWithRunnableApplicationDoesNotEstablishAStaleInstallation()
    {
        var directory = InstallationRemnant();
        using (var key = _registry.OpenKey(Arp, false, true)) key.DeleteValue("DisplayIcon");
        var executable = Path.Combine(directory, "real-app.exe");
        File.WriteAllText(executable, "installed application");
        File.SetLastWriteTimeUtc(executable, DateTime.UtcNow.AddDays(-90));
        File.SetLastAccessTimeUtc(executable, DateTime.UtcNow.AddDays(-90));
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));

        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(File.Exists(executable));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task MsiProductEvidenceWorksWithoutOptionalDisplayPaths(bool installed, bool complete)
    {
        const string product = "{12345678-1234-1234-1234-123456789012}";
        var registration = Arp[..Arp.LastIndexOf('\\')] + "\\" + product;
        _registry.Set(registration, false,
            new("DisplayName", "Removed MSI product", RegistryValueKind.String),
            new CleanerRegistryValue("WindowsInstaller", 1, RegistryValueKind.DWord));
        if (installed) _products.Add(product);
        _complete = complete;

        var scan = await _service.ScanAsync();

        if (installed || !complete) Assert.Empty(scan.Items);
        else
        {
            Assert.Equal(registration, Assert.Single(scan.Items).Location);
            var result = await _service.RemoveAsync([scan.Items[0].Id]);
            Assert.Equal(1, result.Removed); Assert.Empty(result.Errors);
            Assert.False(_registry.KeyExists(registration, false));
            var backups = new UninstallBackupStore(_vault.Object, new(_sessions.Object, _folders.Object, Mock.Of<ICleanerShortcutReader>()));
            Assert.Empty((await backups.RestoreAsync(result.BackupId!.Value, default)).Errors);
            Assert.True(_registry.KeyExists(registration, false));
        }
    }

    [Fact]
    public async Task NameOnlyEntriesAndMissingCacheUninstallersDoNotEstablishRemovedApplications()
    {
        _registry.Set(Arp, false, new CleanerRegistryValue("DisplayName", "Incomplete registration", RegistryValueKind.String));
        _registry.Set(Arp + "Other", false,
            new("DisplayName", "Broken installer reference", RegistryValueKind.String),
            new CleanerRegistryValue("UninstallString", $"\"{Path.Combine(PackageCache, "Missing", "remove.exe")}\"", RegistryValueKind.String));
        Assert.Empty((await _service.ScanAsync()).Items);
    }

    [Theory]
    [InlineData("remaining.exe")]
    [InlineData("library.dll")]
    [InlineData("launch.ps1")]
    [InlineData("repair.msi")]
    [InlineData("module.pyc")]
    [InlineData("module.pyo")]
    [InlineData("launch.pyw")]
    [InlineData("launch.vbe")]
    [InlineData("launch.jse")]
    [InlineData("dependency.msixbundle")]
    [InlineData("dependency.appxbundle")]
    [InlineData("launch.application")]
    [InlineData("launch.appref-ms")]
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
    public async Task RecentRemnantsAndAgedUnregisteredFoldersAreExcluded()
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
        var directory = Path.Combine(_root, "Profile", "AppData", "Local", "ObsoleteTool");
        var registration = @"HKEY_USERS\" + Sid + "\\" + UninstallResources.UninstallBranch + "\\Tool";
        InstallationRemnant(directory, registration);
        var scan = await _service.ScanAsync();
        Assert.Equal(directory, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Empty(result.Errors); Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ConfirmedInstallationRemnantsAcrossMachineAndAllUserRootsAreCleanedAndRestored()
    {
        const string otherSid = "S-1-5-21-1-2-3-1002";
        _sessions.Setup(source => source.GetAccounts()).Returns([new(Sid, "Current user", true), new(otherSid, "Other user", false)]);
        var machineRoots = new[] { "ProgramFiles", "ProgramFilesX86", "ProgramData" }.Select(name => Path.Combine(_root, name)).ToArray();
        _folders.SetupGet(source => source.ApplicationDirectories).Returns(machineRoots);
        var directories = machineRoots.Select(root => Path.Combine(root, "ObsoleteTool")).ToList();
        foreach (var profile in new[] { "Profile", "Profile-" + otherSid })
        foreach (var location in new[] { @"Local\ObsoleteTool", @"Local\Programs\ObsoleteTool", @"Roaming\ObsoleteTool", @"LocalLow\ObsoleteTool" })
            directories.Add(Path.Combine(_root, profile, "AppData", location));
        for (var index = 0; index < directories.Count; index++)
        {
            var directory = directories[index];
            var sid = directory.Contains("Profile-" + otherSid, StringComparison.Ordinal) ? otherSid :
                directory.Contains("Profile", StringComparison.Ordinal) ? Sid : null;
            var registration = (sid == null ? @"HKEY_LOCAL_MACHINE\" : @"HKEY_USERS\" + sid + "\\") +
                UninstallResources.UninstallBranch + "\\ObsoleteTool" + index;
            InstallationRemnant(directory, registration);
        }

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Notices);
        Assert.Equal(directories.Order(), scan.Items.Select(item => item.Location).Order());
        Assert.All(scan.Items, item => { Assert.Equal(LeftoverKind.Folder, item.Kind); Assert.True(item.CanRemove); });
        var result = await _service.RemoveAsync(scan.Items.Select(item => item.Id).ToArray());
        Assert.Equal(directories.Count, result.Removed); Assert.Empty(result.Errors); Assert.NotNull(result.BackupId);
        Assert.All(directories, directory => Assert.False(Directory.Exists(directory)));
        Assert.All(machineRoots, root => Assert.True(Directory.Exists(root)));
        Assert.True(Directory.Exists(Path.Combine(_root, "Profile", "AppData", "Local", "Programs")));
        var backups = new UninstallBackupStore(_vault.Object, new(_sessions.Object, _folders.Object, Mock.Of<ICleanerShortcutReader>()));
        var restored = await backups.RestoreAsync(result.BackupId.Value, default);
        Assert.Empty(restored.Errors);
        Assert.All(directories, directory => Assert.Equal("saved fixture configuration", File.ReadAllText(Path.Combine(directory, "settings.json"))));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("resources.dat")]
    [InlineData("portable-settings.ini")]
    [InlineData("extensionless-runtime")]
    public async Task UnregisteredDataFoldersAcrossEveryScopeAreNotListed(string fileName)
    {
        var machineRoots = new[] { "ProgramFiles", "ProgramFilesX86", "ProgramData" }.Select(name => Path.Combine(_root, name)).ToArray();
        _folders.SetupGet(source => source.ApplicationDirectories).Returns(machineRoots);
        var directories = machineRoots.Select(root => Path.Combine(root, "DataOnly")).Concat(
            new[] { @"Local\DataOnly", @"Local\Programs\DataOnly", @"Roaming\DataOnly", @"LocalLow\DataOnly" }
                .Select(path => Path.Combine(_root, "Profile", "AppData", path))).ToArray();
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, fileName);
            File.WriteAllText(file, "active or unknown data without an uninstall registration");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-90));
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow.AddDays(-90));
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90));
        }

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Items); Assert.Empty(scan.Notices);
        Assert.All(directories, directory => Assert.True(Directory.Exists(directory)));
    }

    [Fact]
    public async Task StaleRegistrationDoesNotQualifySameNamedDataInAnotherRoot()
    {
        var confirmed = InstallationRemnant();
        var unregistered = AgedInstallation(Path.Combine(_root, "Profile", "AppData", "Roaming", Path.GetFileName(confirmed)));

        var scan = await _service.ScanAsync();

        Assert.Equal(confirmed, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Empty(result.Errors); Assert.Equal(1, result.Removed);
        Assert.True(File.Exists(Path.Combine(unregistered, "settings.json")));
    }

    [Fact]
    public async Task ActiveProductAtAnotherLocationProtectsItsObsoleteInstallationFolder()
    {
        var directory = InstallationRemnant();
        _registry.Set(Arp + "Active", false,
            new("DisplayName", "Obsolete fixture app 2.0", RegistryValueKind.String),
            new CleanerRegistryValue("InstallLocation", Path.Combine(_root, "CurrentInstallation"), RegistryValueKind.String));

        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(File.Exists(Path.Combine(directory, "settings.json")));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(Sid, true)]
    [InlineData("S-1-5-21-1-2-3-1002", false)]
    public async Task ActiveProductIdentityProtectionUsesTheFoldersAccountScope(string? activeSid, bool protectedFolder)
    {
        const string otherSid = "S-1-5-21-1-2-3-1002";
        _sessions.Setup(source => source.GetAccounts()).Returns([new(Sid, "Current user", true), new(otherSid, "Other user", false)]);
        var registration = @"HKEY_USERS\" + Sid + "\\" + UninstallResources.UninstallBranch + "\\ObsoleteTool";
        var directory = InstallationRemnant(Path.Combine(_root, "Profile", "AppData", "Local", "Programs", "ObsoleteTool"), registration);
        var activeRegistration = (activeSid == null ? @"HKEY_LOCAL_MACHINE\" : @"HKEY_USERS\" + activeSid + "\\") +
            UninstallResources.UninstallBranch + "\\ActiveTool";
        _registry.Set(activeRegistration, false,
            new("DisplayName", "Obsolete fixture app 2.0", RegistryValueKind.String),
            new CleanerRegistryValue("InstallLocation", Path.Combine(_root, "CurrentInstallation"), RegistryValueKind.String));

        var scan = await _service.ScanAsync();

        if (protectedFolder) Assert.Empty(scan.Items);
        else Assert.Equal(directory, Assert.Single(scan.Items).Location);
        Assert.True(File.Exists(Path.Combine(directory, "settings.json")));
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("repair")]
    [InlineData("file")]
    [InlineData("payload")]
    [InlineData("recent")]
    [InlineData("incomplete")]
    [InlineData("busy")]
    public async Task ConfirmedInstallationRemnantsStillRequireCompleteInactiveAgedEvidence(string protection)
    {
        var registration = @"HKEY_USERS\" + Sid + "\\" + UninstallResources.UninstallBranch + "\\Tool";
        var directory = InstallationRemnant(Path.Combine(_root, "Profile", "AppData", "Local", "Programs", "Tool"), registration);
        switch (protection)
        {
            case "registration": _registry.Set(Arp, false, new CleanerRegistryValue("InstallLocation", directory, RegistryValueKind.String)); break;
            case "repair": _registry.Set(Arp, false, new CleanerRegistryValue("InstallSource", directory, RegistryValueKind.String)); break;
            case "file": _files.Add(Path.Combine(directory, "settings.json")); break;
            case "payload": File.WriteAllText(Path.Combine(directory, "app.exe"), "installed payload"); Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-90)); break;
            case "recent": File.SetLastAccessTimeUtc(Path.Combine(directory, "settings.json"), DateTime.UtcNow); break;
            case "incomplete": _complete = false; break;
            case "busy": _busy = true; break;
        }

        Assert.Empty((await _service.ScanAsync()).Items);
        Assert.True(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("command")]
    [InlineData("product")]
    public async Task NewOwnershipEvidenceDuringBackupPreventsFolderDeletion(string activation)
    {
        var directory = InstallationRemnant();
        var scan = await _service.ScanAsync();
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Callback(() =>
        {
            if (activation == "registration") _registry.Set(Arp + "Active", false, new CleanerRegistryValue("InstallLocation", directory, RegistryValueKind.String));
            else if (activation == "command") _registry.Set(Arp + "Active", false,
                new CleanerRegistryValue("UninstallString", $"launcher.exe --data-dir=\"{directory}\"", RegistryValueKind.String));
            else _registry.Set(Arp + "Active", false,
                new("DisplayName", "Obsolete fixture app 2.0", RegistryValueKind.String),
                new CleanerRegistryValue("InstallLocation", Path.Combine(_root, "NewInstallation"), RegistryValueKind.String));
        }).Returns((byte[] bytes) => bytes);

        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([Assert.Single(scan.Items).Id]));
        Assert.True(File.Exists(Path.Combine(directory, "settings.json")));
    }

    [Fact]
    public async Task UnreadableAccountProtectsInstallationRemnantsInEveryScope()
    {
        InstallationRemnant();
        _sessions.Setup(source => source.Open(Sid)).Throws(new UnauthorizedAccessException("Fixture account unavailable"));
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains("Fixture account unavailable"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedProfileDirectoryBindingsProtectInstallationRemnants(bool programs)
    {
        const string otherSid = "S-1-5-21-1-2-3-1002";
        _sessions.Setup(source => source.GetAccounts()).Returns([new(Sid, "Current user", true), new(otherSid, "Other user", false)]);
        var local = Path.Combine(_root, "Profile", "AppData", "Local");
        var otherSession = new Mock<ICleanerRegistrySession>();
        otherSession.SetupGet(source => source.UserPrefix).Returns(@"HKEY_USERS\" + otherSid);
        otherSession.SetupGet(source => source.Reader).Returns(_registry);
        otherSession.Setup(source => source.GetFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns((Environment.SpecialFolder folder) =>
            folder == Environment.SpecialFolder.LocalApplicationData ? local : Path.Combine(_root, "OtherProfile"));
        _sessions.Setup(source => source.Open(otherSid)).Returns(otherSession.Object);
        var directory = Path.Combine(local, programs ? @"Programs\ObsoleteTool" : "ObsoleteTool");
        foreach (var sid in new[] { Sid, otherSid })
            InstallationRemnant(directory, @"HKEY_USERS\" + sid + "\\" + UninstallResources.UninstallBranch + "\\Tool");

        Assert.Empty((await _service.ScanAsync()).Items);
    }

    [Theory]
    [InlineData("Common Files")]
    [InlineData("Microsoft")]
    [InlineData("Packages")]
    [InlineData("Temp")]
    [InlineData("CrashDumps")]
    public async Task SharedAndSystemDataRootsAreExcludedFromFilesystemDiscovery(string name)
    {
        InstallationRemnant(Path.Combine(_root, "ProgramData", name));
        InstallationRemnant(Path.Combine(_root, "Profile", "AppData", "Local", name),
            @"HKEY_USERS\" + Sid + "\\" + UninstallResources.UninstallBranch + "\\Tool");
        Assert.Empty((await _service.ScanAsync()).Items);
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
    [InlineData("UninstallString", false)]
    [InlineData("QuietUninstallString", false)]
    [InlineData("ModifyPath", false)]
    [InlineData("RepairPath", false)]
    [InlineData("RepairPath", true)]
    public async Task InstallerCommandArgumentsProtectReferencedCacheFilesAndDirectories(string field, bool directoryReference)
    {
        var owned = Installer(@"Product\owned.msi", package: true);
        var unrelated = Installer("unrelated.msi");
        var launcher = Path.Combine(_root, "launcher.exe");
        File.WriteAllText(launcher, "fixture launcher");
        var argument = directoryReference ? Path.GetDirectoryName(owned)! : owned;
        _registry.Set(Arp, false,
            new CleanerRegistryValue(field, $"\"{launcher}\" /repair \"{argument}\"", RegistryValueKind.String));

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Notices);
        Assert.Equal(unrelated, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Empty(result.Errors); Assert.Equal(1, result.Removed);
        Assert.True(File.Exists(owned));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RegisteredInstallerProtectsItsTargetWithoutProtectingTheWholeCache(bool package, bool directoryReference)
    {
        var owned = Installer(package ? @"Owned\installer.msi" : "owned.msi", package);
        var orphan = Installer(package ? @"Orphan\installer.msi" : "orphan.msi", package);
        var launcher = Path.Combine(_root, "launcher.exe");
        File.WriteAllText(launcher, "fixture launcher");
        var reference = directoryReference ? Path.GetDirectoryName(owned)! + "\\" : owned;
        _registry.Set(Arp, false, new CleanerRegistryValue("RepairPath", $"\"{launcher}\" /repair \"{reference}\"", RegistryValueKind.String));

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Notices);
        Assert.Equal(orphan, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Errors);
        Assert.True(File.Exists(owned)); Assert.False(File.Exists(orphan));
    }

    [Theory]
    [InlineData(Sid, "Profile")]
    [InlineData("S-1-5-21-1-2-3-1002", "Profile-S-1-5-21-1-2-3-1002")]
    public async Task PerUserInstallerCachesAreScannedCleanedAndRecoveredInTheirOwningProfile(string sid, string profile)
    {
        const string otherSid = "S-1-5-21-1-2-3-1002";
        _sessions.Setup(source => source.GetAccounts()).Returns([new(Sid, "Current user", true), new(otherSid, "Other user", false)]);
        var root = Path.Combine(_root, profile, "AppData", "Local", "Package Cache");
        var owned = Installer("owned.msi", cacheRoot: root);
        var orphan = Installer("orphan.msi", cacheRoot: root);
        _files.Add(owned);

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Notices);
        Assert.Equal(orphan, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Errors);
        Assert.True(File.Exists(owned)); Assert.False(File.Exists(orphan));
        var backups = new UninstallBackupStore(_vault.Object, new(_sessions.Object, _folders.Object, Mock.Of<ICleanerShortcutReader>()));
        Assert.Empty((await backups.RestoreAsync(result.BackupId!.Value, default)).Errors);
        Assert.True(File.Exists(orphan));
        _sessions.Verify(source => source.Open(sid), Times.AtLeastOnce);
    }

    [Fact]
    public async Task UnreadableProfilePreservesBundleCachesAndFoldersWithoutHidingNativeMsiOrphans()
    {
        var orphan = Installer("orphan.msi");
        var owned = Installer("owned.msi");
        var bundle = Installer("bundle.exe", package: true);
        var directory = InstallationRemnant();
        _files.Add(owned);
        _sessions.Setup(source => source.Open(Sid)).Throws(new UnauthorizedAccessException("Fixture profile cannot be read"));

        var scan = await _service.ScanAsync();

        Assert.Equal(orphan, Assert.Single(scan.Items).Location);
        Assert.Contains(scan.Notices, notice => notice.Contains("Fixture profile cannot be read"));
        Assert.Contains(scan.Notices, notice => notice.Contains("account coverage is incomplete"));
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Equal(1, result.Removed); Assert.Empty(result.Errors);
        Assert.True(File.Exists(owned)); Assert.True(File.Exists(bundle)); Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void PerUserCacheBoundaryRejectsDirectoriesOutsideTheBoundLocalAppData()
    {
        var path = Path.Combine(PackageCache, "unrelated.msi");
        var root = new UninstallFileRoot(path, Sid, Environment.SpecialFolder.LocalApplicationData, PackageCache, true, InstallerCache: true);
        Assert.Throws<IOException>(() => UninstallResources.ValidateRoot(root, _sessions.Object, _folders.Object));
    }

    [Fact]
    public async Task AppExecutionAliasUninstallersKeepRegistrationsAndAllowUnrelatedLeftoverCleanup()
    {
        using var alias = new TestAppExecutionAlias();
        var executable = Path.Combine(_root, "app.exe");
        File.WriteAllText(executable, "installed fixture runtime");
        var userRegistration = @"HKEY_USERS\" + Sid + "\\" + UninstallResources.UninstallBranch + "\\ManagedRuntime";
        Registration(userRegistration);
        _registry.Set(userRegistration, false,
            new("DisplayIcon", executable, RegistryValueKind.String),
            new CleanerRegistryValue("UninstallString", $"\"{alias.Path}\" uninstall --yes --by-id \"fixture-runtime\"", RegistryValueKind.String));
        var orphan = Installer("orphan.msi");

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Notices);
        Assert.Equal(orphan, Assert.Single(scan.Items).Location);
        var result = await _service.RemoveAsync([scan.Items[0].Id]);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Removed);
        Assert.True(_registry.KeyExists(userRegistration, false));
        Assert.True(File.Exists(alias.Path));
    }

    [Fact]
    public async Task UnsupportedLauncherReparseTagsStillProtectInstallerFiles()
    {
        using var alias = new TestAppExecutionAlias(0x800000FF);
        _registry.Set(Arp, false,
            new CleanerRegistryValue("UninstallString", $"\"{alias.Path}\" uninstall", RegistryValueKind.String));
        var orphan = Installer("orphan.msi");

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains(alias.Path) && notice.Contains("1920"));
        Assert.Contains(scan.Notices, notice => notice.Contains("reference coverage is incomplete"));
        Assert.True(File.Exists(orphan));
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

    [Theory]
    [InlineData("PendingFileRenameOperations", "")]
    [InlineData("PendingFileRenameOperations", "*1")]
    [InlineData("PendingFileRenameOperations2", "*2")]
    public async Task QueuedFileDeletionProtectsItsCacheTreeWithoutBlockingUnrelatedLeftovers(string value, string prefix)
    {
        var protectedFile = Installer(@"Queued\owned.msi", package: true);
        var unrelated = Installer("unrelated.msi");
        Registration();
        var queuedDirectory = Path.GetDirectoryName(protectedFile)!;
        _registry.Set(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", false,
            new CleanerRegistryValue(value, new[] { prefix + @"\??\" + queuedDirectory, "" }, RegistryValueKind.MultiString));

        var scan = await _service.ScanAsync();

        Assert.DoesNotContain(scan.Items, item => item.Location == protectedFile);
        Assert.Contains(scan.Items, item => item.Location == unrelated);
        Assert.Contains(scan.Items, item => item.Kind == LeftoverKind.Registration);
        Assert.DoesNotContain(scan.Notices, notice => notice.Contains("installation or restart is pending"));
        var result = await _service.RemoveAsync(scan.Items.Select(item => item.Id).ToArray());
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(protectedFile));
        Assert.False(File.Exists(unrelated));
    }

    [Fact]
    public async Task QueuedRenameProtectsItsSourceDestinationAndApplicationFolder()
    {
        var source = Installer("source.msi");
        var destination = Installer("destination.msi");
        var remnant = InstallationRemnant();
        _registry.Set(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", false,
            new CleanerRegistryValue("PendingFileRenameOperations", new[] { @"\??\" + source, @"!\??\" + destination,
                @"\??\" + Path.GetDirectoryName(remnant)!, "" }, RegistryValueKind.MultiString));

        var scan = await _service.ScanAsync();

        Assert.Empty(scan.Items);
        Assert.Empty(scan.Notices);
    }

    [Fact]
    public async Task EmptyFileOperationQueueDoesNotBlockInstallerCleanup()
    {
        var file = Installer("unrelated.msi");
        _registry.Set(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", false,
            new CleanerRegistryValue("PendingFileRenameOperations", Array.Empty<string>(), RegistryValueKind.MultiString));
        var scan = await _service.ScanAsync();
        Assert.Equal(file, Assert.Single(scan.Items).Location);
        Assert.Empty(scan.Notices);
    }

    [Fact]
    public async Task QueuedFileOperationAppearingDuringBackupPreventsDeletion()
    {
        var file = Installer("protected.msi");
        var scan = await _service.ScanAsync();
        _vault.Setup(source => source.EnsureDirectory(It.IsAny<string>())).Callback((string path) =>
        {
            Directory.CreateDirectory(path);
            _registry.Set(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", false,
                new CleanerRegistryValue("PendingFileRenameOperations", new[] { @"\??\" + file, "" }, RegistryValueKind.MultiString));
        });

        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync(scan.Items.Select(item => item.Id).ToArray()));

        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Installer\InProgress")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")]
    public async Task GenuineInstallationAndRestartMarkersContinueToBlockCleanup(string key)
    {
        Installer("protected.msi");
        Registration();
        _registry.Set(@"HKEY_LOCAL_MACHINE\" + key, false);
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains("installation or restart is pending"));
    }

    [Theory]
    [InlineData("incomplete", RegistryValueKind.MultiString)]
    [InlineData("malformed", RegistryValueKind.MultiString)]
    [InlineData("wrong type", RegistryValueKind.String)]
    public async Task UnreadableFileOperationQueueCannotAuthorizeCleanup(string scenario, RegistryValueKind kind)
    {
        Installer("protected.msi");
        Registration();
        object data = scenario switch
        {
            "incomplete" => new[] { @"\??\" + Path.Combine(_root, "source") },
            "malformed" => new[] { @"\Device\Unknown\source", "" },
            _ => "not a multi-string",
        };
        _registry.Set(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", false,
            new CleanerRegistryValue("PendingFileRenameOperations", data, kind));
        var scan = await _service.ScanAsync();
        Assert.Empty(scan.Items);
        Assert.Contains(scan.Notices, notice => notice.Contains("PendingFileRenameOperations"));
        Assert.DoesNotContain(scan.Notices, notice => notice.Contains("installation or restart is pending"));
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
    public async Task InstallerCommandReferenceAppearingDuringBackupPreventsDeletion()
    {
        var path = Installer("repair.msi");
        var launcher = Path.Combine(_root, "launcher.exe");
        File.WriteAllText(launcher, "fixture launcher");
        var scan = await _service.ScanAsync();
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Callback(() => _registry.Set(Arp, false,
            new CleanerRegistryValue("RepairPath", $"\"{launcher}\" /repair \"{path}\"", RegistryValueKind.String))).Returns((byte[] bytes) => bytes);

        await Assert.ThrowsAsync<IOException>(() => _service.RemoveAsync([Assert.Single(scan.Items).Id]));
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
