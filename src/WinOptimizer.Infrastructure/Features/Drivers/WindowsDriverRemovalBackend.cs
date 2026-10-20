using System.ComponentModel;
using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;

namespace WinOptimizer.Infrastructure.Features.Drivers;

/// <summary>Uses PnPUtil for exact removals without force, device-tree removal or automatic reboot.</summary>
public sealed class WindowsDriverRemovalBackend(IProcessExecutor processes) : IDriverRemovalBackend
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
            // Do not kill PnPUtil midway through a native change; cancellation stops the next item.
            var result = await processes.ExecuteAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), arguments,
                CancellationToken.None).ConfigureAwait(false);
            return new(target, result.ExitCode is 0 or 3010, result.ExitCode == 3010,
                result.ExitCode is 0 or 3010 ? "" : $"PnPUtil ({result.ExitCode}): {result.StandardOutput.Trim()} {result.StandardError.Trim()}".Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        { throw new DriverScanException($"Unable to remove {target.Id}: {ex.Message}", ex); }
    }
}
