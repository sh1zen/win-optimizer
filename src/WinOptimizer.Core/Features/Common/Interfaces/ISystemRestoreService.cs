namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>
/// Reads System Protection state and manages individual restore points through Windows APIs.
/// Shared by settings discovery, backup creation and the Restore page.
/// </summary>
public interface ISystemRestoreService
{
    /// <summary>Reads all available restore points without changing System Protection.</summary>
    Task<IReadOnlyList<Models.SystemRestorePoint>> GetRestorePointsAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes only the restore point with the supplied sequence number.</summary>
    Task<Models.OperationResult> DeleteRestorePointAsync(uint sequenceNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if System Protection is currently enabled for C: drive.
    ///
    /// Source of truth is the REG_MULTI_SZ at
    /// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients under the
    /// System Restore client GUID {09F7EDC5-294E-4180-AF6A-FB0E6A0E9513} — the
    /// same key sysdm.cpl and Enable-/Disable-ComputerRestore read/write.
    /// Updates synchronously with the toggle, so this method correctly reflects
    /// state immediately after Enable-ComputerRestore runs (before any restore
    /// point exists).
    ///
    /// HKLM\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore\DisableSR=1
    /// is a group-policy override that forces SR off system-wide regardless of
    /// the SPP entry list; honoured by this method.
    ///
    /// Returns false on any read error.
    /// </summary>
    bool IsEnabledForC();
}
