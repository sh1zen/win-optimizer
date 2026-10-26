using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>A registry session binds reads, corrections and reference paths to one Windows profile.</summary>
public interface ICleanerRegistrySession : IDisposable
{
    string UserPrefix { get; }
    bool IsInteractive { get; }
    string GetFolderPath(Environment.SpecialFolder folder);
    CleanerRegistryHive GetUserHive();
    ICleanerRegistryReader Reader { get; }
    ICleanerRegistryEditor Editor { get; }
}

/// <summary>Discovers registered accounts and owns explicit per-operation profile sessions.</summary>
public interface ICleanerRegistrySessions
{
    IReadOnlyList<CleanerRegistryAccount> GetAccounts();
    ICleanerRegistrySession Open(string? sid);
}

public sealed record CleanerRegistryProfile(string Sid, string DisplayName, string Directory);
/// <summary>The currently opened native hive and its registered backing file; its lifetime belongs to its session.</summary>
public sealed record CleanerRegistryHive(string KeyPath, string FilePath);

/// <summary>Boundary for Windows' authoritative profile registration catalogue.</summary>
public interface ICleanerRegistryProfiles
{
    IReadOnlyList<CleanerRegistryProfile> Read();
    CleanerRegistryProfile? Find(string sid);
}

/// <summary>Reads account profiles without loading hives or querying Windows during construction.</summary>
public sealed class WindowsCleanerRegistryProfiles : ICleanerRegistryProfiles
{
    public IReadOnlyList<CleanerRegistryProfile> Read() => ReadCatalogue(includeSystemAccounts: false);

    internal static bool IsSystemAccountSid(string sid) => sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20";

