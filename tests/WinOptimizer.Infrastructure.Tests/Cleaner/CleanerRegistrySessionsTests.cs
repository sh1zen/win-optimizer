using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public sealed class CleanerRegistrySessionsTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cleaner-accounts-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ICleanerRegistryProfiles> _profiles = new();
    private readonly Mock<ICleanerRegistryHiveAccess> _native = new();
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly WindowsCleanerRegistrySessions _sessions;

    public CleanerRegistrySessionsTests()
    {
        _profiles.Setup(profiles => profiles.Find(Sid)).Returns(new CleanerRegistryProfile(Sid, "Selected account", _root));
        _sessions = new(_user.Object, _profiles.Object, _native.Object);
    }

    [Fact]
    public void OpeningARegisteredAccountDoesNotLoadItsHiveUntilRead()
    {
        using var session = _sessions.Open(Sid);
        Assert.Equal(@"HKEY_USERS\" + Sid, session.UserPrefix);
        Assert.Equal(_root, session.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _native.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    public void UninstallServiceProfilesShareTheNativeHiveOwnerWithoutChangingCleanerAccountSelection(string sid)
    {
        var profiles = new Mock<IUninstallRegistryProfiles>();
        profiles.Setup(source => source.Find(sid)).Returns(new CleanerRegistryProfile(sid, "Windows service", _root));
        _native.Setup(source => source.IsLoaded(sid)).Returns(true);
        var uninstall = new WindowsUninstallAccountSessions(_sessions, profiles.Object, _user.Object);
        using (var session = uninstall.Open(sid))
        {
            Assert.Equal(@"HKEY_USERS\" + sid, session.UserPrefix);
            session.Reader.GetSubKeyNames(@"HKEY_USERS\" + sid + @"\Software\Vendor", false);
        }
        _native.Verify(source => source.OpenKey(@"HKEY_USERS\" + sid + @"\Software\Vendor", false, false), Times.Once);
        _native.Verify(source => source.Mount(It.IsAny<string>()), Times.Never);
        _native.Verify(source => source.Unmount(It.IsAny<string>()), Times.Never);
        Assert.Throws<IOException>(() => _sessions.Open(sid));
    }

    [Fact]
    public void InventoryLabelsTheInteractiveAccountWithoutLoadingHives()
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        _user.SetupGet(user => user.InteractiveUserSid).Returns(Sid);
        _profiles.Setup(profiles => profiles.Read()).Returns([new(Sid, "Selected account", _root), new("S-1-5-21-1-2-3-1002", "Another account", _root)]);
        var accounts = _sessions.GetAccounts();
        Assert.True(accounts[0].IsCurrent);
        Assert.False(accounts[1].IsCurrent);
        _native.VerifyNoOtherCalls();
    }

    [Fact]
    public void LiveAccountReadsItsClassesHive_AndNeverUnmountsWindowsOwnedHives()
    {
        _native.Setup(native => native.IsLoaded(It.IsAny<string>())).Returns(true);
        using (var session = _sessions.Open(Sid))
        {
            session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Classes\CLSID", true);
            session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Microsoft", false);
        }
        _native.Verify(native => native.OpenKey(@"HKEY_USERS\" + Sid + @"_Classes\CLSID", true, false), Times.Once);
        _native.Verify(native => native.OpenKey(@"HKEY_USERS\" + Sid + @"\Software\Microsoft", false, false), Times.Once);
        _native.Verify(native => native.Mount(It.IsAny<string>()), Times.Never);
        _native.Verify(native => native.Unmount(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OfflineHivesAreMountedOncePerSession_AndBothReleasedInReverseOrder()
    {
        Directory.CreateDirectory(Path.Combine(_root, @"AppData\Local\Microsoft\Windows"));
        var userHive = Path.Combine(_root, "NTUSER.DAT");
        var classesHive = Path.Combine(_root, @"AppData\Local\Microsoft\Windows\UsrClass.dat");
        File.WriteAllBytes(userHive, []);
        File.WriteAllBytes(classesHive, []);
        _native.Setup(native => native.Mount(userHive)).Returns("owned-user");
        _native.Setup(native => native.Mount(classesHive)).Returns("owned-classes");
        List<string> released = [];
        _native.Setup(native => native.Unmount(It.IsAny<string>())).Callback<string>(released.Add);
        using (var session = _sessions.Open(Sid))
        {
            session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Microsoft", false);
            session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Microsoft", false);
            session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Classes\CLSID", false);
            session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Classes\CLSID", true);
        }
        _native.Verify(native => native.Mount(userHive), Times.Once);
        _native.Verify(native => native.Mount(classesHive), Times.Once);
        _native.Verify(native => native.OpenKey(@"HKEY_USERS\owned-user\Software\Microsoft", false, false), Times.Exactly(2));
        _native.Verify(native => native.OpenKey(@"HKEY_USERS\owned-classes\CLSID", false, false), Times.Once);
        _native.Verify(native => native.OpenKey(@"HKEY_USERS\owned-classes\Wow6432Node\CLSID", false, false), Times.Once);
        Assert.Equal(new[] { "owned-classes", "owned-user" }, released);
    }

    [Fact]
    public void SelectedProfileFoldersAndCompactionUseTheRegisteredAccount_AndReleaseItsOfflineHive()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "NTUSER.DAT");
        File.WriteAllBytes(original, []);
        _native.Setup(native => native.Mount(original)).Returns("owned-user");
        using (var session = _sessions.Open(Sid))
        {
            Assert.Equal(Path.Combine(_root, "Desktop"), session.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Assert.Equal(Path.Combine(_root, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs"), session.GetFolderPath(Environment.SpecialFolder.Programs));
            Assert.Equal(Path.Combine(_root, @"AppData\Roaming\Microsoft\Windows\Start Menu"), session.GetFolderPath(Environment.SpecialFolder.StartMenu));
            Assert.Equal(new CleanerRegistryHive(@"HKEY_USERS\owned-user", original), session.GetUserHive());
        }
        _native.Verify(native => native.Mount(original), Times.Once);
        _native.Verify(native => native.Unmount("owned-user"), Times.Once);
    }

    [Fact]
    public void OfflineCompactionReopensItsNativeMount_AndKeepsItAliveThroughBackupAndCompression()
    {
        Directory.CreateDirectory(_root);
        var original = Path.Combine(_root, "NTUSER.DAT");
        File.WriteAllBytes(original, new byte[16384]);
        _native.SetupSequence(native => native.Mount(original)).Returns("scan-mount").Returns("apply-mount");
        var hives = new Mock<IRegistryHiveOperations>();
        hives.SetupGet(hives => hives.IsAvailable).Returns(true);
        hives.Setup(hives => hives.Save(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, path) => File.WriteAllBytes(path, new byte[4096]));
        hives.Setup(hives => hives.Compact(@"HKEY_USERS\apply-mount")).Callback(() =>
        {
            _native.Verify(native => native.Unmount("apply-mount"), Times.Never);
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "vault"), "hive-*.hiv"));
        });
        var compactor = new RegistryHiveCompactor(_sessions, new TestCleanerBackupVault(Path.Combine(_root, "vault")), hives.Object);
        var result = compactor.Scan(new() { RegistryAccountSids = [Sid] }, default);
        var target = Assert.IsType<CleanerHiveTarget>(Assert.Single(result.Findings).Target);
        Assert.Equal(@"HKEY_USERS\" + Sid, target.KeyPath);
        Assert.Equal(original, target.SourcePath);
        compactor.Clean(target, default);
        hives.Verify(hives => hives.Save(@"HKEY_USERS\scan-mount", It.IsAny<string>()), Times.Once);
        hives.Verify(hives => hives.Save(@"HKEY_USERS\apply-mount", It.IsAny<string>()), Times.Once);
        hives.Verify(hives => hives.Compact(@"HKEY_USERS\apply-mount"), Times.Once);
        _native.Verify(native => native.Unmount("scan-mount"), Times.Once);
        _native.Verify(native => native.Unmount("apply-mount"), Times.Once);
    }

    [Fact]
    public void UnmountFailureDoesNotPreventReleasingOtherOwnedHives()
    {
        Directory.CreateDirectory(Path.Combine(_root, @"AppData\Local\Microsoft\Windows"));
        File.WriteAllBytes(Path.Combine(_root, "NTUSER.DAT"), []);
        File.WriteAllBytes(Path.Combine(_root, @"AppData\Local\Microsoft\Windows\UsrClass.dat"), []);
        _native.Setup(native => native.Mount(It.IsAny<string>())).Returns<string>(file => file.EndsWith("NTUSER.DAT") ? "user" : "classes");
        _native.Setup(native => native.Unmount("classes")).Throws(new IOException("busy hive"));
        var session = _sessions.Open(Sid);
        session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Microsoft", false);
        session.Reader.GetSubKeyNames(@"HKEY_USERS\" + Sid + @"\Software\Classes\CLSID", false);
        Assert.Throws<IOException>(session.Dispose);
        _native.Verify(native => native.Unmount("user"), Times.Once);
    }

    [Theory]
    [InlineData("relative\\profile")]
    [InlineData("..\\profile")]
    [InlineData("\\\\server\\profile")]
    [InlineData("%USERPROFILE%")]
    [InlineData("")]
    public void ProfileRegistrationRejectsUnresolvedOrNonlocalPaths(string path) =>
        Assert.Throws<IOException>(() => WindowsCleanerRegistryProfiles.ValidateProfileDirectory(path));

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-1-2-3-1001\\Software")]
    public void InvalidAccountsAreRejectedBeforeProfileOrNativeAccess(string sid)
    {
        Assert.Throws<IOException>(() => _sessions.Open(sid));
        _profiles.VerifyNoOtherCalls();
        _native.VerifyNoOtherCalls();
    }

    [Fact]
    public void RemovedAccountIsRejected_AndCannotOpenAnotherAccountsKeys()
    {
        Assert.Throws<IOException>(() => _sessions.Open("S-1-5-21-1-2-3-9999"));
        using var session = _sessions.Open(Sid);
        Assert.Throws<IOException>(() => session.Reader.GetSubKeyNames(@"HKEY_USERS\S-1-5-21-1-2-3-1002\Software", false));
        _native.VerifyNoOtherCalls();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
