using System.Security.AccessControl;
using System.Security.Principal;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public class CleanerBackupVaultTests
{
    [Fact]
    public void StorageIsScopedToTheAccountThatProtectsItsMetadata()
    {
        using var account = WindowsIdentity.GetCurrent();
        var root = new WindowsCleanerBackupVault().RootDirectory;
        Assert.Equal(account.User!.Value, Path.GetFileName(root));
        Assert.Equal("Cleaner", Path.GetFileName(Path.GetDirectoryName(root)));
        Assert.Equal("Backups", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(root))));
    }

    private static DirectorySecurity Acl(FileSystemRights usersRights)
    {
        var acl = new DirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), usersRights, AccessControlType.Allow));
        return acl;
    }
    [Fact]
    public void ParentCanAllowSiblingCreation_WhileBackupStorageCannotAllowUntrustedWrites()
    {
        var acl = Acl(FileSystemRights.Write);
        WindowsCleanerBackupVault.ValidateAcl(acl, container: true);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsCleanerBackupVault.ValidateAcl(acl, container: false));
    }
    [Theory]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void ParentMustNotAllowReplacementOfProtectedBackups(FileSystemRights rights) =>
        Assert.Throws<UnauthorizedAccessException>(() => WindowsCleanerBackupVault.ValidateAcl(Acl(rights), container: true));

    [Fact]
    public void ReadOnlyUserAccessDoesNotPermitTampering()
    {
        WindowsCleanerBackupVault.ValidateAcl(Acl(FileSystemRights.ReadAndExecute), container: false);
    }
    [Fact]
    public void UntrustedOwnerIsRejected()
    {
        var acl = Acl(FileSystemRights.Read);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsCleanerBackupVault.ValidateAcl(acl, container: false));
    }
}
