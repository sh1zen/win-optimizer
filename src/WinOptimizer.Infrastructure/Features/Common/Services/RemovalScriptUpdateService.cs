using System.Text.RegularExpressions;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Utilities;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>Refreshes the shared package removal script while preserving the saved selections.</summary>
public class RemovalScriptUpdateService(ILogService log, IFileSystemService files)
    : IRemovalScriptUpdateService
{
    private static readonly Regex ScriptVersion = new(@"Script Version:\s*(\d+\.\d+)", RegexOptions.Compiled);

    public async Task CheckAndUpdateScriptsAsync()
    {
        var path = ScriptPaths.PackageRemovalScript;
        await BloatRemovalService.PersistentPlanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!files.FileExists(path)) return;
            var content = files.ReadAllText(path);
            var installedVersion = ScriptVersion.Match(content).Groups[1].Value;
            if (installedVersion == BloatRemovalScriptGenerator.ScriptVersion) return;
            await BloatRemovalService.PublishPlanAsync(files, path, BloatRemovalScriptGenerator.UpdateScriptTemplate(content),
                replace: true, CancellationToken.None).ConfigureAwait(false);
            log.LogInformation("Windows package removal script updated.");
        }
        catch (Exception ex) { log.LogError("Failed to update the Windows package removal script.", ex); }
        finally { BloatRemovalService.PersistentPlanGate.Release(); }
    }
}
