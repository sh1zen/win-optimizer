using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Runs Windows' shared disk analysis and media-aware optimization commands.</summary>
internal static class VolumeOptimization
{
    public static Task<ProcessExecutionResult> RunAsync(IProcessExecutor processes, string root, bool analyzeOnly, CancellationToken ct)
    {
        DiskPaths.Drive(root, fixedOnly: true);
        var options = analyzeOnly ? "/A /V" : "/O /U /V";
        return processes.ExecuteAsync(Path.Combine(Environment.SystemDirectory, "defrag.exe"), $"{root[..2]} {options}", ct);
    }
}
