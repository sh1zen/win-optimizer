namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Opens each selected profile once and releases its session when enumeration advances or stops.</summary>
internal static class CleanerAccounts
{
    public static IEnumerable<(string? Sid, ICleanerRegistrySession Session)> OpenSelected(ICleanerRegistrySessions sessions,
        string[]? selection, ICollection<string> notices, CancellationToken ct)
    {
        IEnumerable<string?> selected;
        if (selection is null) selected = new string?[] { null };
        else selected = selection.Distinct(StringComparer.Ordinal);
        foreach (var sid in selected)
        {
            ct.ThrowIfCancellationRequested();
            ICleanerRegistrySession session;
            try { session = sessions.Open(sid); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notices.Add($"{sid ?? "Interactive account"}: {ex.Message}"); continue; }
            using (session) { yield return (sid, session); }
        }
    }
}
