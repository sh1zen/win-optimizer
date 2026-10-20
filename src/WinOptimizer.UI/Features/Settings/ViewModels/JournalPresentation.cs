using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.UI.Features.Settings.ViewModels;

/// <summary>A stable localized filter option; relabeling does not reset selection.</summary>
public sealed class JournalOption(string key, ILocalizationService localization) : ObservableObject
{
    public string Title => localization.GetString(key);
    public void Refresh() => OnPropertyChanged(nameof(Title));
}

/// <summary>A subject summary referencing the exact rows displayed and searched by the journal.</summary>
public sealed partial class LogTopicGroup(LogTopic topic, IReadOnlyList<LogDisplayEntry> items,
    ILocalizationService localization) : ObservableObject
{
    public LogTopic Topic { get; } = topic;
    public IReadOnlyList<LogDisplayEntry> Items { get; } = items;
    private readonly int _problems = items.Count(item => item.Record.IsProblem);
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    public string Title => localization.GetString("Logs_Topic_" + Topic);
    public string Summary => localization.GetString("Logs_GroupCount", Items.Count);
    public bool HasProblems => _problems > 0;
    public string Problems => localization.GetString("Logs_GroupProblems", _problems);
    public Icon Icon => Topic switch
    {
        LogTopic.Software => Icon.Apps, LogTopic.Settings => Icon.Settings, LogTopic.Startup => Icon.Power,
        LogTopic.SystemInformation => Icon.Info, LogTopic.Updates => Icon.ArrowSync,
        LogTopic.Configuration => Icon.Document, LogTopic.Monitoring => Icon.DataUsage,
        _ => Icon.DocumentText
    };
    public void RefreshLabels() => OnPropertyChanged(string.Empty);
}

/// <summary>Formats typed events with compact summaries and separately expandable technical details.</summary>
public sealed class LogDisplayEntry(JournalRecord record, ILocalizationService localization) : ObservableObject
{
    public JournalRecord Record { get; } = record;
    public string Timestamp => Record.Timestamp?.ToLocalTime().ToString("g") ?? localization.GetString("Logs_UnknownDate");
    public string Message => Record.Diagnostic?.Message ?? Record.Change?.Name ?? "";
    public string Category => localization.GetString(Record.Change is { } change
        ? "Logs_Kind_" + change.Kind : "Logs_Level_" + (Record.Diagnostic?.Level?.ToString() ?? "Unknown"));
    public string Group => Record.Change?.Group ?? "";
    public bool HasGroup => Group.Length > 0;
    public string Batch => Record.Change?.Batch ?? "";
    public bool HasBatch => Batch.Length > 0;
    public bool HasValueChange => Record.Change?.Kind == HistoryEntryKind.SettingChange;
    public string BeforeLabel => localization.GetString("Logs_Before");
    public string AfterLabel => localization.GetString("Logs_After");
    public string Before => Value(Record.Change?.Before);
    public string After => Value(Record.Change?.After);
    public string DetailsLabel => localization.GetString("Logs_Details");
    public string Details => string.Join(Environment.NewLine, new[]
    {
        localization.GetString("Logs_RecordSource") + ": " + Record.FileName,
        Record.Change?.BatchId is { } id ? localization.GetString("Logs_Operation") + ": " + id : "",
        Record.Diagnostic?.Exception is { } error ? string.Join(Environment.NewLine,
            new[] { error.Type + ": " + error.Message, error.StackTrace, error.InnerException }.Where(text => !string.IsNullOrEmpty(text))) : ""
    }.Where(text => text.Length > 0));
    public void RefreshLabels() => OnPropertyChanged(string.Empty);
    private string Value(string? value) => string.IsNullOrEmpty(value) ? localization.GetString("Logs_EmptyValue") : value;
}
