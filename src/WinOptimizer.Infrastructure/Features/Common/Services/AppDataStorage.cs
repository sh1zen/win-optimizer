using System.Security.AccessControl;
using System.Security.Principal;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Explicitly prepares protected application storage before consumers start.</summary>
public sealed class AppDataStorage(IInteractiveUserService user)
{
    private const AccessControlSections ProtectionSections = AccessControlSections.Owner | AccessControlSections.Access;

    /// <summary>Prepares shared and account storage on a worker thread.</summary>
    public Task InitializeAsync() => Task.Run(InitializeDirectories);

    private void InitializeDirectories()
    {
        EnsureProtectedDirectory(AppDataPaths.Root);
        foreach (var path in new[] { AppDataPaths.Backups, AppDataPaths.Temp,
            AppDataPaths.State, AppDataPaths.Downloads, AppDataPaths.History })
            EnsureProtectedDirectory(path);
        foreach (var path in new[] { Path.Combine(AppDataPaths.Root, "OpenWebSearch"),
            Path.Combine(AppDataPaths.Root, "Unattend") })
            EnsureProtectedDirectory(path, readable: true);

        var sid = AppDataPaths.InteractiveUserSid(user);
        var userRoot = AppDataPaths.User(sid);
        EnsureProtectedDirectory(Path.GetDirectoryName(userRoot)!);
        EnsureProtectedDirectory(userRoot, sid);

        ProtectTree(AppDataPaths.Tools);
        ProtectTree(AppDataPaths.Apps, readable: true);
    }

    /// <summary>Protects executable contents before any elevated process can use them.</summary>
    private static void ProtectTree(string path, bool readable = false)
    {
        ValidatePath(path);
        var fileSecurity = CreateProtectedFileSecurity(readable);
        var expected = GetProtectionDescriptor(fileSecurity);
        ProtectDirectory(new DirectoryInfo(path));

        void ProtectDirectory(DirectoryInfo directory)
        {
            ApplyDirectoryProtection(directory, userSid: null, readable);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.FullName))
            {
                ValidatePath(entry);
                if (Directory.Exists(entry)) ProtectDirectory(new DirectoryInfo(entry));
                else ApplyFileProtection(new FileInfo(entry));
            }
        }

        void ApplyFileProtection(FileInfo file)
        {
            FileSecurity existing;
            try { existing = file.GetAccessControl(ProtectionSections); }
            catch (UnauthorizedAccessException)
            {
                // An ACL may grant permission to repair it without granting permission to read it.
                file.SetAccessControl(CreateProtectedFileSecurity(readable));
                return;
            }
            if (GetProtectionDescriptor(existing) != expected) file.SetAccessControl(CreateProtectedFileSecurity(readable));
        }
    }

    private static FileSecurity CreateProtectedFileSecurity(bool readable)
    {
        var acl = new FileSecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        if (readable)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return acl;
    }

    /// <summary>Compares the owner and complete DACL, ignoring Windows' inheritance bookkeeping on protected ACLs.</summary>
    internal static string GetProtectionDescriptor(FileSystemSecurity security)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected))
            descriptor.SetFlags(descriptor.ControlFlags & ~(ControlFlags.DiscretionaryAclAutoInherited |
                ControlFlags.DiscretionaryAclAutoInheritRequired));
        return descriptor.GetSddlForm(ProtectionSections);
    }

    /// <summary>Rejects filesystem redirections on every existing ancestor of a storage target.</summary>
    internal static void ValidatePath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Application storage cannot follow a link or junction: {current}");
        }
    }

    /// <summary>Creates private directories without granting users write access to shared executable or recovery data.</summary>
    private static void EnsureProtectedDirectory(string path, string? userSid = null, bool readable = false)
    {
        ValidatePath(path);
        ApplyDirectoryProtection(new DirectoryInfo(path), userSid, readable);
    }

    private static void ApplyDirectoryProtection(DirectoryInfo directory, string? userSid, bool readable)
    {
        var path = directory.FullName;
        var existing = directory.Exists
            ? directory.GetAccessControl(ProtectionSections)
            : null;
        var acl = CreateProtectedDirectorySecurity(path, existing, userSid, readable);
        if (existing is null) acl.CreateDirectory(path);
        if (existing is null || GetProtectionDescriptor(existing) != GetProtectionDescriptor(acl))
            directory.SetAccessControl(acl);
    }

    /// <summary>Validates existing ownership and builds the permissions applied before storage is used.</summary>
    internal static DirectorySecurity CreateProtectedDirectorySecurity(string path, DirectorySecurity? existing = null,
        string? userSid = null, bool readable = false)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (existing is not null)
        {
            var owner = existing.GetOwner(typeof(SecurityIdentifier));
            if (!admins.Equals(owner) && !system.Equals(owner))
            {
                // Windows can assign newly created folders to the launching account. Normalize
                // that account's folders before use, but never adopt another account's storage.
                using var account = WindowsIdentity.GetCurrent();
                if (account.User is null || !account.User.Equals(owner))
                    throw new UnauthorizedAccessException($"Application storage must be owned by Administrators or SYSTEM: {path}");
            }
        }
        var acl = new DirectorySecurity();
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { admins, system })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        // Users can traverse the common root to reach their private directory, but cannot replace shared data.
        if (path == AppDataPaths.Root || path == Path.Combine(AppDataPaths.Root, "Users"))
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        if (readable)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        if (userSid != null)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(userSid), FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

}
