using WinOptimizer.Core.Features.Common.Constants;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Boundary protecting backup directories and authenticating private recovery metadata.</summary>
public interface ICleanerBackupVault
{
    string RootDirectory { get; }
    void EnsureDirectory(string path);
    void ValidateDirectory(string path);
    byte[] Seal(byte[] data);
    byte[] Unseal(byte[] data);
}

/// <summary>Creates administrator-owned backup storage and binds recovery metadata to the Windows account.</summary>
public sealed class WindowsCleanerBackupVault : ICleanerBackupVault
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly byte[] Entropy = "win-optimizer.Cleaner.Backup.v1"u8.ToArray();
    public string RootDirectory
    {
        get
        {
            using var account = WindowsIdentity.GetCurrent();
            var sid = account.User?.Value ?? throw new UnauthorizedAccessException("The Windows backup account is unavailable.");
            return Path.Combine(AppDataPaths.CleanerBackups, sid);
        }
    }

    public void EnsureDirectory(string path)
    {
        var root = Path.GetFullPath(RootDirectory);
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)) CleanerPaths.ValidateDescendant(root, full);
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, System })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var storage = Path.GetDirectoryName(root)!;
        var application = AppDataPaths.Root;
        foreach (var directory in new[] { application, AppDataPaths.Backups, storage, root, full }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CleanerPaths.ValidateDescendant(Path.GetPathRoot(directory)!, Path.Combine(directory, ".boundary"));
            security.CreateDirectory(directory);
            ValidatePermissions(directory, container: directory.Equals(application, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void ValidateDirectory(string path)
    {
        var root = Path.GetFullPath(RootDirectory);
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)) CleanerPaths.ValidateDescendant(root, full);
        var storage = Path.GetDirectoryName(root)!;
        ValidatePermissions(AppDataPaths.Root, container: true);
        ValidatePermissions(AppDataPaths.Backups, container: false);
        foreach (var directory in new[] { storage, root, full }.Distinct(StringComparer.OrdinalIgnoreCase))
            ValidatePermissions(directory, container: false);
    }

    private static void ValidatePermissions(string path, bool container)
    {
        CleanerPaths.ValidateDescendant(Path.GetPathRoot(path)!, Path.Combine(path, ".boundary"));
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        ValidateAcl(acl, container);
    }

    /// <summary>Checks storage ACLs without changing permissions on the surrounding application folder.</summary>
    internal static void ValidateAcl(DirectorySecurity acl, bool container)
    {
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (!Administrators.Equals(owner) && !System.Equals(owner)) throw new UnauthorizedAccessException("Cleaner backup storage must be owned by Administrators or SYSTEM.");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            // The surrounding app folder may allow creation of sibling files. It must
            // not allow replacement of protected backup directories or their permissions.
            var writes = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            if (!container) writes |= FileSystemRights.Write;
            if ((rule.FileSystemRights & writes) != 0 && !Administrators.Equals(rule.IdentityReference) && !System.Equals(rule.IdentityReference))
                throw new UnauthorizedAccessException("Cleaner backup storage grants write access to an untrusted account.");
        }
    }
    public byte[] Seal(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unseal(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
