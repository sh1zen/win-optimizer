using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Isolates Windows WLAN access from account checks and deterministic service tests.</summary>
internal interface ISavedWifiSource
{
    SavedWifiSnapshot Read(CancellationToken cancellationToken);
}

/// <summary>Runs explicit WLAN reads off the UI thread, preventing alternate-account profile disclosure.</summary>
internal sealed class SavedWifiService(ISavedWifiSource source, IInteractiveUserService user) : ISavedWifiService
{
    public Task<SavedWifiSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        if (user.IsOtsElevation)
            throw new UtilityOperationException("Saved Wi-Fi profiles require the signed-in user's account. Restart the app as that user; use that account's administrator permissions to reveal protected keys.");
        return Task.Run(() => source.Read(cancellationToken), cancellationToken);
    }
}
