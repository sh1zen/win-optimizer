using System.Security.AccessControl;
using System.Security.Principal;
using WinOptimizer.Infrastructure.Features.Common.Services;

namespace WinOptimizer.Infrastructure.Features.Management;

/// <summary>Validates the app files that Windows will run with elevated rights at sign-in.</summary>
internal static class ApplicationStartupExecutable
{
    private const AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Access;
    private const int WriteAccess = (int)(FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
        | 0x50000000; // GENERIC_WRITE and GENERIC_ALL
    private static readonly HashSet<SecurityIdentifier> TrustedOwners =
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464") // TrustedInstaller
    ];

    public static string FindProtectedInstallation()
    {
        var executable = Environment.ProcessPath;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installation = executable is null ? null : Path.GetDirectoryName(executable);
        if (executable is null || installation is null || programFiles.Length == 0 || executable.Contains('%') ||
            !installation.StartsWith(Path.TrimEndingDirectorySeparator(programFiles) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(executable), "win-optimizer.exe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Install win-optimizer in a protected folder under Program Files to enable administrator startup.");

        AppDataStorage.ValidatePath(executable);
        var root = new DirectoryInfo(installation);
        for (var parent = root.Parent; parent is not null; parent = parent.Parent)
        {
            ValidatePermissions(parent.GetAccessControl(Sections), parent.FullName);
            if (string.Equals(parent.FullName, programFiles, StringComparison.OrdinalIgnoreCase)) break;
        }
        ValidateContents(root);
        return executable;
    }

    private static void ValidateContents(DirectoryInfo directory)
    {
        ValidatePermissions(directory.GetAccessControl(Sections), directory.FullName);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Administrator startup requires application files without links or junctions: {entry.FullName}");
            if (entry is DirectoryInfo child) ValidateContents(child);
            else ValidatePermissions(((FileInfo)entry).GetAccessControl(Sections), entry.FullName);
        }
    }

    /// <summary>Rejects payloads that a non-elevated process could replace or grant itself access to.</summary>
    internal static void ValidatePermissions(FileSystemSecurity security, string path)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is not { } owner || !TrustedOwners.Contains(owner) ||
            descriptor.DiscretionaryAcl is not { } acl ||
            acl.OfType<QualifiedAce>().Any(rule => rule.AceQualifier == AceQualifier.AccessAllowed &&
                (rule.AceFlags & AceFlags.InheritOnly) == 0 && (rule.AccessMask & WriteAccess) != 0 &&
                !TrustedOwners.Contains(rule.SecurityIdentifier)))
            throw new UnauthorizedAccessException($"Administrator startup requires files and folders writable only by administrators or Windows system services: {path}");
    }
}
