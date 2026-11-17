using System;

namespace WinOptimizer.Core.Features.Common.Utils;

public static class SearchHelper
{
    public static bool MatchesSearchTerm(string searchTerm, params string[] searchableFields)
    {
        var query = Prepare(searchTerm);
        if (!query.HasTerms) return true;
        ArgumentNullException.ThrowIfNull(searchableFields);
        return query.Matches(searchableFields);
    }

    /// <summary>Parses text once for consumers that apply the same search to many rows.</summary>
    public static PreparedSearch Prepare(string? searchTerm) => string.IsNullOrWhiteSpace(searchTerm)
        ? default : new(searchTerm.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Matches every space-separated term against the supplied searchable fields.</summary>
    public readonly struct PreparedSearch
    {
        private readonly string[]? _terms;
        internal PreparedSearch(string[] terms) => _terms = terms;
        internal bool HasTerms => _terms is { Length: > 0 };

        public bool Matches(ReadOnlySpan<string?> fields)
        {
            if (_terms is null) return true;
            foreach (var term in _terms)
            {
                var matched = false;
                foreach (var field in fields)
                {
                    if (field?.ToLowerInvariant().Contains(term) != true) continue;
                    matched = true;
                    break;
                }
                if (!matched) return false;
            }
            return true;
        }
    }
}
