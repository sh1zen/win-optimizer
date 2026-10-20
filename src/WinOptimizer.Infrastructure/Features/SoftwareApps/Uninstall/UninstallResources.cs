using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Cleaner;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Uninstall;

public sealed record UninstallPackage(string Name, string FamilyName, string InstallDirectory, string? Notice = null);

/// <summary>Read-only deployment boundary; each query names an explicit Windows account.</summary>
public interface IUninstallPackageSource
{
    IReadOnlyList<UninstallPackage> GetPackages(string sid, IReadOnlyCollection<string> names);
}

public sealed class WindowsUninstallPackageSource : IUninstallPackageSource
{
    public IReadOnlyList<UninstallPackage> GetPackages(string sid, IReadOnlyCollection<string> names) =>
        new Windows.Management.Deployment.PackageManager().FindPackagesForUser(sid)
            .Where(package => names.Contains(package.Id.Name, StringComparer.OrdinalIgnoreCase)).Select(package =>
            {
                try { return new UninstallPackage(package.Id.Name, package.Id.FamilyName, package.InstalledLocation?.Path ?? string.Empty); }
                catch (Exception ex) when (UninstallResources.IsResourceError(ex) || ex is System.Runtime.InteropServices.COMException)
                {
                    return new UninstallPackage(package.Id.Name, package.Id.FamilyName, string.Empty,
                        $"{package.Id.Name}: install-directory metadata unavailable ({ex.Message}).");
                }
            }).ToArray();
}

/// <summary>Supplies machine scan locations without querying Windows during construction.</summary>
public interface IUninstallScanFolders
{
    IReadOnlyList<string> ApplicationDirectories { get; }
    IReadOnlyList<string> ShortcutDirectories { get; }
    string WindowsDirectory { get; }
}

public sealed class WindowsUninstallScanFolders : IUninstallScanFolders
{
    public IReadOnlyList<string> ApplicationDirectories => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> ShortcutDirectories => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
    public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
}

internal sealed record UninstallAccount(string Sid, string Name, string ProfileDirectory);
internal sealed record UninstallRegistration(string? Sid, string Path, bool View32, string Name,
    string InstallDirectory, string Executable);
internal sealed record UninstallFileRoot(string Path, string? Sid, Environment.SpecialFolder? Folder,
    string BaseDirectory, bool SingleFile = false, string? ShortcutTarget = null, string? KnownFolderId = null,
    bool InstallerCache = false);
internal sealed record UninstallRegistryRoot(string? Sid, string Path, bool View32);
internal sealed record UninstallEvidence(UninstallResidue Finding, UninstallFileRoot? Root = null,
    CleanerFileTarget? File = null, string? RegistryData = null, int RegistryKind = 0, string? Sid = null,
    string[]? OwnerAppIds = null);

