using Microsoft.Win32;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Cleaner.Models;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

public sealed partial class CleanerRegistryScanner
{
    private sealed record PrivacyRegistryRule(string Id, string Path, string Values, int Depth = 0, bool Application = false);
    private static readonly PrivacyRegistryRule[] PrivacyRules =
    [
        new("Cleaner_RunHistory", @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU", @"(?:[a-z]|MRUList)"),
        new("Cleaner_ExplorerHistory", @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths", @"url[0-9]+"),
        new("Cleaner_ExplorerHistory", @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", @"(?:[0-9]+|MRUListEx)", 1),
        new("Cleaner_ExplorerHistory", @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU", @"(?:[0-9]+|MRUListEx)", 1),
        new("Cleaner_ExplorerHistory", @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU", @"(?:[0-9]+|MRUListEx)"),
        new("Cleaner_MmcHistory", @"Software\Microsoft\Microsoft Management Console\Recent File List", @"File[0-9]+"),
        new("Cleaner_MuiHistory", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", @".+"),
        new("Cleaner_MuiHistory", @"Software\Microsoft\Windows\ShellNoRoam\MUICache", @".+"),
        new("Cleaner_WinRarHistory", @"Software\WinRAR\ArcHistory", @"[0-9]+", Application: true),
        new("Cleaner_WinRarHistory", @"Software\WinRAR\DialogEditHistory\ArcName", @"[0-9]+", Application: true),
        new("Cleaner_WinRarHistory", @"Software\WinRAR\DialogEditHistory\ExtrPath", @"[0-9]+", Application: true)
    ];
    private const string OfficeRoot = @"Software\Microsoft\Office\";
    private static readonly string[] OfficeVersions = ["12.0", "14.0", "15.0", "16.0"];
    private static readonly string[] OfficeApps = ["Word", "Excel", "PowerPoint", "Access", "Publisher"];
    private static readonly string[] OfficeLists = ["File MRU", "Place MRU"];

    /// <summary>Inventories allowlisted history values for the privacy account selection, retaining exact typed evidence.</summary>
    public CleanerScanResult ScanPrivacy(CleanerOptions options, CancellationToken ct, IProgress<CleanerProgress>? progress = null)
    {
        options.Validate();
        if (!options.WindowsHistory && !options.ApplicationHistory) return new([], []);
        List<string> notices = [];
        List<CleanerFinding> findings = [];
        List<(PrivacyRegistryRule Rule, string Path, IReadOnlyList<CleanerRegistryValue> Values)> inventory = [];
        progress?.Report(new("Cleaner_Privacy", 0, 0));
        foreach (var (_, account) in CleanerAccounts.OpenSelected(sessions, options.PrivacyAccountSids, notices, ct))
        {
            List<(PrivacyRegistryRule Rule, string Path)> plan = [];
            foreach (var rule in PrivacyRules.Where(rule => rule.Application ? options.ApplicationHistory : options.WindowsHistory))
                DiscoverPrivacy(rule, account.UserPrefix + "\\" + rule.Path, rule.Depth, account, plan, notices, ct);
            if (options.ApplicationHistory)
                foreach (var version in OfficeVersions)
                    foreach (var app in OfficeApps)
                    {
                        ct.ThrowIfCancellationRequested();
                        var root = OfficeRoot + version + "\\" + app;
                        foreach (var list in OfficeLists)
                            plan.Add((OfficeRule(root + "\\" + list), account.UserPrefix + "\\" + root + "\\" + list));
                        var users = account.UserPrefix + "\\" + root + @"\User MRU";
                        try
                        {
                            foreach (var user in account.Reader.GetSubKeyNames(users, false))
                                foreach (var list in OfficeLists)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var relative = root + @"\User MRU\" + user + "\\" + list;
                                    plan.Add((OfficeRule(relative), account.UserPrefix + "\\" + relative));
                                }
                        }
                        catch (Exception ex) when (IsPrivacyReadError(ex)) { notices.Add($"{users}: {ex.Message}"); }
                    }
            foreach (var (rule, path) in plan)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<CleanerRegistryValue> values = [];
                try
                {
                    values = account.Reader.GetValues(path, false, ct, notices);
                }
                catch (Exception ex) when (IsPrivacyReadError(ex)) { notices.Add($"{path}: {ex.Message}"); }
                inventory.Add((rule, path, values));
                if (inventory.Count % 256 == 0) progress?.Report(new("Cleaner_Privacy", inventory.Count, 0));
            }
        }
        // Read within each account's session, then inspect the complete inventory with one measured denominator.
        progress?.Report(new("Cleaner_Privacy", 0, inventory.Count));
        for (int index = 0; index < inventory.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (rule, path, values) = inventory[index];
            foreach (var value in values)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsPrivacyValue(rule, value)) continue;
                var target = new CleanerRegistryTarget(path, value.Name, Serialize(value.Data), (int)value.Kind, false, "") { PrivacyRule = rule.Id };
                findings.Add(new(Guid.NewGuid(), CleanerArea.Privacy, rule.Id, path + "\\" + value.Name, 0, true, target));
            }
            if ((index + 1) % 64 == 0 || index + 1 == inventory.Count) progress?.Report(new("Cleaner_Privacy", index + 1, inventory.Count));
        }
        return new(findings.AsReadOnly(), notices.AsReadOnly()) { CheckedItems = inventory.Count };
    }

    /// <summary>Visits only the configured number of history subkeys; unreadable branches do not hide other histories.</summary>
    private static void DiscoverPrivacy(PrivacyRegistryRule rule, string path, int depth, ICleanerRegistrySession account,
        List<(PrivacyRegistryRule Rule, string Path)> plan, List<string> notices, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        plan.Add((rule, path));
        if (depth == 0) return;
        try
        {
            foreach (var child in account.Reader.GetSubKeyNames(path, false))
                DiscoverPrivacy(rule, path + "\\" + child, depth - 1, account, plan, notices, ct);
        }
        catch (Exception ex) when (IsPrivacyReadError(ex)) { notices.Add($"{path}: {ex.Message}"); }
    }

    private static PrivacyRegistryRule OfficeRule(string path) => new("Cleaner_OfficeHistory", path, @"Item [0-9]+", Application: true);
    private static bool IsPrivacyReadError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or ArgumentException or System.ComponentModel.Win32Exception;

    /// <summary>Recognizes history payloads without treating configuration values as traces.</summary>
    private static bool IsPrivacyValue(PrivacyRegistryRule rule, CleanerRegistryValue value)
    {
        if (!Regex.IsMatch(value.Name, "\\A(?:" + rule.Values + ")\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (rule.Id == "Cleaner_MuiHistory")
        {
            var name = Regex.Replace(value.Name, @"\.(?:FriendlyAppName|ApplicationCompany)$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return value.Kind == RegistryValueKind.String && ExtractLocalPath(name) is not null;
        }
        return value.Data switch
        {
            string text => text.Length > 0 && value.Kind == RegistryValueKind.String,
            byte[] bytes => bytes.Length > 0 && value.Kind == RegistryValueKind.Binary &&
                (rule.Path.Contains("RecentDocs", StringComparison.Ordinal) || rule.Path.Contains("PidlMRU", StringComparison.Ordinal)),
            _ => false
        };
    }

    /// <summary>Revalidates the history allowlist during backup, cleanup and recovery, including persisted targets.</summary>
    private static PrivacyRegistryRule? PrivacyRuleFor(CleanerRegistryTarget target, ICleanerRegistrySession account)
    {
        if (!target.KeyPath.StartsWith(account.UserPrefix + "\\", StringComparison.Ordinal) || target.View32 || target.ReferenceArea is not null ||
            target.DiagnosticOnly || target.MissingPath.Length != 0) return null;
        var relative = target.KeyPath[(account.UserPrefix.Length + 1)..];
        foreach (var rule in PrivacyRules.Where(rule => rule.Id == target.PrivacyRule))
            if (relative.Equals(rule.Path, StringComparison.OrdinalIgnoreCase) || rule.Depth == 1 &&
                relative.StartsWith(rule.Path + "\\", StringComparison.OrdinalIgnoreCase) && !relative[(rule.Path.Length + 1)..].Contains('\\')) return rule;
        if (target.PrivacyRule != "Cleaner_OfficeHistory" || !relative.StartsWith(OfficeRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var parts = relative[OfficeRoot.Length..].Split('\\');
        return parts.Length is 3 or 5 && OfficeVersions.Contains(parts[0], StringComparer.OrdinalIgnoreCase) &&
            OfficeApps.Contains(parts[1], StringComparer.OrdinalIgnoreCase) && OfficeLists.Contains(parts[^1], StringComparer.OrdinalIgnoreCase) &&
            (parts.Length == 3 || parts[2].Equals("User MRU", StringComparison.OrdinalIgnoreCase) && parts[3].Length > 0)
            ? OfficeRule(relative) : null;
    }
}
