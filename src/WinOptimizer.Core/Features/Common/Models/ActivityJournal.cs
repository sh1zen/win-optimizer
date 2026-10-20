using WinOptimizer.Core.Features.Common.Enums;

namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>Stable subjects shared by stored diagnostics and the activity journal.</summary>
public enum LogTopic { General, Startup, SystemInformation, Software, Settings, Configuration, Cleaner, Drivers, Monitoring, WindowsTools, Updates }

/// <summary>The kinds of activity visible in a journal query.</summary>
public enum JournalFilter { All, Changes, Problems, Diagnostics }

/// <summary>A query applies across saved sessions, independently of their filenames.</summary>
public sealed record JournalQuery(LogTopic? Topic = null, JournalFilter Filter = JournalFilter.All, string Search = "");

/// <summary>Retains one original typed record and its technical provenance without flattening its fields.</summary>
public sealed class JournalRecord
{
    private JournalRecord(string fileName, AppLogEntry entry)
    { FileName = fileName; Diagnostic = entry; Topic = entry.Topic; }
    private JournalRecord(string fileName, ChangeHistoryEntry entry)
    {
        FileName = fileName; Change = entry;
        Topic = entry.Kind switch
        {
            HistoryEntryKind.AppInstalled or HistoryEntryKind.AppRemoved => LogTopic.Software,
            HistoryEntryKind.SettingChange or HistoryEntryKind.SettingAction => LogTopic.Settings,
            _ => LogTopic.General
        };
    }

    public string FileName { get; }
    public AppLogEntry? Diagnostic { get; }
    public ChangeHistoryEntry? Change { get; }
    public LogTopic Topic { get; }
    public DateTimeOffset? Timestamp => Diagnostic?.Timestamp ?? Change?.Timestamp;
    public bool IsProblem => Diagnostic?.Level is LogLevel.Error or LogLevel.Warning;
    public static JournalRecord From(string fileName, AppLogEntry entry) => new(fileName, entry);
    public static JournalRecord From(string fileName, ChangeHistoryEntry entry) => new(fileName, entry);

    /// <summary>Filters typed fields, including full exception details and original change values.</summary>
    public bool Matches(JournalQuery query)
    {
        if (query.Topic is { } topic && Topic != topic) return false;
        if (query.Filter == JournalFilter.Changes && Change is null ||
            query.Filter == JournalFilter.Diagnostics && Diagnostic is null ||
            query.Filter == JournalFilter.Problems && !IsProblem) return false;
        if (string.IsNullOrWhiteSpace(query.Search)) return true;
        var search = query.Search.Trim();
        return new[] { Diagnostic?.Message, Diagnostic?.Exception?.Type, Diagnostic?.Exception?.Message,
            Diagnostic?.Exception?.StackTrace, Diagnostic?.Exception?.InnerException,
            Change?.Name, Change?.Group, Change?.Before, Change?.After, Change?.Batch, Change?.ActionLabel }
            .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>A bounded merged page; notices expose incomplete or unreadable sources.</summary>
public sealed record JournalPage(IReadOnlyList<JournalRecord> Entries, bool HasMore, int UnreadableEntries,
    IReadOnlyList<string> Notices);

/// <summary>Assigns known message subjects when a writing workflow has no explicit topic.</summary>
public static class LogTopics
{
    private static readonly (LogTopic Topic, string[] Prefixes)[] MessagePrefixes =
    [
        (LogTopic.SystemInformation, ["OS:", "Architecture:", "Device Type:", "CPU:", "RAM:", "GPU:", ".NET Runtime:", "Elevation:", "Firmware:", "Secure Boot:", "TPM:", "Domain Joined:"]),
        (LogTopic.Startup, ["==== win-optimizer", "win-optimizer application starting", "[Preloader]", "[Startup", "[HardwareCompatibility", "Pre-filtering completed", "Loaded settings", "Registered settings"]),
        (LogTopic.Software, ["[DISM-Detect]", "[DismSession]", "[WindowsAppsViewModel]", "[SoftwareAppsViewModel]", "[ExternalAppsViewModel]", "[winget", "WinGet", "AppX detection", "AppIconResolver:", "AppxIconSource:", "IconManifest:", "Installed (", "Detection complete:"]),
        (LogTopic.Configuration, ["[Config", "Configuration ", "Creating user backup", "Applying configuration"]),
        (LogTopic.Settings, ["[BulkSettings]", "[Setting", "Applied setting:", "Failed to apply setting"]),
        (LogTopic.Cleaner, ["[Cleaner", "Cleaner:", "Cleaner scan", "Cleaner confirmation:", "[RegistryHive"]),
        (LogTopic.Drivers, ["[Driver"]),
        (LogTopic.Monitoring, ["[SysMonitor", "[SystemMonitor", "[ProcessMonitor"]),
        (LogTopic.WindowsTools, ["[Connection", "[Hosts", "[Battery", "[Autorun", "[WindowsServicing", "[EventLog"]),
        (LogTopic.Updates, ["Background application update", "Background script update", "Automatic update", "Current version:", "Checking for updates"])
    ];

    public static LogTopic FromMessage(string message)
    {
        var text = message.TrimStart();
        foreach (var (topic, prefixes) in MessagePrefixes)
            if (prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return topic;
        return LogTopic.General;
    }
}
