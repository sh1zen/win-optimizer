using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.SoftwareApps;

/// <summary>Exercises the production capture/inspect/backup/delete/recovery path against isolated external boundaries.</summary>
public sealed class UninstallResidueServiceTests : IDisposable
{
    private const string First = "S-1-5-21-1-2-3-1001", Second = "S-1-5-21-1-2-3-1002", Service = "S-1-5-20";
    private const string Arp = @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Uninstall\Recuva";
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "uninstall-residue-test-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryStore _registry = new();
    private readonly Dictionary<string, string> _profiles = [];
    private readonly HashSet<string> _unavailable = [];
    private readonly HashSet<string> _open = [];
    private readonly Mock<IUninstallAccountSessions> _sessions = new();
    private readonly Mock<IUninstallRegistryProfiles> _catalogue = new();
    private readonly Mock<IUninstallPackageSource> _packages = new();
    private readonly Mock<IUninstallProcessSource> _processes = new();
    private readonly Mock<ICleanerBackupVault> _vault = new();
    private readonly UninstallResidueService _sut;
    private readonly string _executable;
    private readonly ItemDefinition _app = new() { Id = "recuva", Name = "Recuva", Description = "Selected application" };

    public UninstallResidueServiceTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var sid in new[] { First, Second, Service })
            _profiles[sid] = Directory.CreateDirectory(Path.Combine(_root, sid)).FullName;
        _catalogue.Setup(source => source.Read()).Returns(() => _profiles.Select(pair => new CleanerRegistryProfile(pair.Key, pair.Key, pair.Value)).ToArray());
        _sessions.Setup(source => source.Open(It.IsAny<string?>())).Returns((string? sid) => Open(sid));
        _packages.Setup(source => source.GetPackages(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>())).Returns([]);
        _processes.Setup(source => source.FindRunningExecutables(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<ICollection<string>>())).Returns([]);
        var folders = new Mock<IUninstallScanFolders>();
        folders.SetupGet(source => source.ApplicationDirectories).Returns([Path.Combine(_root, "ProgramData"), Path.Combine(_root, "Programs")]);
        folders.SetupGet(source => source.ShortcutDirectories).Returns([]);
        folders.SetupGet(source => source.WindowsDirectory).Returns(Path.Combine(_root, "Windows"));
        _vault.SetupGet(source => source.RootDirectory).Returns(Path.Combine(_root, "vault"));
        _vault.Setup(source => source.EnsureDirectory(It.IsAny<string>())).Callback((string path) => Directory.CreateDirectory(path));
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Returns((byte[] data) => data);
        _vault.Setup(source => source.Unseal(It.IsAny<byte[]>())).Returns((byte[] data) => data);
        var mode = new Mock<IApplicationModeService>();
        mode.Setup(source => source.BeginSystemOperation()).Returns(() => new Scope(() => { }));
        _sut = new(_sessions.Object, _catalogue.Object, _packages.Object, folders.Object, _processes.Object,
            Mock.Of<ICleanerShortcutReader>(), _vault.Object, mode.Object, new DeadlineClock());
        _executable = Write(Path.Combine(_root, "Programs", "Recuva", "recuva.exe"), "executable");
        _registry.Set(Arp, false, new("DisplayName", "Recuva", RegistryValueKind.String), new("DisplayIcon", _executable, RegistryValueKind.String));
    }

    private ICleanerRegistrySession Open(string? sid)
    {
        if (sid != null && _unavailable.Contains(sid)) throw new UnauthorizedAccessException("Test profile is unavailable.");
        if (sid != null && !_open.Add(sid)) throw new IOException("An offline profile must not be mounted twice.");
        var session = new Mock<ICleanerRegistrySession>();
        session.SetupGet(value => value.UserPrefix).Returns(sid == null ? "HKEY_CURRENT_USER" : @"HKEY_USERS\" + sid);
        session.SetupGet(value => value.Reader).Returns(_registry);
        session.SetupGet(value => value.Editor).Returns(_registry);
        session.Setup(value => value.GetFolderPath(It.IsAny<Environment.SpecialFolder>())).Returns((Environment.SpecialFolder folder) =>
            folder == Environment.SpecialFolder.UserProfile ? _profiles[sid ?? First] : Path.Combine(_profiles[sid ?? First], folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => @"AppData\Local",
                Environment.SpecialFolder.ApplicationData => @"AppData\Roaming",
                Environment.SpecialFolder.DesktopDirectory => "Desktop",
                Environment.SpecialFolder.Programs => @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs",
                _ => throw new ArgumentOutOfRangeException(nameof(folder))
            }));
        session.Setup(value => value.Dispose()).Callback(() => { if (sid != null) _open.Remove(sid); });
        return session.Object;
    }

    private static string Write(string path, string data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, data);
        return path;
    }
    private string Local(string sid, string relative) => Path.Combine(_profiles[sid], @"AppData\Local", relative);
    private void NativeRemove() { File.Delete(_executable); _registry.Remove(Arp, false); }
    private Task<UninstallResidueScan> Inspect(WinOptimizer.Core.Features.SoftwareApps.Interfaces.IUninstallResidueSession session,
        bool success = true, bool deferred = false) => session.InspectAsync([new(_app.Id, success, deferred, null)], null, default);

    [Fact]
    public async Task ScansEveryAccountIncludingServiceProfiles_AndOnlyProductSpecificFolders()
    {
        foreach (var sid in _profiles.Keys)
        {
            Write(Local(sid, @"Piriform\Recuva\settings.ini"), "residual");
            Write(Local(sid, @"Piriform\Sibling\keep.ini"), "keep");
            _registry.Set(@"HKEY_USERS\" + sid + @"\Software\Piriform\Recuva", false, new CleanerRegistryValue("", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary));
            _registry.Set(@"HKEY_USERS\" + sid + @"\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", true,
                new CleanerRegistryValue("OldRecuva", '"' + _executable + '"', RegistryValueKind.String));
        }
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        Assert.Equal(UninstallVerificationState.Removed, Assert.Single(scan.Applications).State);
        foreach (var sid in _profiles.Keys)
        {
            Assert.Contains(scan.Findings, item => item.Path == Local(sid, @"Piriform\Recuva\settings.ini") && item.CanDelete);
            Assert.Contains(scan.Findings, item => item.Account.Contains(sid) && item.Kind == UninstallArtifactKind.RegistryValue && item.View32);
        }
        Assert.DoesNotContain(scan.Findings, item => item.Path.Contains("Sibling") || item.Path.EndsWith("Piriform"));
        Assert.Empty(_open);
    }

    [Theory]
    [InlineData(false, false, UninstallVerificationState.Unknown)]
    [InlineData(true, true, UninstallVerificationState.Deferred)]
    public async Task FailedOrDeferredNativeRemovalCannotAuthorizeCleanup(bool success, bool deferred, UninstallVerificationState state)
    {
        Write(Local(First, @"Recuva\settings"), "residual");
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session, success, deferred);
        Assert.Equal(state, scan.Applications[0].State);
        Assert.All(scan.Findings, item => Assert.False(item.CanDelete));
        await Assert.ThrowsAsync<IOException>(() => session.CleanAsync([scan.Findings[0].Id], null, default));
    }

    [Fact]
    public async Task RunningProcessProtectsResources_RescanCanVerifyItsExitWithoutChangingNativeOutcome()
    {
        Write(Local(First, @"Recuva\settings"), "residual");
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        _processes.Setup(source => source.FindRunningExecutables(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<ICollection<string>>())).Returns([_executable]);
        var scan = await Inspect(session);
        Assert.Equal(UninstallVerificationState.StillInstalled, scan.Applications[0].State);
        Assert.All(scan.Findings, item => Assert.False(item.CanDelete));
        _processes.Setup(source => source.FindRunningExecutables(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<ICollection<string>>())).Returns([]);
        scan = await session.RescanAsync(null, default);
        Assert.Equal(UninstallVerificationState.Removed, scan.Applications[0].State);
        Assert.All(scan.Findings, item => Assert.True(item.CanDelete));
    }

    [Fact]
    public async Task RemainingInstallationInAnotherAccountProtectsThatAccountAndSharedResources()
    {
        var first = Write(Local(First, @"Recuva\settings"), "removed account");
        var second = Write(Local(Second, @"Recuva\settings"), "installed account");
        var activeExe = Write(Local(Second, @"Recuva\recuva.exe"), "binary");
        _registry.Set(@"HKEY_USERS\" + Second + @"\Software\Microsoft\Windows\CurrentVersion\Uninstall\Recuva", false,
            new("DisplayName", "Recuva", RegistryValueKind.String), new("DisplayIcon", activeExe, RegistryValueKind.String));
        var common = Write(Path.Combine(_root, @"ProgramData\Recuva\data"), "shared");
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        Assert.Equal(UninstallVerificationState.StillInstalled, scan.Applications[0].State);
        Assert.True(scan.Findings.Single(item => item.Path == first).CanDelete);
        Assert.False(scan.Findings.Single(item => item.Path == second).CanDelete);
        Assert.False(scan.Findings.Single(item => item.Path == common).CanDelete);
    }

    [Fact]
    public async Task InitiallyUnavailableProfileIsRevisited_AndNewProductLocationsAreDiscovered()
    {
        _unavailable.Add(Second);
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var path = Write(Local(Second, @"Recuva\created-during-uninstall"), "residual");
        _unavailable.Clear();
        var scan = await Inspect(session);
        Assert.Equal(UninstallVerificationState.Removed, scan.Applications[0].State);
        Assert.True(scan.Findings.Single(item => item.Path == path).CanDelete);
    }

    [Fact]
    public async Task SelectedFilesAndTypedDefaultValuesHaveDurableBackup_AndRestoreWithoutOverwritingSiblings()
    {
        var selected = Write(Local(First, @"Recuva\selected"), "original");
        var sibling = Write(Local(First, @"Recuva\keep"), "unselected");
        var key = @"HKEY_USERS\" + First + @"\Software\Piriform\Recuva";
        _registry.Set(key, false, new("", new byte[] { 4, 5, 6 }, RegistryValueKind.Binary), new("keep", 42, RegistryValueKind.DWord));
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        var ids = scan.Findings.Where(item => item.Path == selected || item.Path == key && item.ValueName == "").Select(item => item.Id).ToArray();
        Assert.Equal(2, ids.Length);
        var result = await session.CleanAsync(ids, null, default);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Removed.Count);
        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(sibling));
        Assert.Null(_registry.Value(key, false, ""));
        Assert.Equal(42, _registry.Value(key, false, "keep")!.Data);
        Assert.Equal(result.BackupId, Assert.Single(await _sut.GetBackupsAsync(default)).Id);
        var restored = await _sut.RestoreAsync(result.BackupId!.Value, default);
        Assert.Empty(restored.Errors);
        Assert.Equal(2, restored.Restored);
        Assert.Equal("original", File.ReadAllText(selected));
        Assert.Equal(RegistryValueKind.Binary, _registry.Value(key, false, "")!.Kind);
        Assert.Equal(new byte[] { 4, 5, 6 }, (byte[])_registry.Value(key, false, "")!.Data);
    }

    [Fact]
    public async Task SelectedParentsNeverRecursivelyDeleteUnselectedChildren()
    {
        var path = Write(Local(First, @"Recuva\keep"), "unselected");
        var key = @"HKEY_USERS\" + First + @"\Software\Recuva";
        _registry.Set(key, false, new CleanerRegistryValue("keep", "value", RegistryValueKind.String));
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        var parents = scan.Findings.Where(item => item.Path == Path.GetDirectoryName(path) && item.Kind == UninstallArtifactKind.Directory
            || item.Path == key && item.Kind == UninstallArtifactKind.RegistryKey).Select(item => item.Id).ToArray();
        Assert.Equal(2, parents.Length);
        var result = await session.CleanAsync(parents, null, default);
        Assert.Empty(result.Removed);
        Assert.Equal(2, result.Errors.Count);
        Assert.True(File.Exists(path));
        Assert.NotNull(_registry.Value(key, false, "keep"));
    }

    [Fact]
    public async Task BackupFailurePreventsEveryDeletion()
    {
        var path = Write(Local(First, @"Recuva\selected"), "original");
        var key = @"HKEY_USERS\" + First + @"\Software\Recuva";
        _registry.Set(key, false, new CleanerRegistryValue("keep", "original", RegistryValueKind.String));
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        _vault.Setup(source => source.Seal(It.IsAny<byte[]>())).Throws(new IOException("Test vault write failure."));
        await Assert.ThrowsAsync<IOException>(() => session.CleanAsync(scan.Findings.Select(item => item.Id).ToArray(), null, default));
        Assert.True(File.Exists(path));
        Assert.NotNull(_registry.Value(key, false, "keep"));
        Assert.Empty(await _sut.GetBackupsAsync(default));
    }

    [Fact]
    public async Task ChangedEvidenceAndUnknownIdsFailBeforeMutation()
    {
        var path = Write(Local(First, @"Recuva\selected"), "original");
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        Write(path, "modified and longer");
        await Assert.ThrowsAsync<IOException>(() => session.CleanAsync([scan.Findings.Single(item => item.Path == path).Id], null, default));
        await Assert.ThrowsAsync<IOException>(() => session.CleanAsync([Guid.NewGuid()], null, default));
        Assert.Equal("modified and longer", File.ReadAllText(path));
        Assert.Empty(await _sut.GetBackupsAsync(default));
    }

    [Fact]
    public async Task CorruptPayloadStopsRecoveryBeforeAnyRegistryOrFileMutation()
    {
        var path = Write(Local(First, @"Recuva\selected"), "original");
        var key = @"HKEY_USERS\" + First + @"\Software\Recuva";
        _registry.Set(key, false, new CleanerRegistryValue("selected", 7, RegistryValueKind.DWord));
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        var result = await session.CleanAsync(scan.Findings.Where(item => item.Path == path || item.ValueName == "selected").Select(item => item.Id).ToArray(), null, default);
        Assert.Empty(result.Errors);
        var directory = Path.Combine(_root, "vault", result.BackupId!.Value.ToString("N"));
        File.WriteAllText(Assert.Single(Directory.GetFiles(directory, "*.bin")), "corrupt");
        await Assert.ThrowsAsync<IOException>(() => _sut.RestoreAsync(result.BackupId.Value, default));
        Assert.False(File.Exists(path));
        Assert.Null(_registry.Value(key, false, "selected"));
    }

    [Fact]
    public async Task RecoveryPreservesConflictingDestination_AndCancelledCleanupDoesNotMutate()
    {
        var path = Write(Local(First, @"Recuva\selected"), "original");
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        var id = scan.Findings.Single(item => item.Path == path).Id;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CleanAsync([id], null, cancelled.Token));
        Assert.True(File.Exists(path));
        var result = await session.CleanAsync([id], null, default);
        Write(path, "new data");
        var recovery = await _sut.RestoreAsync(result.BackupId!.Value, default);
        Assert.Single(recovery.Errors);
        Assert.Equal(0, recovery.Restored);
        Assert.Equal("new data", File.ReadAllText(path));
    }

    [Fact]
    public async Task AppxQueriesEverySid_AndFindsUnregisteredPackageFamilyData()
    {
        var app = _app with { AppxPackageName = ["Vendor.Recuva"] };
        _packages.Setup(source => source.GetPackages(First, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns([new("Vendor.Recuva", "Vendor.Recuva_123456789abcd", Path.GetDirectoryName(_executable)!)]);
        var path = Write(Local(Service, @"Packages\Vendor.Recuva_123456789abcd\LocalState\data"), "residual");
        Write(Local(Service, @"Packages\Vendor.RecuvaOther_123456789abcd\LocalState\keep"), "keep");
        await using var session = await _sut.CaptureAsync([app], null, default);
        NativeRemove();
        _packages.Setup(source => source.GetPackages(First, It.IsAny<IReadOnlyCollection<string>>())).Returns([]);
        var scan = await Inspect(session);
        Assert.True(scan.Findings.Single(item => item.Path == path).CanDelete);
        Assert.DoesNotContain(scan.Findings, item => item.Path.Contains("RecuvaOther"));
        foreach (var sid in _profiles.Keys)
            _packages.Verify(source => source.GetPackages(sid, It.IsAny<IReadOnlyCollection<string>>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task CancellationAfterFirstDeletionReturnsPartialResultsWithRecoverableBackup()
    {
        var first = Write(Local(First, @"Recuva\one"), "one");
        var second = Write(Local(First, @"Recuva\two"), "two");
        await using var session = await _sut.CaptureAsync([_app], null, default);
        NativeRemove();
        var scan = await Inspect(session);
        using var cancellation = new CancellationTokenSource();
        var progress = new Mock<IProgress<UninstallResidueProgress>>();
        progress.Setup(progress => progress.Report(It.IsAny<UninstallResidueProgress>())).Callback((UninstallResidueProgress value) =>
        {
            if (value.Total == 2 && value.Completed == 1) cancellation.Cancel();
        });
        var result = await session.CleanAsync(scan.Findings.Where(item => item.Path == first || item.Path == second).Select(item => item.Id).ToArray(), progress.Object, cancellation.Token);
        Assert.Single(result.Removed);
        Assert.Single(result.Errors);
        Assert.NotNull(result.BackupId);
        Assert.NotEqual(File.Exists(first), File.Exists(second));
        var restored = await _sut.RestoreAsync(result.BackupId.Value, default);
        Assert.Empty(restored.Errors);
        Assert.Equal("one", File.ReadAllText(first));
        Assert.Equal("two", File.ReadAllText(second));
    }

    public void Dispose()
    {
        Assert.Empty(_open);
        var full = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("uninstall-residue-test-", Path.GetFileName(full));
        Directory.Delete(full, true);
    }

    private sealed class Scope(Action release) : IDisposable { public void Dispose() => release(); }
    // Each observation advances beyond the deadline, so busy-process tests never sleep on wall-clock time.
    private sealed class DeadlineClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, TimeSpan.FromSeconds(31).Ticks);
    }

    /// <summary>An in-memory registry boundary retaining actual value kinds, account paths and independent views.</summary>
    internal sealed class RegistryStore : ICleanerRegistryReader, ICleanerRegistryEditor
    {
        private readonly Dictionary<string, Dictionary<string, CleanerRegistryValue>> _keys = new(StringComparer.OrdinalIgnoreCase);
        private static string Key(string path, bool view) => (view ? "32:" : "64:") + path;
        public void Set(string path, bool view, params CleanerRegistryValue[] values)
        {
            var parts = path.Split('\\');
            for (int count = 1; count <= parts.Length; count++) _keys.TryAdd(Key(string.Join("\\", parts.Take(count)), view), new(StringComparer.OrdinalIgnoreCase));
            foreach (var value in values) _keys[Key(path, view)][value.Name] = value;
        }
        public void Remove(string path, bool view) { foreach (var key in _keys.Keys.Where(key => key.Equals(Key(path, view), StringComparison.OrdinalIgnoreCase) || key.StartsWith(Key(path, view) + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) _keys.Remove(key); }
        public CleanerRegistryValue? Value(string path, bool view, string name) => _keys.GetValueOrDefault(Key(path, view))?.GetValueOrDefault(name);
        public bool KeyExists(string path, bool view32) => _keys.ContainsKey(Key(path, view32));
        public string[] GetSubKeyNames(string path, bool view32) => _keys.Keys.Where(key => key.StartsWith(Key(path, view32) + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(key => key[(Key(path, view32).Length + 1)..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        public IReadOnlyList<CleanerRegistryValue> GetValues(string path, bool view32, CancellationToken ct, ICollection<string>? notices = null) => _keys.GetValueOrDefault(Key(path, view32))?.Values.ToArray() ?? [];
        public ICleanerRegistryKey OpenKey(string path, bool view32, bool writable) => KeyExists(path, view32) ? new Handle(this, path, view32) : throw new IOException("Missing registry parent.");
        private sealed class Handle(RegistryStore owner, string path, bool view) : ICleanerRegistryKey
        {
            public CleanerRegistryValue? ReadValue(string name) => owner.Value(path, view, name);
            public void DeleteValue(string name) { if (!owner._keys[Key(path, view)].Remove(name)) throw new IOException("Missing value."); }
            public void SetValue(CleanerRegistryValue value) => owner.Set(path, view, value);
            public void DeleteEmptySubKey(string name)
            {
                var child = path + "\\" + name;
                if (owner.GetSubKeyNames(child, view).Length > 0 || owner.GetValues(child, view, default).Count > 0) throw new IOException("Unselected children remain.");
                owner.Remove(child, view);
            }
            public ICleanerRegistryKey CreateSubKey(string name) { owner.Set(path + "\\" + name, view); return new Handle(owner, path + "\\" + name, view); }
            public void Dispose() { }
        }
    }
}
