using System.ComponentModel;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Uses PnPUtil for exact removals without force, device-tree removal or automatic reboot.</summary>
public sealed partial class WindowsDriverRemovalBackend(IProcessExecutor processes, TimeMachineStore? timeMachine = null) : IDriverRemovalBackend
{
    public async Task<DriverRemovalOutcome> RemoveAsync(DriverRemovalTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string arguments;
        if (target.Kind == DriverRemovalKind.Package && Regex.IsMatch(target.Id, @"\Aoem[0-9]+\.inf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            arguments = $"/delete-driver {target.Id}";
        else if (target.Kind == DriverRemovalKind.Device && !string.IsNullOrWhiteSpace(target.Id) &&
            target.Id.Length <= 200 && Regex.IsMatch(target.Id, @"\A[^\\]+\\[^\\]+\\[^\\]+\z") &&
            !target.Id.Any(character => char.IsControl(character) || character is '"' or '*' or '?'))
            arguments = $"/remove-device \"{target.Id}\"";
        else throw new DriverScanException("Windows driver removal requires a valid published INF name or exact device instance ID.");
        try
        {
            await _driverGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var undo = target.Kind == DriverRemovalKind.Package && timeMachine is not null
                    ? await ExportForRemovalAsync(target.Id, ct).ConfigureAwait(false) : null;
                // Let a native write finish; cancellation stops the next item.
                var result = await processes.ExecuteAsync(PnpUtil, arguments, CancellationToken.None).ConfigureAwait(false);
                var success = result.ExitCode is 0 or 3010;
                if (result.ExitCode == 3010)
                    timeMachine?.Record(LogTopic.Drivers, target.Id, "Driver removal pending restart", undo);
                else if (success)
                    timeMachine?.Record(LogTopic.Drivers, target.Id, target.Kind == DriverRemovalKind.Package
                        ? "Driver package removed" : "Disconnected device removed", undo);
                else if (undo is not null && DriverIsStaged(DriverEvidence(undo).InfHash))
                    timeMachine!.DiscardUndo(undo.Id);
                else if (undo is not null)
                    timeMachine!.Record(LogTopic.Drivers, target.Id, "Driver removal requires review", undo);
                return new(target, success, result.ExitCode == 3010, success ? "" :
                    $"PnPUtil ({result.ExitCode}): {result.StandardOutput.Trim()} {result.StandardError.Trim()}".Trim());
            }
            finally { _driverGate.Release(); }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        { throw new DriverScanException($"Unable to remove {target.Id}: {ex.Message}", ex); }
    }
}
