using System.Globalization;
using Microsoft.UI.Xaml;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps;

namespace WinOptimizer.UI.Features.Dashboard;

/// <summary>Indexes installed translations on demand and resolves suggestions in the current UI language.</summary>
internal sealed class ProgramSearchService(ILocalizationService localization, ICompatibleSettingsRegistry settings,
    IApplicationModeService mode)
{
    private const int ResultLimit = 12;
    private const CompareOptions MatchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace |
        CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;
    private static readonly CompareInfo Comparer = CultureInfo.InvariantCulture.CompareInfo;
    private readonly object _gate = new();
    private Task<IndexedEntry[]>? _index;
    private bool _includedSettings;

    public async Task<IReadOnlyList<ProgramSearchResult>> FindAsync(string query, CancellationToken token)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];

        var index = await GetIndexAsync().WaitAsync(token).ConfigureAwait(false);
        return await Task.Run(() => Find(index, query.Trim(), terms, token), token).ConfigureAwait(false);
    }

    /// <summary>Uses the current mode and compatibility snapshot for both suggestions and activation.</summary>
    internal bool CanOpen(ProgramSearchEntry entry) =>
        (!entry.NormalModeOnly || mode.CurrentMode == WinOptimizerMode.Normal) &&
        (entry.Parameter is not SoftwareAppsTab.External || mode.CurrentMode != WinOptimizerMode.Builder ||
            mode.CurrentBuilderTarget != BuilderTarget.Autounattend) &&
        (entry.SettingId is null || settings.IsInitialized && settings.GetById(entry.SettingId) is not null);

    private Task<IndexedEntry[]> GetIndexAsync()
    {
        lock (_gate)
        {
            var includeSettings = settings.IsInitialized;
            if (_index is null || _index.IsFaulted || _includedSettings != includeSettings)
            {
                _includedSettings = includeSettings;
                _index = Task.Run(() => BuildIndex(includeSettings));
            }
            return _index;
        }
    }

    private IndexedEntry[] BuildIndex(bool includeSettings)
    {
        var entries = ProgramSearchCatalog.Create(settings, includeSettings);
        var keys = entries.SelectMany(entry => entry.Keywords.Append(entry.TitleKey).Append(entry.DescriptionKey))
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var translations = localization.ReadTranslations(keys);
        IEnumerable<string> Texts(string? key) => key is not null && translations.TryGetValue(key, out var values) ? values : [];

        return entries.Select(entry => new IndexedEntry(entry,
            Texts(entry.TitleKey).Append(entry.FallbackTitle).Where(value => !string.IsNullOrWhiteSpace(value)).OfType<string>().Distinct().ToArray(),
            Texts(entry.DescriptionKey).Concat(entry.Keywords.SelectMany(Texts)).Append(entry.FallbackDescription)
                .Where(value => !string.IsNullOrWhiteSpace(value)).OfType<string>().Distinct().ToArray())).ToArray();
    }

    /// <summary>Ranks titles above descriptions and requires all words to match a single translation.</summary>
    private IReadOnlyList<ProgramSearchResult> Find(IndexedEntry[] index, string query, string[] terms, CancellationToken token)
    {
        var matches = new List<(ProgramSearchResult Result, int Rank)>();
        foreach (var candidate in index)
        {
            token.ThrowIfCancellationRequested();
            var entry = candidate.Entry;
            var title = GetTitle(entry.TitleKey, entry.FallbackTitle);
            var rank = RankTitle(title, query, terms);
            foreach (var translation in candidate.Titles)
            {
                if (rank == 0) break;
                rank = Math.Min(rank, RankTitle(translation, query, terms));
            }
            if (rank == int.MaxValue && candidate.Details.Any(detail => ContainsTerms(detail, terms))) rank = 3;
            if (rank == int.MaxValue || !CanOpen(entry)) continue;

            var section = entry.SectionKey is { } sectionKey ? localization.GetString(sectionKey) : "";
            matches.Add((new ProgramSearchResult(title, section, entry), rank));
        }

        return matches.OrderBy(match => match.Rank).ThenBy(match => match.Result.Title.Length)
            .DistinctBy(match => (match.Result.Title, match.Result.Entry.PageType, match.Result.Entry.Parameter))
            .Take(ResultLimit).Select(match => match.Result).ToArray();
    }

    private string GetTitle(string key, string? fallback)
    {
        var title = localization.GetString(key);
        return fallback is not null && title == $"[{key}]" ? fallback : title;
    }

    private static int RankTitle(string text, string query, string[] terms)
    {
        if (Comparer.Compare(text, query, MatchOptions) == 0) return 0;
        if (Comparer.IsPrefix(text, query, MatchOptions)) return 1;
        return ContainsTerms(text, terms) ? 2 : int.MaxValue;
    }

    private static bool ContainsTerms(string text, string[] terms) =>
        terms.All(term => Comparer.IndexOf(text, term, MatchOptions) >= 0);

    private sealed record IndexedEntry(ProgramSearchEntry Entry, string[] Titles, string[] Details);
}

/// <summary>Displays the localized title and its section while retaining the selected route.</summary>
public sealed class ProgramSearchResult
{
    internal ProgramSearchResult(string title, string section, ProgramSearchEntry entry) => (Title, Section, Entry) = (title, section, entry);
    public string Title { get; }
    public string Section { get; }
    public Visibility SectionVisibility => Section.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    internal ProgramSearchEntry Entry { get; }
}