/// <summary>Stable per-product discovery evidence retained across the native uninstall.</summary>
internal sealed class UninstallProductPlan(ItemDefinition application)
{
    public ItemDefinition Application { get; } = application;
    public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<UninstallRegistration> Registrations { get; } = [];
    public List<UninstallFileRoot> FileRoots { get; } = [];
    public List<UninstallRegistryRoot> RegistryRoots { get; } = [];
    public Dictionary<string, UninstallPackage[]> Packages { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ReferencePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnreadableAccounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RegistryBefore { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> Executables { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Shared identity matching and validation for discovery, backup, deletion and recovery.</summary>
internal static class UninstallResources
{
    public const string UninstallBranch = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string LocalLowId = "{A520A1A4-1780-4FF6-BD18-167343C5AF16}";
    public static bool IsResourceError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or System.ComponentModel.Win32Exception;

    public static string NormalizeName(string name)
    {
        name = Regex.Replace(name, @"\s+(?:v?\d+(?:\.\d+)+|\(?x(?:86|64)\)?|\(?\d+-bit\)?).*$", "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    public static void AddName(UninstallProductPlan plan, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var normalized = NormalizeName(name);
        if (normalized.Length >= 3 && !new[] { "microsoft", "windows", "software", "programs", "commonfiles", "apps", "application", "data", "setup", "uninstall", "unins000", "update", "launcher" }.Contains(normalized))
            plan.Names.Add(normalized);
    }

    public static bool MatchesRegistration(ItemDefinition app, string keyName, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(app.RegistrySubKeyName) && AppStatusDiscoveryService.MatchesPattern(keyName, app.RegistrySubKeyName)) return true;
        if (!string.IsNullOrWhiteSpace(app.RegistryDisplayName)) return AppStatusDiscoveryService.MatchesPattern(displayName, app.RegistryDisplayName);
        return string.IsNullOrWhiteSpace(app.RegistrySubKeyName) && NormalizeName(displayName) == NormalizeName(app.Name);
    }

    public static bool Under(string path, string directory) => path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Access failures are never interpreted as absence during uninstall verification.</summary>
    public static bool Exists(string path, bool directory)
    {
        try { return ((File.GetAttributes(path) & FileAttributes.Directory) != 0) == directory; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static string LocalLow(ICleanerRegistrySession account)
    {
        var value = account.Reader.GetValues(account.UserPrefix + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
            false, CancellationToken.None).FirstOrDefault(value => value.Name.Equals(LocalLowId, StringComparison.OrdinalIgnoreCase));
        if (value != null && value.Data is not string) throw new IOException("The account's LocalLow location is invalid.");
        var configured = value?.Data as string ?? Path.Combine(account.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        return LocalPath(CleanerAccountEnvironment.Expand(configured, RegistryView.Registry64, account))
            ?? throw new IOException("The account's LocalLow location is not a resolved local directory.");
    }

    public static string AccountBase(UninstallFileRoot root, ICleanerRegistrySession account) => root.KnownFolderId switch
    {
        null => account.GetFolderPath(root.Folder ?? Environment.SpecialFolder.UserProfile),
        LocalLowId => LocalLow(account),
        _ => throw new IOException("The cleanup resource uses an unsupported account folder identity.")
    };

    public static string? LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');
        if (!CleanerPaths.IsLocalPath(path) || path.Contains('%') || path[3..].Contains(':') || path.IndexOfAny(['*', '?', '\0', '\r', '\n', '"']) >= 0) return null;
        return System.IO.Path.GetFullPath(path).TrimEnd('\\');
    }

    /// <summary>ARP paths use the registration's account and view; machine entries cannot borrow a user's folders.</summary>
    public static string ExpandMetadata(string value, ICleanerRegistrySession account, string? sid, bool view32)
    {
        if (sid == null && new[] { "%USERPROFILE%", "%APPDATA%", "%LOCALAPPDATA%" }
            .Any(variable => value.Contains(variable, StringComparison.OrdinalIgnoreCase))) return value;
        return CleanerAccountEnvironment.Expand(value, view32 ? RegistryView.Registry32 : RegistryView.Registry64, account);
    }

    public static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? LocalPath(text[1..close]) : null;
        }
        var extension = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return extension >= 0 ? LocalPath(text[..(extension + 4)]) : null;
    }

    public static bool References(string value, IReadOnlyCollection<string> paths)
    {
        foreach (var path in paths)
        {
            var offset = 0;
            while ((offset = value.IndexOf(path, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var end = offset + path.Length;
                var before = offset == 0 || !char.IsLetterOrDigit(value[offset - 1]) && value[offset - 1] is not '\\' and not '/' and not '_';
                var after = end == value.Length || value[end] is '\\' or '/' or '"' or '\'' or ',' or ';' or '|' or '\0' or '\r' or '\n' || char.IsWhiteSpace(value[end]);
                if (before && after) return true;
                offset = end;
            }
        }
        return false;
    }

    public static string Serialize(CleanerRegistryValue value) => JsonSerializer.Serialize(value.Data, value.Data.GetType());
    public static object Deserialize(int kind, string json) => (RegistryValueKind)kind switch
    {
        RegistryValueKind.String or RegistryValueKind.ExpandString => JsonSerializer.Deserialize<string>(json) ?? throw new IOException("Invalid registry string backup."),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(json) ?? throw new IOException("Invalid registry string-array backup."),
        RegistryValueKind.Binary or RegistryValueKind.None => JsonSerializer.Deserialize<byte[]>(json) ?? throw new IOException("Invalid registry binary backup."),
        RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(json),
        RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(json),
        _ => throw new IOException("Unsupported registry value type; the value was preserved.")
    };

    public static bool Supported(RegistryValueKind kind) => kind is RegistryValueKind.String or RegistryValueKind.ExpandString
        or RegistryValueKind.MultiString or RegistryValueKind.Binary or RegistryValueKind.None or RegistryValueKind.DWord or RegistryValueKind.QWord;

    public static void ValidateRoot(UninstallFileRoot root, ICleanerRegistrySessions sessions, IUninstallScanFolders folders,
        ICleanerRegistrySession? currentAccount = null)
    {
        var path = LocalPath(root.Path) ?? throw new IOException("The uninstall resource path is not a resolved local path.");
        var parent = System.IO.Path.GetDirectoryName(path) ?? throw new IOException("A drive root cannot be cleaned.");
        if (root.InstallerCache)
        {
            // Only individually captured installer files may use the system-cache recovery boundary.
            var allowed = new[] { Path.Combine(folders.WindowsDirectory, "Installer") }
                .Concat(folders.ApplicationDirectories.Select(directory => Path.Combine(directory, "Package Cache")));
            if (!root.SingleFile || root.Sid != null || !allowed.Contains(root.BaseDirectory, StringComparer.OrdinalIgnoreCase) ||
                !Under(path, root.BaseDirectory) || path.Equals(root.BaseDirectory, StringComparison.OrdinalIgnoreCase) ||
                !new[] { ".msi", ".msp", ".exe" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new IOException("The installer file is outside its captured cache boundary.");
            if (root.BaseDirectory.Equals(Path.Combine(folders.WindowsDirectory, "Installer"), StringComparison.OrdinalIgnoreCase) &&
                (!parent.Equals(root.BaseDirectory, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Only top-level MSI and MSP cache files belong to this installer boundary.");
            CleanerPaths.ValidateDescendant(root.BaseDirectory, path);
            return;
        }
        var systemProfile = root.Sid switch
        {
            "S-1-5-18" => Path.Combine(folders.WindowsDirectory, "System32", "config", "systemprofile"),
            "S-1-5-19" => Path.Combine(folders.WindowsDirectory, "ServiceProfiles", "LocalService"),
            "S-1-5-20" => Path.Combine(folders.WindowsDirectory, "ServiceProfiles", "NetworkService"),
            _ => null
        };
        if (Under(path, folders.WindowsDirectory) && (systemProfile == null || !Under(path, systemProfile)) ||
            folders.ApplicationDirectories.Any(directory => path.Equals(directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) ||
            path.Equals(root.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A shared system or profile directory cannot be removed as an uninstall leftover.");
        if (root.Sid != null)
        {
            using var ownedAccount = currentAccount == null ? sessions.Open(root.Sid) : null;
            var account = currentAccount ?? ownedAccount!;
            if (!account.UserPrefix.Equals(@"HKEY_USERS\" + root.Sid, StringComparison.Ordinal))
                throw new IOException("The cleanup directory belongs to a different account session.");
            var current = AccountBase(root, account);
            if (!current.Equals(root.BaseDirectory, StringComparison.OrdinalIgnoreCase) || !Under(path, current))
                throw new IOException("The Windows account's directory changed after discovery; scan again.");
        }
        CleanerPaths.ValidateDescendant(parent, path);
    }

    public static string ResourceIdentity(UninstallResidue finding) => $"{finding.AppId}\0{finding.Account}\0{finding.Kind}\0{finding.View32}\0{finding.Path}\0{finding.ValueName}";
    public static string PhysicalIdentity(UninstallEvidence entry) => $"{entry.Root?.Sid ?? entry.Sid}\0{entry.Finding.Kind}\0{entry.Finding.View32}\0{entry.Finding.Path}\0{entry.Finding.ValueName}";
    public static string Fingerprint(UninstallEvidence evidence) => evidence.RegistryData == null ? evidence.Finding.Kind.ToString()
        : evidence.RegistryKind + ":" + evidence.RegistryData;
}
