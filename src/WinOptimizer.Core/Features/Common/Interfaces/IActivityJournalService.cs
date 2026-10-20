using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>Opens an explicit query across saved diagnostic and change-history records.</summary>
public interface IActivityJournalService
{
    Task<IActivityJournalSession> OpenAsync(JournalQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Owns bounded source buffers and cursors for one query; no filesystem handles remain open between pages.</summary>
public interface IActivityJournalSession
{
    Task<JournalPage> ReadNextAsync(CancellationToken cancellationToken = default);
}
