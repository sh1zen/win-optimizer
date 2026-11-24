using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Native Windows boundary, replaceable in isolated compaction tests.</summary>
public interface IRegistryHiveOperations
{
    void Save(string keyPath, string destination);
}

/// <summary>Measures recoverable hive space and resolves reviewed targets for offline compaction.</summary>
public sealed class RegistryHiveCompactor(ICleanerRegistrySessions accounts, ICleanerBackupVault vault,
    IRegistryHiveOperations native, IRegistryDefragBoot boot)
{
    private static Dictionary<string, string> MachineHives() => new(StringComparer.OrdinalIgnoreCase)
    {
        [@"HKEY_LOCAL_MACHINE\SOFTWARE"] = Path.Combine(Environment.SystemDirectory, "config", "SOFTWARE"),
        [@"HKEY_LOCAL_MACHINE\SYSTEM"] = Path.Combine(Environment.SystemDirectory, "config", "SYSTEM")
    };

    public CleanerScanResult Scan(CleanerOptions options, CancellationToken ct, IProgress<CleanerProgress>? progress = null)
    {
        List<CleanerFinding> findings = [];
        List<string> notices = [];
        if (boot.GetStatusNotice() is { } status) notices.Add(status);
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

    /// <summary>Re-resolves every reviewed hive before handing one job to the boot scheduler.</summary>
    public Task ScheduleAsync(IReadOnlyList<CleanerHiveTarget> targets, CancellationToken ct)
    {
        var machineHives = MachineHives();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            var path = machineHives.TryGetValue(target.KeyPath, out var machineFile)
                ? machineFile : ResolveProfileHive(target.KeyPath);
            if (!string.Equals(target.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The registered account hive changed. Scan again.");
            paths.Add(path);
        }
        return boot.ScheduleAsync(paths.ToArray(), ct);
    }

    private string ResolveProfileHive(string keyPath)
    {
        const string usersPrefix = @"HKEY_USERS\";
        var sid = keyPath == "HKEY_CURRENT_USER" ? null :
            keyPath.StartsWith(usersPrefix, StringComparison.Ordinal) && CleanerOptions.IsAccountSid(keyPath[usersPrefix.Length..])
            ? keyPath[usersPrefix.Length..] : throw new IOException("Unsupported registry hive.");
        using var account = accounts.Open(sid);
        if (account.UserPrefix != keyPath) throw new IOException("The selected account changed. Scan again.");
        return account.GetUserHive().FilePath;
    }
}

/// <summary>Exports a hive with scoped backup privileges for the read-only space estimate.</summary>
public sealed class WindowsRegistryHiveOperations : IRegistryHiveOperations
{
    public void Save(string keyPath, string destination)
    {
        using var backup = new RegistryPrivilegeScope("SeBackupPrivilege");
        using var key = OpenHive(keyPath);
        int status = RegSaveKeyEx(key.Handle, destination, IntPtr.Zero, 2);
        if (status != 0) throw new Win32Exception(status, "Unable to save registry hive: " + new Win32Exception(status).Message);
    }

    private static RegistryKey OpenHive(string keyPath)
    {
        if (keyPath == "HKEY_CURRENT_USER") return Registry.CurrentUser.OpenSubKey("") ?? throw new IOException("User hive is unavailable.");
        const string machinePrefix = @"HKEY_LOCAL_MACHINE\";
        if (keyPath is @"HKEY_LOCAL_MACHINE\SOFTWARE" or @"HKEY_LOCAL_MACHINE\SYSTEM")
            return Registry.LocalMachine.OpenSubKey(keyPath[machinePrefix.Length..]) ?? throw new IOException("System hive is unavailable.");
        const string usersPrefix = @"HKEY_USERS\";
        if (keyPath.StartsWith(usersPrefix, StringComparison.Ordinal))
        {
            var name = keyPath[usersPrefix.Length..];
            const string mounted = "WinOptimizer_Cleaner_";
            if (CleanerOptions.IsAccountSid(name) || name.StartsWith(mounted, StringComparison.Ordinal) && Guid.TryParseExact(name[mounted.Length..], "N", out _))
                return Registry.Users.OpenSubKey(name) ?? throw new IOException("The selected account hive is unavailable.");
        }
        throw new IOException("Unsupported registry hive.");
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSaveKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegSaveKeyEx(SafeRegistryHandle key, string file, IntPtr security, uint flags);
}

