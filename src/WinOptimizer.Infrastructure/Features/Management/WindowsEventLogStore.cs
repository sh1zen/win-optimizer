using WinOptimizer.Core.Features.Common.Constants;
using System.Diagnostics.Eventing.Reader;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Uses local Windows event APIs and protected, uniquely named archives; construction performs no IO.</summary>
internal sealed class WindowsEventLogStore : IEventLogStore
{
    private static string BackupRoot => AppDataPaths.EventLogBackups;

    public IReadOnlyList<string> GetLogNames()
    {
        try { using var session = new EventLogSession(); return session.GetLogNames().ToArray(); }
        catch (Exception ex) when (IsExpectedFailure(ex))
        { throw new UtilityOperationException("Windows event log discovery failed: " + ex.Message, ex); }
    }

    public EventLogChannel Read(string name)
    {
        try
        {
            using var session = new EventLogSession();
            var info = session.GetLogInformation(name, PathType.LogName);
            return new(name, info.RecordCount, info.FileSize, "");
        }
        catch (Exception ex) when (IsExpectedFailure(ex)) { return new(name, null, null, ex.Message); }
    }

    public void EnsureCanClear()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Restart win-optimizer as administrator to clear event logs.");
    }

    /// <summary>Archives may contain audit data; restrict them to Administrators and SYSTEM and reject links.</summary>
    public string CreateBackupDirectory()
    {
        var root = Path.GetFullPath(BackupRoot);
        ValidateNoLinks(root);
        var parent = Path.GetDirectoryName(root)!;
        Directory.CreateDirectory(parent);
        ValidateStorageAcl(new DirectoryInfo(parent).GetAccessControl(), container: true);
        var security = new DirectorySecurity();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { administrators, system })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.CreateDirectory(root);
        ValidateStorageAcl(new DirectoryInfo(root).GetAccessControl(), container: false);
        var directory = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        security.CreateDirectory(directory);
        ValidateNoLinks(directory);
        return directory;
    }

    /// <summary>The native clear call saves the cleared events directly, avoiding an export/clear race.</summary>
    public EventLogCleanupOutcome ClearWithBackup(string name, string backupDirectory)
    {
        var backup = "";
        var cleared = false;
        try
        {
            EnsureCanClear();
            var directory = Path.GetFullPath(backupDirectory);
            if (!string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(BackupRoot), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The event archive directory is outside its storage root.");
            ValidateNoLinks(directory);
            var label = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
            backup = Path.Combine(directory, $"{label}-{hash}.evtx");
            using var session = new EventLogSession();
            session.ClearLog(name, backup);
            cleared = true;
            if (!File.Exists(backup) || new FileInfo(backup).Length == 0)
                throw new IOException("The log was cleared, but its EVTX archive could not be verified.");
            _ = session.GetLogInformation(backup, PathType.FilePath);
            return new(name, true, backup, "", Read(name));
        }
        catch (Exception ex) when (IsExpectedFailure(ex)) { return new(name, cleared, backup, ex.Message, cleared ? Read(name) : null); }
    }

    /// <summary>Rejects replaceable parents and archive directories accessible to untrusted accounts.</summary>
    internal static void ValidateStorageAcl(DirectorySecurity actual, bool container)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var owner = actual.GetOwner(typeof(SecurityIdentifier));
        if (!administrators.Equals(owner) && !system.Equals(owner))
            throw new UnauthorizedAccessException("Event archive storage has an untrusted owner.");
        var forbidden = container ? FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership : FileSystemRights.FullControl;
        foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & forbidden) != 0 && !administrators.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference))
                throw new UnauthorizedAccessException("Event archive storage grants access to an untrusted account.");
    }

    private static bool IsExpectedFailure(Exception ex) => ex is EventLogException or IOException or UnauthorizedAccessException or
        SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException;

    /// <summary>Validates every existing ancestor before privileged directory or archive creation.</summary>
    private static void ValidateNoLinks(string path)
    {
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Event archives cannot use links or junctions: " + part);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