    internal static IReadOnlyList<CleanerRegistryProfile> ReadCatalogue(bool includeSystemAccounts)
    {
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
            ?? throw new IOException("Windows profile registrations are unavailable.");
        List<CleanerRegistryProfile> result = [];
        foreach (var sid in profiles.GetSubKeyNames().Where(sid => CleanerOptions.IsAccountSid(sid) || includeSystemAccounts && IsSystemAccountSid(sid)))
        {
            using var profile = profiles.OpenSubKey(sid) ?? throw new IOException($"The Windows profile disappeared: {sid}");
            result.Add(ReadProfile(profile, sid, includeSystemAccounts));
        }
        return result.OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public CleanerRegistryProfile? Find(string sid) => FindInCatalogue(sid, includeSystemAccounts: false);

    internal static CleanerRegistryProfile? FindInCatalogue(string sid, bool includeSystemAccounts)
    {
        if (!CleanerOptions.IsAccountSid(sid) && !(includeSystemAccounts && IsSystemAccountSid(sid))) throw new IOException("Invalid registry account SID.");
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList")
            ?? throw new IOException("Windows profile registrations are unavailable.");
        using var key = profiles.OpenSubKey(sid);
        return key is null ? null : ReadProfile(key, sid, includeSystemAccounts);
    }

    private static CleanerRegistryProfile ReadProfile(RegistryKey profile, string sid, bool expandSystemFolders)
    {
        var directory = profile.GetValue("ProfileImagePath") as string;
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException($"The registered profile path for {sid} is missing.");
        if (expandSystemFolders)
            directory = directory.Replace("%SYSTEMDRIVE%", Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                .Replace("%SYSTEMROOT%", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                .Replace("%WINDIR%", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
        directory = ValidateProfileDirectory(directory);
        string name;
        try { name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (IdentityNotMappedException) { name = Path.GetFileName(directory.TrimEnd('\\')); }
        return new(sid, name, directory);
    }

    /// <summary>Accepts only resolved local profile paths before normalizing their filesystem identity.</summary>
    internal static string ValidateProfileDirectory(string directory)
    {
        if (!CleanerPaths.IsLocalPath(directory) || directory.Contains('%')) throw new IOException("The registered profile path is not a local resolved path.");
        try { return Path.GetFullPath(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { throw new IOException("The registered profile path is invalid.", ex); }
    }
}

/// <summary>Loads and releases only hives mounted by the current registry operation.</summary>
public interface ICleanerRegistryHiveAccess
{
    bool IsLoaded(string name);
    RegistryKey? OpenKey(string path, bool view32, bool writable);
    string Mount(string file);
    void Unmount(string name);
}

/// <summary>Isolates privileged hive mount/unmount calls for deterministic native argument and failure tests.</summary>
internal interface IRegistryHiveMountApi
{
    int Load(IntPtr root, string name, string file);
    int Unload(IntPtr root, string name);
}

public sealed class WindowsCleanerRegistryHiveAccess : ICleanerRegistryHiveAccess
{
    // These APIs accept the predefined root; child-key handles cannot be used here.
    private static readonly IntPtr UsersRoot = new((int)RegistryHive.Users);
    private readonly IRegistryHiveMountApi _native;

    public WindowsCleanerRegistryHiveAccess() : this(new WindowsRegistryHiveMountApi()) { }
    internal WindowsCleanerRegistryHiveAccess(IRegistryHiveMountApi native) => _native = native;

    public bool IsLoaded(string name)
    {
        using var key = Registry.Users.OpenSubKey(name);
        return key is not null;
    }

    public RegistryKey? OpenKey(string path, bool view32, bool writable)
    {
        int separator = path.IndexOf('\\');
        if (separator < 1) throw new ArgumentException("A full registry path is required.", nameof(path));
        var hiveId = path[..separator] switch
        {
            "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException("Unsupported registry hive.", nameof(path))
        };
        using var hive = RegistryKey.OpenBaseKey(hiveId, view32 ? RegistryView.Registry32 : RegistryView.Registry64);
        return hive.OpenSubKey(path[(separator + 1)..], writable);
    }

    public string Mount(string file)
    {
        // RegLoadKey creates a missing file, so validate existence before calling Windows.
        CleanerPaths.ValidateDescendant(Path.GetPathRoot(file)!, file);
        if (!File.Exists(file)) throw new IOException($"The account registry hive is unavailable: {file}");
        var name = "WinOptimizer_Cleaner_" + Guid.NewGuid().ToString("N");
        int status = _native.Load(UsersRoot, name, file);
        if (status != 0) throw new IOException($"Cannot load the account registry hive: {file} (Windows error {status}: {new Win32Exception(status).Message})", new Win32Exception(status));
        return name;
    }

    public void Unmount(string name)
    {
        int status = _native.Unload(UsersRoot, name);
        if (status != 0) throw new IOException($"Cannot release the account registry hive: {name} (Windows error {status}: {new Win32Exception(status).Message})", new Win32Exception(status));
    }

    /// <summary>Scopes backup/restore privileges around each synchronous native operation.</summary>
    private sealed class WindowsRegistryHiveMountApi : IRegistryHiveMountApi
    {
        public int Load(IntPtr root, string name, string file) => WithPrivileges(() => RegLoadKey(root, name, file));
        public int Unload(IntPtr root, string name) => WithPrivileges(() => RegUnLoadKey(root, name));

        private static int WithPrivileges(Func<int> operation)
        {
            using var backup = new RegistryPrivilegeScope("SeBackupPrivilege");
            using var restore = new RegistryPrivilegeScope("SeRestorePrivilege");
            return operation();
        }

        [DllImport("advapi32.dll", EntryPoint = "RegLoadKeyW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegLoadKey(IntPtr key, string name, string file);
        [DllImport("advapi32.dll", EntryPoint = "RegUnLoadKeyW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int RegUnLoadKey(IntPtr key, string name);
    }
}

/// <summary>Resolves live or offline profile hives while keeping persisted targets tied to stable account SIDs.</summary>
public sealed class WindowsCleanerRegistrySessions(IInteractiveUserService user, ICleanerRegistryProfiles profiles,
    ICleanerRegistryHiveAccess mounts) : ICleanerRegistrySessions
{
    public IReadOnlyList<CleanerRegistryAccount> GetAccounts()
    {
        var currentSid = user.InteractiveUserSid;
        if (!user.IsOtsElevation) { using var identity = WindowsIdentity.GetCurrent(); currentSid = identity.User?.Value; }
        return profiles.Read().Select(profile => new CleanerRegistryAccount(profile.Sid, profile.DisplayName, profile.Sid == currentSid)).ToArray();
    }

    public ICleanerRegistrySession Open(string? sid)
    {
        if (sid is null) return new Session(user, null, mounts);
        if (!CleanerOptions.IsAccountSid(sid)) throw new IOException("Invalid registry account SID.");
        var profile = profiles.Find(sid)
            ?? throw new IOException($"The selected Windows account is no longer registered: {sid}");
        return new Session(user, profile, mounts);
    }

    /// <summary>Opens an explicitly discovered uninstall account, including registered Windows service identities.</summary>
    internal ICleanerRegistrySession OpenRegisteredProfile(CleanerRegistryProfile profile)
    {
        if (!CleanerOptions.IsAccountSid(profile.Sid) && !WindowsCleanerRegistryProfiles.IsSystemAccountSid(profile.Sid))
            throw new IOException("Unsupported Windows account SID.");
        _ = WindowsCleanerRegistryProfiles.ValidateProfileDirectory(profile.Directory);
        return new Session(user, profile, mounts);
    }

    private sealed class Session : ICleanerRegistrySession
    {
        private readonly IInteractiveUserService _user;
        private readonly CleanerRegistryProfile? _profile;
        private readonly ICleanerRegistryHiveAccess _mounts;
        private readonly Dictionary<bool, string?> _roots = [];
        private readonly List<string> _ownedMounts = [];
        private readonly Dictionary<Environment.SpecialFolder, string> _folders = [];
        public string UserPrefix { get; }
        public bool IsInteractive => _profile is null && !_user.IsOtsElevation;
        public ICleanerRegistryReader Reader { get; }
        public ICleanerRegistryEditor Editor { get; }

        public Session(IInteractiveUserService user, CleanerRegistryProfile? profile, ICleanerRegistryHiveAccess mounts)
        {
            _user = user; _profile = profile; _mounts = mounts;
            UserPrefix = profile is not null ? @"HKEY_USERS\" + profile.Sid : user.IsOtsElevation
                ? @"HKEY_USERS\" + (user.InteractiveUserSid ?? throw new IOException("Interactive user SID is unavailable.")) : "HKEY_CURRENT_USER";
            Reader = new WindowsCleanerRegistryReader(OpenKey);
            Editor = new WindowsCleanerRegistryEditor(OpenKey);
        }

        /// <summary>Resolves each folder once per account session; reopening a session reads current folder bindings.</summary>
        public string GetFolderPath(Environment.SpecialFolder folder)
        {
            if (_folders.TryGetValue(folder, out var cached)) return cached;
            if (_profile is null)
            {
                var interactivePath = _user.GetInteractiveUserFolderPath(folder);
                _folders.Add(folder, interactivePath);
                return interactivePath;
            }
            if (folder == Environment.SpecialFolder.UserProfile) return _profile.Directory;
            var name = folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => "Local AppData",
                Environment.SpecialFolder.ApplicationData => "AppData",
                Environment.SpecialFolder.DesktopDirectory => "Desktop",
                Environment.SpecialFolder.Programs => "Programs",
                Environment.SpecialFolder.StartMenu => "Start Menu",
                _ => throw new ArgumentOutOfRangeException(nameof(folder))
            };
            using var key = OpenKey(UserPrefix + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", false, false);
            var configured = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (configured is not null and not string) throw new IOException($"The selected account's {name} path is invalid.");
            var defaultPath = folder switch
            {
                Environment.SpecialFolder.LocalApplicationData => Path.Combine(_profile.Directory, @"AppData\Local"),
                Environment.SpecialFolder.ApplicationData => Path.Combine(_profile.Directory, @"AppData\Roaming"),
                Environment.SpecialFolder.DesktopDirectory => Path.Combine(_profile.Directory, "Desktop"),
                Environment.SpecialFolder.Programs => Path.Combine(GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Environment.SpecialFolder.StartMenu => Path.Combine(GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu"),
                _ => throw new ArgumentOutOfRangeException(nameof(folder))
            };
            var resolved = (configured as string ?? defaultPath).Replace("%USERPROFILE%", _profile.Directory, StringComparison.OrdinalIgnoreCase);
            if (folder is Environment.SpecialFolder.DesktopDirectory or Environment.SpecialFolder.Programs or Environment.SpecialFolder.StartMenu)
            {
                if (resolved.Contains("%APPDATA%", StringComparison.OrdinalIgnoreCase))
                    resolved = resolved.Replace("%APPDATA%", GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase);
                if (resolved.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase))
                    resolved = resolved.Replace("%LOCALAPPDATA%", GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase);
            }
            if (resolved.Contains('%') || !Path.IsPathFullyQualified(resolved)) throw new IOException($"The selected account's {name} path cannot be resolved.");
            resolved = WindowsCleanerRegistryProfiles.ValidateProfileDirectory(resolved);
            _folders.Add(folder, resolved);
            return resolved;
        }

        /// <summary>Opens the registered user hive only on demand and retains ownership of any temporary mount.</summary>
        public CleanerRegistryHive GetUserHive() => new(_profile is null ? UserPrefix : @"HKEY_USERS\" + GetRoot(false),
            Path.Combine(GetFolderPath(Environment.SpecialFolder.UserProfile), "NTUSER.DAT"));

        private RegistryKey? OpenKey(string path, bool view32, bool writable)
        {
            if (path.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.Ordinal) ||
                _profile is null && path.StartsWith(UserPrefix + "\\", StringComparison.Ordinal))
                return _mounts.OpenKey(path, view32, writable);
            if (_profile is null || !path.StartsWith(UserPrefix + "\\", StringComparison.Ordinal))
                throw new IOException("The registry path is outside the selected account.");
            var relative = path[(UserPrefix.Length + 1)..];
            const string classesPath = @"Software\Classes";
            var classes = relative.Equals(classesPath, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(classesPath + "\\", StringComparison.OrdinalIgnoreCase);
            var root = GetRoot(classes);
            if (root is null) return null;
            if (classes) relative = relative[classesPath.Length..].TrimStart('\\');
            // Offline hives have no Windows user-view registration. Resolve their stored COM view explicitly.
            // https://learn.microsoft.com/windows/win32/winprog64/shared-registry-keys
            if (classes && view32 && Environment.Is64BitOperatingSystem && _ownedMounts.Contains(root) &&
                new[] { "CLSID", "Interface", "DirectShow", "Media Type", "MediaFoundation" }.Contains(relative.Split('\\')[0], StringComparer.OrdinalIgnoreCase))
            {
                relative = @"Wow6432Node\" + relative;
                view32 = false;
            }
            return _mounts.OpenKey(@"HKEY_USERS\" + root + (relative.Length == 0 ? "" : "\\" + relative), view32, writable);
        }

        private string? GetRoot(bool classes)
        {
            if (_roots.TryGetValue(classes, out var root)) return root;
            var liveName = _profile!.Sid + (classes ? "_Classes" : "");
            if (_mounts.IsLoaded(liveName)) root = liveName;
            else
            {
                var file = Path.Combine(_profile.Directory, classes ? @"AppData\Local\Microsoft\Windows\UsrClass.dat" : "NTUSER.DAT");
                CleanerPaths.ValidateDescendant(_profile.Directory, file);
                if (classes && !File.Exists(file)) root = null;
                else { root = _mounts.Mount(file); _ownedMounts.Add(root); }
            }
            _roots.Add(classes, root);
            return root;
        }

        public void Dispose()
        {
            List<Exception> errors = [];
            foreach (var name in _ownedMounts.AsEnumerable().Reverse())
                try { _mounts.Unmount(name); } catch (Exception ex) when (ex is IOException or Win32Exception) { errors.Add(ex); }
            _ownedMounts.Clear();
            if (errors.Count > 0) throw new IOException("An account registry hive could not be released.", new AggregateException(errors));
        }
    }
}
