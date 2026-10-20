namespace WinOptimizer.Core.Features.Cleaner.Models;

public enum CleanerArea { Privacy, JunkFiles, Shortcuts, RegistryDefrag, Registry, Disk }
public enum RegistryArea
{
    ActiveX, Integrity, ApplicationPaths, Fonts, HelpFiles, History, SharedFiles,
    Shortcuts, Software, Sounds, StartMenu, Startup, MuiCache, VirtualDevices,
    FileExtensions, SystemExtensions, WindowsServices, Firewall
}

/// <summary>Persisted cleanup choices; an application cache is opt-in by package directory name.</summary>
public sealed record CleanerOptions
{
    public RegistryArea[] RegistryAreas { get; init; } = Enum.GetValues<RegistryArea>();
    /// <summary>Null uses the interactive account; an empty selection scans machine registrations only.</summary>
    public string[]? RegistryAccountSids { get; init; }
    /// <summary>Independent profile selections; null uses the interactive account and empty skips user data.</summary>
    public string[]? PrivacyAccountSids { get; init; }
    public string[]? JunkAccountSids { get; init; }
    public string[]? ShortcutAccountSids { get; init; }
    public bool DeepClean { get; init; }
    public bool BackupBeforeCleaning { get; init; } = true;
    public bool BackupBeforeRegistryDefrag { get; init; } = true;
    public bool RecentFiles { get; init; } = true;
    public bool BrowserCache { get; init; } = true;
    public bool BrowserHistory { get; init; }
    public bool BrowserOfflineCache { get; init; }
    public bool WindowsHistory { get; init; } = true;
    public bool ApplicationHistory { get; init; }
    public bool ApplicationPrivacyCaches { get; init; } = true;
    public bool CertificateCache { get; init; } = true;
    public bool JumpLists { get; init; }
    public bool DefenderLogs { get; init; }
    public bool UserTemp { get; init; } = true;
    public bool WindowsTemp { get; init; } = true;
    public bool WindowsLogs { get; init; } = true;
    public bool ShaderCache { get; init; } = true;
    public bool ThumbnailCache { get; init; } = true;
    public bool ErrorReports { get; init; } = true;
    public bool CrashDumps { get; init; } = true;
    public bool InternetCache { get; init; } = true;
    public bool DesktopShortcuts { get; init; } = true;
    public bool StartMenuShortcuts { get; init; } = true;
    /// <summary>Checks recent-document links for missing targets independently of privacy-history cleanup.</summary>
    public bool RecentShortcuts { get; init; } = true;
    /// <summary>Minimum modification age for actionable junk files; zero includes recent files.</summary>
    public int MinimumAgeDays { get; init; } = 7;
    public string[] ApplicationCaches { get; init; } = [];
    public string DiskRoot { get; init; } = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    /// <summary>Validates persisted input before it reaches Windows services.</summary>
    public CleanerOptions Validate()
    {
        if (RegistryAreas is null || RegistryAreas.Any(x => !Enum.IsDefined(x)) ||
            new[] { RegistryAccountSids, PrivacyAccountSids, JunkAccountSids, ShortcutAccountSids }
                .Any(selection => selection is not null && selection.Any(sid => !IsAccountSid(sid))) ||
            ApplicationCaches is null || ApplicationCaches.Any(x => string.IsNullOrWhiteSpace(x) || x.IndexOfAny(['/', '\\', ':', '"']) >= 0 || x is "." or "..") ||
            MinimumAgeDays is < 0 or > 365 || string.IsNullOrEmpty(DiskRoot) ||
            DiskRoot.Length != 3 || !char.IsAsciiLetter(DiskRoot[0]) || DiskRoot[1..] != ":\\")
            throw new ArgumentException("Invalid Cleaner preferences. Correct the values or reset the Cleaner configuration.");
        return this;
    }

    /// <summary>Accepts canonical local/domain and Microsoft Entra account SIDs, never registry subpaths.</summary>
    public static bool IsAccountSid(string? sid) => sid is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(sid, @"\AS-1-(?:5-21|12-1)(?:-[0-9]{1,10}){4}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
        sid.Split('-').Skip(4).All(part => uint.TryParse(part, out _));
}

/// <summary>Immutable scan evidence, revalidated by the owning Windows component before modification.</summary>
public abstract record CleanerTarget;
public sealed record CleanerFileTarget(string Root, string Path, long Length, DateTime LastWriteUtc,
    DateTime CutoffUtc, string? ShortcutTarget = null) : CleanerTarget
{
    /// <summary>Reopens the original account and resolves its cleanup root again during backup, deletion and recovery.</summary>
    public CleanerProfilePath? Profile { get; init; }
}
/// <summary>A cleanup directory relative to one account's registered special folder.</summary>
public sealed record CleanerProfilePath(string? AccountSid, Environment.SpecialFolder Folder, string RelativePath);
public sealed record CleanerRegistryTarget(string KeyPath, string ValueName, string Data, int Kind,
    bool View32, string MissingPath, bool DiagnosticOnly = false) : CleanerTarget
{
    /// <summary>Retains the reference rule so profile variables can be resolved again before correction.</summary>
    public RegistryArea? ReferenceArea { get; init; }
    /// <summary>An allowlisted privacy history rule, independent of missing-file reference checks.</summary>
    public string? PrivacyRule { get; init; }
}
public sealed record CleanerDiskTarget(string Root) : CleanerTarget;
public sealed record CleanerHiveTarget(string KeyPath) : CleanerTarget
{
    /// <summary>Prevents a changed profile registration from redirecting an existing compaction plan.</summary>
    public string? SourcePath { get; init; }
}

public sealed record CleanerFinding(Guid Id, CleanerArea Area, string LabelKey, string Detail,
    long Bytes, bool CanClean, CleanerTarget Target)
{
    /// <summary>Explains why a detected resource cannot be selected in this scan.</summary>
    public string? UnavailableReason { get; init; }
}
public sealed record CleanerScanResult(IReadOnlyList<CleanerFinding> Findings, IReadOnlyList<string> Notices)
{
    public int CheckedItems { get; init; }
}
public sealed record CleanerApplyResult(int Completed, IReadOnlyList<string> Errors);
public sealed record CleanerApplication(string Id, string DisplayName);
public sealed record CleanerApplicationInventory(IReadOnlyList<CleanerApplication> Applications, IReadOnlyList<string> Notices);
/// <summary>A registered Windows profile offered for account-scoped Cleaner operations.</summary>
public sealed record CleanerRegistryAccount(string Sid, string DisplayName, bool IsCurrent);

/// <summary>Measured work within a scan/apply stage; an unknown total means discovery or a native Windows call.</summary>
public sealed record CleanerProgress(string Message, int Completed, int Total, int Stage = 1, int Stages = 1)
{
    public bool IsIndeterminate => Total <= 0;
    public double Percentage => 100d * (Stage - 1 + (Total > 0 ? Math.Clamp((double)Completed / Total, 0, 1) : 0)) / Math.Max(1, Stages);
}

public sealed record CleanerBackupInfo(Guid Id, DateTime CreatedUtc, int FileCount, int RegistryValueCount)
{
    public string DisplayName => $"{CreatedUtc.ToLocalTime():g} — {FileCount} file, {RegistryValueCount} registry";
}
public sealed record CleanerRestoreResult(int Restored, IReadOnlyList<string> Errors);
