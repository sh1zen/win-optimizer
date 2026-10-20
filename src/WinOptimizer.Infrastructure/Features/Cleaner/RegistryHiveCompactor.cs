using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Native Windows boundary, replaceable in isolated compaction tests.</summary>
public interface IRegistryHiveOperations
{
    bool IsAvailable { get; }
    void Save(string keyPath, string destination);
    void Compact(string keyPath);
}

/// <summary>Measures recoverable hive space and requires a completed backup before native compaction.</summary>
public sealed class RegistryHiveCompactor(ICleanerRegistrySessions accounts, ICleanerBackupVault vault, IRegistryHiveOperations native)
{
    private static Dictionary<string, string> MachineHives() => new(StringComparer.OrdinalIgnoreCase)
    {
        [@"HKEY_LOCAL_MACHINE\SOFTWARE"] = Path.Combine(Environment.SystemDirectory, "config", "SOFTWARE"),
        [@"HKEY_LOCAL_MACHINE\SYSTEM"] = Path.Combine(Environment.SystemDirectory, "config", "SYSTEM")
    };

    public CleanerScanResult Scan(CleanerOptions options, CancellationToken ct, IProgress<CleanerProgress>? progress = null)
    {
        if (!native.IsAvailable) return new([], ["Native registry compaction is unavailable on this Windows version."]);
        List<CleanerFinding> findings = [];
        List<string> notices = [];
        options.Validate();
        int total = (options.RegistryAccountSids?.Distinct(StringComparer.Ordinal).Count() ?? 1) + (options.DeepClean ? 2 : 0);
        int completed = 0;
        foreach (var (_, account) in CleanerAccounts.OpenSelected(accounts, options.RegistryAccountSids, notices, ct))
        {
            try { Analyze(account.UserPrefix, account.GetUserHive()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            { notices.Add($"{account.UserPrefix}: {ex.Message}"); }
        }
        if (options.DeepClean)
            foreach (var hive in MachineHives()) Analyze(hive.Key, new(hive.Key, hive.Value));
        progress?.Report(new("Cleaner_RegistryDefrag", total, total));
        return new(findings.AsReadOnly(), notices.AsReadOnly()) { CheckedItems = completed };

        void Analyze(string identity, CleanerRegistryHive hive)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new("Cleaner_RegistryDefrag", completed, total));
            var analysis = Path.Combine(vault.RootDirectory, $"analysis-{Guid.NewGuid():N}.hiv");
            try
            {
                CleanerPaths.ValidateDescendant(vault.RootDirectory, analysis);
                vault.EnsureDirectory(vault.RootDirectory);
                long original = new FileInfo(hive.FilePath).Length;
                native.Save(hive.KeyPath, analysis);
                long compacted = new FileInfo(analysis).Length;
                if (original > compacted + 4096)
                    findings.Add(new(Guid.NewGuid(), CleanerArea.RegistryDefrag, "Cleaner_RegistryDefrag", identity,
                        original - compacted, true, new CleanerHiveTarget(identity) { SourcePath = hive.FilePath }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            { notices.Add($"{identity}: {ex.Message}"); }
            finally
            {
                // Only this generated analysis file is temporary; durable backups are retained.
                if (File.Exists(analysis)) File.Delete(analysis);
                completed++;
                progress?.Report(new("Cleaner_RegistryDefrag", completed, total));
            }
        }
    }

    public void Clean(CleanerHiveTarget target, CancellationToken ct, bool createBackup = true)
    {
        if (!native.IsAvailable) throw new IOException("Native registry compaction is unavailable on this Windows version.");
        ct.ThrowIfCancellationRequested();
        ICleanerRegistrySession? account = null;
        try
        {
            CleanerRegistryHive hive;
            if (MachineHives().TryGetValue(target.KeyPath, out var file)) hive = new(target.KeyPath, file);
            else
            {
                var sid = target.KeyPath == "HKEY_CURRENT_USER" ? null : target.KeyPath.StartsWith(@"HKEY_USERS\", StringComparison.Ordinal)
                    ? target.KeyPath[@"HKEY_USERS\".Length..] : throw new IOException("Unsupported registry hive.");
                account = accounts.Open(sid);
                if (account.UserPrefix != target.KeyPath) throw new IOException("The selected account changed. Scan again.");
                hive = account.GetUserHive();
            }
            if (target.SourcePath is not null && !string.Equals(target.SourcePath, hive.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The registered account hive changed. Scan again.");
            if (createBackup) CreateBackup(hive);
            ct.ThrowIfCancellationRequested();
            native.Compact(hive.KeyPath);
        }
        finally { account?.Dispose(); }
    }

    private void CreateBackup(CleanerRegistryHive hive)
    {
        var backup = Path.Combine(vault.RootDirectory, $"hive-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.hiv");
        CleanerPaths.ValidateDescendant(vault.RootDirectory, backup);
        vault.EnsureDirectory(vault.RootDirectory);
        var partial = backup + ".partial";
        try
        {
            native.Save(hive.KeyPath, partial);
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0) throw new IOException("Registry hive backup was not created.");
            File.Move(partial, backup);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
        var original = hive.FilePath;
        using var backupStream = File.OpenRead(backup);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(backupStream));
        File.WriteAllText(backup + ".txt", $"Registry hive: {hive.KeyPath}\r\nBackup: {backup}\r\nOriginal hive location: {original}\r\n" +
            "Restore this hive from Windows Recovery Environment after identifying the correct offline Windows volume. " +
            "Unlock BitLocker if enabled. Back up the current hive to a separate file first. " +
            "Copy the verified backup to the original hive location with Windows offline, adjusting drive letters to the recovery environment. " +
            "Move the current .LOG1/.LOG2 transaction logs beside the saved current hive before installing the backup, then restart. " +
            "A whole-hive restore also reverts registry changes made after the backup.\r\n" +
            $"SHA256: {hash}\r\n");
    }
}

/// <summary>Uses the native hive compactor with scoped backup/restore privileges and explicit NTSTATUS checks.</summary>
public sealed class WindowsRegistryHiveOperations : IRegistryHiveOperations
{
    public bool IsAvailable
    {
        get
        {
            if (!NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "ntdll.dll"), out var module)) return false;
            try { return NativeLibrary.TryGetExport(module, "NtCompressKey", out _); }
            finally { NativeLibrary.Free(module); }
        }
    }

    public void Save(string keyPath, string destination)
    {
        using var backup = new RegistryPrivilegeScope("SeBackupPrivilege");
        using var key = OpenHive(keyPath, writable: false);
        int status = RegSaveKeyEx(key.Handle, destination, IntPtr.Zero, 2);
        if (status != 0) throw new Win32Exception(status, "Unable to save registry hive: " + new Win32Exception(status).Message);
    }

    public void Compact(string keyPath)
    {
        using var backup = new RegistryPrivilegeScope("SeBackupPrivilege");
        using var restore = new RegistryPrivilegeScope("SeRestorePrivilege");
        using var key = OpenHive(keyPath, writable: true);
        int status = NtCompressKey(key.Handle);
        if (status < 0) throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)), $"Registry compaction failed (NTSTATUS 0x{status:X8}).");
    }

    private static RegistryKey OpenHive(string keyPath, bool writable)
    {
        if (keyPath == "HKEY_CURRENT_USER") return Registry.CurrentUser.OpenSubKey("", writable) ?? throw new IOException("User hive is unavailable.");
        const string machinePrefix = @"HKEY_LOCAL_MACHINE\";
        if (keyPath is @"HKEY_LOCAL_MACHINE\SOFTWARE" or @"HKEY_LOCAL_MACHINE\SYSTEM")
            return Registry.LocalMachine.OpenSubKey(keyPath[machinePrefix.Length..], writable) ?? throw new IOException("System hive is unavailable.");
        const string usersPrefix = @"HKEY_USERS\";
        if (keyPath.StartsWith(usersPrefix, StringComparison.Ordinal))
        {
            var name = keyPath[usersPrefix.Length..];
            const string mounted = "WinOptimizer_Cleaner_";
            if (CleanerOptions.IsAccountSid(name) || name.StartsWith(mounted, StringComparison.Ordinal) && Guid.TryParseExact(name[mounted.Length..], "N", out _))
                return Registry.Users.OpenSubKey(name, writable) ?? throw new IOException("The selected account hive is unavailable.");
        }
        throw new IOException("Unsupported registry hive.");
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSaveKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegSaveKeyEx(SafeRegistryHandle key, string file, IntPtr security, uint flags);
    // Native declaration: https://github.com/winsiderss/phnt/blob/master/ntregapi.h
    [DllImport("ntdll.dll")] private static extern int NtCompressKey(SafeRegistryHandle key);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);

}

