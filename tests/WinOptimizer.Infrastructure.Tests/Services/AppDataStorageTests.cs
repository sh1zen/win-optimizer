using System.Security.AccessControl;
using System.Security.Principal;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Checks protected storage permissions with synthetic files in a private workspace.</summary>
public sealed class AppDataStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinOptimizerStorageTests", Guid.NewGuid().ToString("N"));

    public AppDataStorageTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LaunchingAccountOwnedDirectoryIsNormalizedWithoutChangingItsContents()
    {
        var path = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(path);
        var payload = Path.Combine(path, "saved.txt");
        File.WriteAllText(payload, "saved data");
        using var account = WindowsIdentity.GetCurrent();
        var existing = new DirectoryInfo(path).GetAccessControl();
        existing.SetOwner(account.User!);
        existing.AddAccessRule(new FileSystemAccessRule(account.User!, FileSystemRights.FullControl, AccessControlType.Allow));

        var acl = AppDataStorage.CreateProtectedDirectorySecurity(path, existing);

        AssertSharedPermissions(acl);
        Assert.Equal(account.User, existing.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal("saved data", File.ReadAllText(payload));
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    public void TrustedDirectoryOwnershipCanBePreparedRepeatedly(WellKnownSidType owner)
    {
        var existing = new DirectorySecurity();
        existing.SetOwner(new SecurityIdentifier(owner, null));
        var acl = AppDataStorage.CreateProtectedDirectorySecurity(_root, existing);
        AssertSharedPermissions(acl);
        AssertSharedPermissions(AppDataStorage.CreateProtectedDirectorySecurity(_root, acl));
    }

    [Fact]
    public void NewDirectoryReceivesProtectedSharedPermissions() =>
        AssertSharedPermissions(AppDataStorage.CreateProtectedDirectorySecurity(_root));

    [Fact]
    public void UnrelatedOwnerIsRejectedWithoutChangingExistingPermissions()
    {
        var existing = new DirectorySecurity();
        existing.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        var original = existing.GetSecurityDescriptorBinaryForm();

        var error = Assert.Throws<UnauthorizedAccessException>(() =>
            AppDataStorage.CreateProtectedDirectorySecurity(_root, existing));

        Assert.Contains(_root, error.Message);
        Assert.Equal(original, existing.GetSecurityDescriptorBinaryForm());
    }

    /// <summary>Checks the same protected ACL used for new and normalized shared directories.</summary>
    private static void AssertSharedPermissions(DirectorySecurity acl)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        Assert.Equal(admins, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(acl.AreAccessRulesProtected);
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.Equal(2, rules.Length);
        foreach (var sid in new[] { admins, system })
        {
            var rule = Assert.Single(rules, rule => sid.Equals(rule.IdentityReference));
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        }
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var parent = Path.Combine(Path.GetTempPath(), "WinOptimizerStorageTests") + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test workspace.");
        Directory.Delete(full, recursive: true);
    }
}
