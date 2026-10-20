using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Finds verifiable stale references; never removes keys or treats access failures as corruption.</summary>
public interface ICleanerRegistryRecovery
{
    void Verify(CleanerRegistryTarget target);
    void Restore(CleanerRegistryTarget target);
}

/// <summary>Inventories selected registrations, resolves typed references and revalidates exact-value corrections.</summary>
public sealed partial class CleanerRegistryScanner(ICleanerRegistrySessions sessions, ICleanerBackupVault vault) : ICleanerRegistryRecovery
{
    private sealed record Rule(RegistryArea Area, string Path, int Depth = 1, bool NamesArePaths = false, bool DeepOnly = false);
    private static readonly Rule[] Rules =
    [
        new(RegistryArea.ActiveX, @"Software\Classes\CLSID", 2),
        new(RegistryArea.ActiveX, @"Software\Classes\*\shellex", 2),
        new(RegistryArea.ActiveX, @"Software\Classes\AllFilesystemObjects\shellex", 2),
        new(RegistryArea.ActiveX, @"Software\Classes\Directory\shellex", 2),
        new(RegistryArea.ActiveX, @"Software\Classes\Directory\Background\shellex", 2),
        new(RegistryArea.ActiveX, @"Software\Classes\Folder\shellex", 2),
        new(RegistryArea.ActiveX, @"Software\Classes\Drive\shellex", 2),
        new(RegistryArea.ActiveX, @"Software\Classes", 4, DeepOnly: true),
        new(RegistryArea.Integrity, @"Software\Classes\TypeLib", 5),
        new(RegistryArea.Integrity, @"Software\Classes\Interface", 2),
        new(RegistryArea.ApplicationPaths, @"Software\Microsoft\Windows\CurrentVersion\App Paths"),
        new(RegistryArea.Fonts, @"Software\Microsoft\Windows NT\CurrentVersion\Fonts", 0),
        new(RegistryArea.HelpFiles, @"Software\Microsoft\Windows\Help", 0),
        new(RegistryArea.HelpFiles, @"Software\Microsoft\Windows\HTML Help", 0),
        new(RegistryArea.History, @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths", 0),
        new(RegistryArea.SharedFiles, @"Software\Microsoft\Windows\CurrentVersion\SharedDLLs", 0, true),
        new(RegistryArea.Shortcuts, @"Software\Classes\Applications", 4),
        new(RegistryArea.Shortcuts, @"Software\Classes", 4, DeepOnly: true),
        new(RegistryArea.Software, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        new(RegistryArea.Sounds, @"AppEvents\Schemes\Apps", 3),
        new(RegistryArea.StartMenu, @"Software\Clients\StartMenuInternet", 4),
        new(RegistryArea.Startup, @"Software\Microsoft\Windows\CurrentVersion\Run", 0),
        new(RegistryArea.Startup, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", 0),
        new(RegistryArea.Startup, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", 0),
        new(RegistryArea.MuiCache, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", 0, true),
        new(RegistryArea.MuiCache, @"Software\Microsoft\Windows\ShellNoRoam\MUICache", 0, true),
        new(RegistryArea.VirtualDevices, @"SYSTEM\CurrentControlSet\Control\VirtualDeviceDrivers", 0),
        new(RegistryArea.FileExtensions, @"Software\Classes", 2),
        new(RegistryArea.SystemExtensions, @"Software\Classes", 2),
        new(RegistryArea.WindowsServices, @"SYSTEM\CurrentControlSet\Services", 1),
        new(RegistryArea.Firewall, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", 0)
    ];

    public string BackupDirectory => vault.RootDirectory;
    public IReadOnlyList<CleanerRegistryAccount> GetAccounts() => sessions.GetAccounts();

    public CleanerScanResult Scan(CleanerOptions options, CancellationToken ct, IProgress<CleanerProgress>? progress = null)
    {
        options.Validate();
        List<CleanerFinding> findings = [];
        List<string> notices = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> references = new(StringComparer.OrdinalIgnoreCase);
        List<(Rule Rule, string Path, RegistryView View, ICleanerRegistrySession Account)> plan = [];
        List<ICleanerRegistrySession> opened = [];
        try
        {
            var interactive = sessions.Open(null);
            opened.Add(interactive);
            List<ICleanerRegistrySession> selected = [];
            if (options.RegistryAccountSids is null) selected.Add(interactive);
            else foreach (var sid in options.RegistryAccountSids.Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                try { var account = sessions.Open(sid); opened.Add(account); selected.Add(account); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notices.Add($"{sid}: {ex.Message}"); }
            }
            progress?.Report(new("Cleaner_Registry", 0, 0));
            foreach (var rule in Rules.Where(x => options.RegistryAreas.Contains(x.Area) && (!x.DeepOnly || options.DeepClean)))
            {
                ct.ThrowIfCancellationRequested();
                // Selected categories include their actual Windows storage locations, including machine registrations.
                // Machine registrations are inventoried once, independently of account selection.
                foreach (var scope in selected.Select(account => (Account: account, Machine: false)).Append((Account: interactive, Machine: true)))
                {
                    var machine = scope.Machine;
                    if (rule.Path.StartsWith("SYSTEM\\", StringComparison.Ordinal) && !machine ||
                        rule.Area == RegistryArea.SystemExtensions && !machine || rule.Area == RegistryArea.FileExtensions && machine) continue;
                    var prefix = machine ? "HKEY_LOCAL_MACHINE" : scope.Account.UserPrefix;
                    // Most class associations and SYSTEM keys are shared across views. COM registrations are redirected.
                    bool separateViews = rule.Path is @"Software\Classes\CLSID" or @"Software\Classes\Interface" ||
                        machine && rule.Path.StartsWith("Software\\", StringComparison.Ordinal) &&
                        !rule.Path.StartsWith("Software\\Classes", StringComparison.Ordinal) &&
                        !rule.Path.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Policies\", StringComparison.Ordinal) &&
                        rule.Area is not (RegistryArea.ApplicationPaths or RegistryArea.Fonts or RegistryArea.StartMenu);
                    foreach (var view in separateViews && Environment.Is64BitOperatingSystem
                        ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry64 })
                        Discover(prefix + "\\" + rule.Path, rule.Depth, rule, view, scope.Account, plan, notices, progress, ct);
                }
            }
            progress?.Report(new("Cleaner_Registry", 0, plan.Count));
            for (int index = 0; index < plan.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var item = plan[index];
                Inspect(item.Path, item.Rule, item.View, item.Account, findings, notices, seen, references, ct);
                if ((index + 1) % 64 == 0 || index + 1 == plan.Count || plan[index + 1].Rule.Area != item.Rule.Area)
                    progress?.Report(new($"Cleaner_Registry_{item.Rule.Area}", index + 1, plan.Count));
            }
            return new(findings.AsReadOnly(), notices.AsReadOnly()) { CheckedItems = plan.Count };
        }
        finally
        {
            List<Exception> errors = [];
            foreach (var account in opened.AsEnumerable().Reverse())
                try { account.Dispose(); } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { errors.Add(ex); }
            if (errors.Count > 0) throw new IOException("Account registry sessions could not be released.", new AggregateException(errors));
        }
    }

    /// <summary>Builds the key inventory before checking references so progress has a known denominator.</summary>
    private void Discover(string path, int depth, Rule rule, RegistryView view, ICleanerRegistrySession account,
        List<(Rule Rule, string Path, RegistryView View, ICleanerRegistrySession Account)> plan, List<string> notices, IProgress<CleanerProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        plan.Add((rule, path, view, account));
        if (plan.Count % 256 == 0) progress?.Report(new("Cleaner_Registry", plan.Count, 0));
        if (depth <= 0) return;
        try
        {
            foreach (var child in account.Reader.GetSubKeyNames(path, view == RegistryView.Registry32))
            {
                ct.ThrowIfCancellationRequested();
                var name = child.ToUpperInvariant();
                if (rule.Area is RegistryArea.FileExtensions or RegistryArea.SystemExtensions &&
                    (depth == rule.Depth ? !child.StartsWith('.') : name != "OPENWITHPROGIDS")) continue;
                if (rule.DeepOnly)
                {
                    if (depth == rule.Depth && (name is "CLSID" or "TYPELIB" or "INTERFACE" or "APPID" or "INSTALLER" or "APPLICATIONS" ||
                        rule.Area == RegistryArea.Shortcuts && child.StartsWith('.'))) continue;
                    if (depth == rule.Depth - 1 && (rule.Area == RegistryArea.ActiveX
                        ? name is not ("CLSID" or "CURVER" or "SHELLEX") : name is not ("DEFAULTICON" or "SHELL"))) continue;
                    if (rule.Area == RegistryArea.Shortcuts && depth == 1 && name != "COMMAND") continue;
                    if (rule.Area == RegistryArea.ActiveX && depth < rule.Depth - 1 &&
                        !path.EndsWith("\\shellex", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains("\\shellex\\", StringComparison.OrdinalIgnoreCase)) continue;
                    // Predefined objects have their own standard rules; only file-type handlers need deep discovery.
                    if (rule.Area == RegistryArea.ActiveX && depth == rule.Depth - 1 && name == "SHELLEX" &&
                        Path.GetFileName(path).ToUpperInvariant() is "*" or "ALLFILESYSTEMOBJECTS" or "DIRECTORY" or "FOLDER" or "DRIVE") continue;
                }
                Discover(path + "\\" + child, depth - 1, rule, view, account, plan, notices, progress, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.ComponentModel.Win32Exception)
        { notices.Add($"{path}: {ex.Message}"); }
    }

    private void Inspect(string displayPath, Rule rule, RegistryView view, ICleanerRegistrySession account,
        List<CleanerFinding> findings, List<string> notices, HashSet<string> seen, Dictionary<string, bool> references, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var values = account.Reader.GetValues(displayPath, view == RegistryView.Registry32, ct, notices);
            foreach (var entry in values)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var name = entry.Name;
                    var value = entry.Data;
                    var referenceView = ReferenceView(displayPath, view);
                    var missing = value is string[] entries && rule.Area == RegistryArea.VirtualDevices && IsReferenceField(rule.Area, displayPath, name)
                        ? entries.Select(x =>
                        {
                            ct.ThrowIfCancellationRequested();
                            return ExtractLocalPath(CleanerAccountEnvironment.Expand(x, view, account));
                        }).FirstOrDefault(x => x is not null && IsMissing(x, referenceView))
                        : GetReference(rule, displayPath, name, value, referenceView, account);
                    if (missing is null || !IsMissing(missing, referenceView)) continue;
                    var identity = $"{view}|{displayPath}|{name}";
                    if (!seen.Add(identity)) continue;
                    bool diagnostic = rule.Area is RegistryArea.WindowsServices or RegistryArea.VirtualDevices or RegistryArea.Firewall;
                    var target = new CleanerRegistryTarget(displayPath, name, Serialize(value), (int)entry.Kind, view == RegistryView.Registry32, missing, diagnostic)
                        { ReferenceArea = rule.Area };
                    findings.Add(new(Guid.NewGuid(), CleanerArea.Registry, $"Cleaner_Registry_{rule.Area}",
                        $"{displayPath}\\{(name.Length == 0 ? "(Default)" : name)} ({(view == RegistryView.Registry32 ? "32-bit" : "64-bit")}) → {missing}", 0, !diagnostic, target));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.ComponentModel.Win32Exception)
                { notices.Add($"{displayPath}\\{entry.Name} ({(view == RegistryView.Registry32 ? "32-bit" : "64-bit")}): {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.ComponentModel.Win32Exception)
        { notices.Add($"{displayPath}: {ex.Message}"); }

        // Cache only successful probes within this scan. Cleanup always checks live state again.
        bool IsMissing(string reference, RegistryView referenceView)
        {
            var identity = $"{account.UserPrefix}|{referenceView}|{reference}";
            if (references.TryGetValue(identity, out var missing)) return missing;
            missing = IsMissingReference(reference, referenceView, account);
            references.Add(identity, missing);
            return missing;
        }
    }

    /// <summary>Distinguishes shared storage from the architecture of the referenced COM component or module.</summary>
    internal static RegistryView ReferenceView(string key, RegistryView storageView) =>
        IsShellHandlerReference(key) || (key.EndsWith("\\CLSID", StringComparison.OrdinalIgnoreCase) &&
            !key.EndsWith(@"\Classes\CLSID", StringComparison.OrdinalIgnoreCase)) ? RegistryView.Default :
        key.Contains(@"\Classes\TypeLib\", StringComparison.OrdinalIgnoreCase) && key.EndsWith(@"\win32", StringComparison.OrdinalIgnoreCase)
            ? RegistryView.Registry32 : storageView;

    private string? GetReference(Rule rule, string key, string name, object value, RegistryView view, ICleanerRegistrySession account)
    {
        if (rule.Area is RegistryArea.FileExtensions or RegistryArea.SystemExtensions)
        {
            if (key.EndsWith("\\OpenWithProgids", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(Path.GetDirectoryName(key) ?? "").StartsWith('.')) return ClassReference(name);
            return name == "" && Path.GetFileName(key).StartsWith('.') && value is string progId ? ClassReference(progId) : null;
        }
        if (!IsReferenceField(rule.Area, key, name)) return null;
        if (rule.Area == RegistryArea.ActiveX)
        {
            if (IsShellHandlerReference(key)) return GuidReference(value, "CLSID");
            var branch = ClassMappingBranch(key);
            if (branch is not null) return branch.Length == 0 && value is string progId
                ? ClassReference(progId) : GuidReference(value, branch);
        }
        if (rule.Area == RegistryArea.Integrity && key.Contains(@"\Classes\Interface\", StringComparison.OrdinalIgnoreCase))
        {
            if (value is not string id || !Guid.TryParseExact(id, "B", out _)) return null;
            return "class:" + (key.EndsWith(@"\TypeLib", StringComparison.OrdinalIgnoreCase) ? @"TypeLib\" : @"CLSID\") + id;
        }
        string? text = rule.NamesArePaths ? name : value as string;
        if (text is null) return null;
        if (rule.Area == RegistryArea.MuiCache)
        {
            foreach (var suffix in new[] { ".FriendlyAppName", ".ApplicationCompany" })
                if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) text = text[..^suffix.Length];
        }
        if (rule.Area == RegistryArea.Firewall)
            text = text.Split('|').FirstOrDefault(x => x.StartsWith("App=", StringComparison.OrdinalIgnoreCase))?[4..];
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = CleanerAccountEnvironment.Expand(text.Trim(' '), view, account);
        if (rule.Area == RegistryArea.Fonts && !Path.IsPathRooted(text))
            text = Path.Combine(key.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Fonts)
                : Path.Combine(account.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts"), text);
        if (rule.Area == RegistryArea.HelpFiles && !Path.HasExtension(text) && Path.HasExtension(name))
            text = Path.Combine(text, name);
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), text[12..]);
        bool command = rule.Area is RegistryArea.Startup or RegistryArea.WindowsServices ||
            key.EndsWith("\\command", StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\LocalServer32", StringComparison.OrdinalIgnoreCase) ||
            rule.Area == RegistryArea.Software && !name.Equals("DisplayIcon", StringComparison.OrdinalIgnoreCase);
        if (command) return ExtractCommandPath(text);
        if (rule.Area == RegistryArea.Software || key.EndsWith("\\DefaultIcon", StringComparison.OrdinalIgnoreCase))
            return ExtractIconPath(text);
        var localPath = ExtractLocalPath(text);
        return rule.Area == RegistryArea.Integrity && localPath is not null
            ? Regex.Replace(localPath, @"(?<=\.(?:dll|ocx|exe|tlb|olb))\\[0-9]+$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : localPath;
    }

    /// <summary>Restricts each registration type to fields whose semantics are file references.</summary>
    internal static bool IsReferenceField(RegistryArea area, string key, string name) => area switch
    {
        RegistryArea.Software => name.Equals("DisplayIcon", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("UninstallString", StringComparison.OrdinalIgnoreCase) || name.Equals("QuietUninstallString", StringComparison.OrdinalIgnoreCase),
        RegistryArea.ApplicationPaths => name == "",
        RegistryArea.Integrity => name == "" && (key.EndsWith("\\win32", StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\win64", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(@"\Classes\Interface\", StringComparison.OrdinalIgnoreCase) &&
            (key.EndsWith("\\ProxyStubClsid32", StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\TypeLib", StringComparison.OrdinalIgnoreCase))),
        RegistryArea.Shortcuts or RegistryArea.StartMenu => name == "" && (key.EndsWith("\\command", StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\DefaultIcon", StringComparison.OrdinalIgnoreCase)),
        RegistryArea.WindowsServices => name.Equals("ImagePath", StringComparison.OrdinalIgnoreCase),
        RegistryArea.ActiveX => name == "" && (key.EndsWith("\\InprocServer32", StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\InprocHandler32", StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\LocalServer32", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(@"\Classes\CLSID\", StringComparison.OrdinalIgnoreCase) && key.EndsWith("\\DefaultIcon", StringComparison.OrdinalIgnoreCase) ||
            ClassMappingBranch(key) is not null || IsShellHandlerReference(key)),
        RegistryArea.Fonts or RegistryArea.HelpFiles or RegistryArea.SharedFiles or RegistryArea.Startup or RegistryArea.MuiCache => name.Length > 0,
        RegistryArea.History => Regex.IsMatch(name, @"^url[0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        RegistryArea.Sounds => name == "",
        RegistryArea.VirtualDevices => name.Equals("VDD", StringComparison.OrdinalIgnoreCase),
        RegistryArea.Firewall => name.Length > 0,
        _ => false
    };

    /// <summary>Accepts only a single class name, keeping malformed or indirect registrations untouched.</summary>
    private static string? ClassReference(string name) =>
        !string.IsNullOrWhiteSpace(name) && name == name.Trim() && !name.Any(char.IsControl) &&
        name.IndexOfAny(['\\', '/', ':', '%', '"']) < 0 ? "class:" + name : null;

    /// <summary>Resolves explicit COM identifiers; the null GUID is a sentinel rather than a missing component.</summary>
    private static string? GuidReference(object value, string branch) =>
        value is string id && Guid.TryParseExact(id, "B", out var guid) && guid != Guid.Empty ? @"class:" + branch + "\\" + id : null;

    /// <summary>Identifies documented direct class mappings, excluding descriptions and nested metadata.</summary>
    private static string? ClassMappingBranch(string key)
    {
        const string marker = @"\Software\Classes\";
        int start = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var parts = key[(start + marker.Length)..].ToUpperInvariant().Split('\\');
        return parts.Length switch
        {
            2 when parts[1] == "CLSID" => "CLSID",
            2 when parts[1] == "CURVER" => "",
            3 when parts[0] == "CLSID" && parts[2] is "TREATAS" or "AUTOTREATAS" or "AUTOCONVERTTO" => "CLSID",
            3 when parts[0] == "CLSID" && parts[2] == "TYPELIB" => "TypeLib",
            3 when parts[0] == "CLSID" && parts[2] is "PROGID" or "VERSIONINDEPENDENTPROGID" => "",
            _ => null
        };
    }

    /// <summary>Recognizes single-handler slots and named multi-handler values without interpreting arbitrary ShellEx metadata.</summary>
    private static bool IsShellHandlerReference(string key)
    {
        const string marker = @"\shellex\";
        int start = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        var parts = key[(start + marker.Length)..].ToUpperInvariant().Split('\\');
        return parts.Length == 2 && parts[0] is "CONTEXTMENUHANDLERS" or "COPYHOOKHANDLERS" or "DRAGDROPHANDLERS" or "PROPERTYSHEETHANDLERS" or "COLUMNHANDLERS" ||
            parts.Length == 1 && (parts[0] is "DATAHANDLER" or "DROPHANDLER" or "ICONHANDLER" or "PROPERTYHANDLER" || Guid.TryParseExact(parts[0], "B", out _));
    }

    /// <summary>Uses the reference's registry view and interactive profile, preserving unresolved user variables.</summary>
    internal string ExpandReferenceEnvironment(string text, RegistryView view)
    {
        using var account = sessions.Open(null);
        return CleanerAccountEnvironment.Expand(text, view, account);
    }

    /// <summary>Reads an entire literal file reference without interpreting executable suffixes as arguments.</summary>
    internal static string? ExtractLocalPath(string text)
    {
        text = text.Trim(' ');
        if (text.StartsWith('"'))
        {
            if (text.Length < 2 || !text.EndsWith('"')) return null;
            text = text[1..^1];
        }
        return CleanerPaths.IsLocalPath(text) && !text.Any(char.IsControl) &&
            text.IndexOfAny(['"', '|', '<', '>', '*', '?', '%', '/']) < 0 && !text[3..].Contains(':') ? text : null;
    }

    /// <summary>Separates the numeric icon index from a complete, optionally quoted resource path.</summary>
    internal static string? ExtractIconPath(string text) =>
        ExtractLocalPath(Regex.Replace(text.TrimStart('@'), @",\s*-?[0-9]+\s*$", "", RegexOptions.CultureInvariant));

    /// <summary>Resolves explicit executable tokens; relative launchers and ambiguous unquoted paths are preserved.</summary>
    internal static string? ExtractCommandPath(string text)
    {
        if (text.Any(x => char.IsControl(x) && x != '\t')) return null;
        text = text.Trim();
        string token;
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end < 0 || end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1])) return null;
            token = text[1..end];
        }
        else
        {
            int end = text.IndexOfAny([' ', '\t']);
            token = end < 0 ? text : text[..end];
            // An unquoted executable-looking directory can be part of a longer module path.
            // Only a switch or quoted argument makes this boundary unambiguous enough to clean.
            if (end >= 0)
            {
                var arguments = text[end..].TrimStart();
                if (arguments.Length > 0 && arguments[0] is not ('/' or '-' or '"')) return null;
            }
        }
        var path = ExtractLocalPath(token);
        return path is not null && Regex.IsMatch(Path.GetExtension(path), @"^\.(exe|com|cmd|bat|ps1|vbs|js)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? path : null;
    }

    private static bool IsMissingReference(string reference, RegistryView view, ICleanerRegistrySession account)
    {
        if (!reference.StartsWith("class:", StringComparison.Ordinal))
        {
            // A 32-bit consumer may resolve System32 through WOW64 file-system redirection.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var system32 = Path.Combine(windows, "System32") + "\\";
            var sysnative = Path.Combine(windows, "Sysnative") + "\\";
            if (view == RegistryView.Registry32 && reference.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
                return CleanerPaths.IsMissingLocalFile(Path.Combine(windows, "SysWOW64", reference[system32.Length..]));
            if (view == RegistryView.Registry32 && reference.StartsWith(sysnative, StringComparison.OrdinalIgnoreCase))
                return CleanerPaths.IsMissingLocalFile(Path.Combine(windows, "System32", reference[sysnative.Length..]));
            return CleanerPaths.IsMissingLocalFile(reference);
        }
        var name = reference[6..];
        if (view == RegistryView.Default)
            return IsMissingReference(reference, RegistryView.Registry64, account) &&
                (!Environment.Is64BitOperatingSystem || IsMissingReference(reference, RegistryView.Registry32, account));
        return !account.Reader.KeyExists(@"HKEY_LOCAL_MACHINE\Software\Classes\" + name, view == RegistryView.Registry32) &&
            !account.Reader.KeyExists(account.UserPrefix + @"\Software\Classes\" + name, view == RegistryView.Registry32);
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType());

    /// <summary>Checks the exact scan evidence immediately before backup and correction.</summary>
    public void Verify(CleanerRegistryTarget target)
    {
        if (target.DiagnosticOnly) throw new InvalidOperationException("This reference requires manual review.");
        using var account = OpenTargetSession(target);
        using var key = OpenTarget(target, writable: false, account);
        var value = key.ReadValue(target.ValueName);
        if (!MatchesEvidence(target, value, account))
            throw new IOException("Registry reference changed after the scan. Scan again.");
    }

    /// <summary>Deletes only the selected unchanged value; session backup is owned by CleanerService.</summary>
    public Task CleanAsync(CleanerRegistryTarget target, CancellationToken ct) => Task.Run(() =>
    {
        Verify(target);
        ct.ThrowIfCancellationRequested();
        using var account = OpenTargetSession(target);
        using var key = OpenTarget(target, writable: true, account);
        var value = key.ReadValue(target.ValueName);
        if (!MatchesEvidence(target, value, account))
            throw new IOException("Registry reference changed after the scan. Scan again.");
        ct.ThrowIfCancellationRequested();
        key.DeleteValue(target.ValueName);
    }, ct);

    /// <summary>Rechecks raw evidence and its resolved profile path, including redirects changed after scanning.</summary>
    private bool MatchesEvidence(CleanerRegistryTarget target, CleanerRegistryValue? value, ICleanerRegistrySession account)
    {
        if (value is null || Serialize(value.Data) != target.Data || (int)value.Kind != target.Kind) return false;
        if (target.PrivacyRule is not null)
            return !target.View32 && target.ReferenceArea is null && target.MissingPath.Length == 0 &&
                PrivacyRuleFor(target, account) is { } privacy && IsPrivacyValue(privacy, value);
        var view = ReferenceView(target.KeyPath, target.View32 ? RegistryView.Registry32 : RegistryView.Registry64);
        if (target.ReferenceArea is { } area)
        {
            var rule = Rules.FirstOrDefault(rule => rule.Area == area) ?? throw new IOException("Unknown registry reference rule.");
            var resolved = GetReference(rule, target.KeyPath, target.ValueName, value.Data, view, account);
            if (!string.Equals(resolved, target.MissingPath, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return IsMissingReference(target.MissingPath, view, account);
    }

    /// <summary>Restores one missing value without importing or overwriting an entire registry branch.</summary>
    public void Restore(CleanerRegistryTarget target)
    {
        if (target.DiagnosticOnly) throw new IOException("Diagnostic registry entries cannot be restored automatically.");
        using var account = OpenTargetSession(target);
        using var key = OpenTarget(target, writable: true, account);
        var value = key.ReadValue(target.ValueName);
        if (value is not null)
        {
            if (Serialize(value.Data) == target.Data && (int)value.Kind == target.Kind) return;
            throw new IOException("A different registry value now exists; it has been preserved.");
        }
        object original = (RegistryValueKind)target.Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => JsonSerializer.Deserialize<string>(target.Data) ?? throw new IOException("Invalid registry backup string."),
            RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(target.Data) ?? throw new IOException("Invalid registry backup array."),
            RegistryValueKind.Binary or RegistryValueKind.None => JsonSerializer.Deserialize<byte[]>(target.Data) ?? throw new IOException("Invalid registry backup bytes."),
            RegistryValueKind.DWord => JsonSerializer.Deserialize<int>(target.Data),
            RegistryValueKind.QWord => JsonSerializer.Deserialize<long>(target.Data),
            _ => throw new IOException("Unsupported registry backup type.")
        };
        if (target.PrivacyRule is not null && (PrivacyRuleFor(target, account) is not { } privacy ||
            !IsPrivacyValue(privacy, new(target.ValueName, original, (RegistryValueKind)target.Kind))))
            throw new IOException("Invalid privacy registry backup value.");
        key.SetValue(new(target.ValueName, original, (RegistryValueKind)target.Kind));
    }

    private ICleanerRegistrySession OpenTargetSession(CleanerRegistryTarget target)
    {
        const string users = @"HKEY_USERS\";
        if (!target.KeyPath.StartsWith(users, StringComparison.Ordinal)) return sessions.Open(null);
        var sid = target.KeyPath[users.Length..].Split('\\')[0];
        return sessions.Open(sid);
    }

    private static ICleanerRegistryKey OpenTarget(CleanerRegistryTarget target, bool writable, ICleanerRegistrySession account)
    {
        if (target.KeyPath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0 || target.ValueName.Contains('\0')) throw new IOException("Invalid registry path.");
        int separator = target.KeyPath.IndexOf('\\');
        if (separator < 1) throw new IOException("Unsupported registry hive.");
        var prefix = target.KeyPath[..separator];
        if (prefix != "HKEY_LOCAL_MACHINE" && !target.KeyPath.StartsWith(account.UserPrefix + "\\", StringComparison.Ordinal))
            throw new IOException("Unsupported registry hive or user.");
        if (target.PrivacyRule is not null && PrivacyRuleFor(target, account) is null)
            throw new IOException("Unsupported privacy registry target.");
        return account.Editor.OpenKey(target.KeyPath, target.View32, writable);
    }
}
