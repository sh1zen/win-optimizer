using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Enums;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

public class ExternalAppUninstallService(
    IWinGetPackageInstaller winGetPackageInstaller,
    IChocolateyService chocolateyService,
    ILogService logService,
    IUninstallCommandSource uninstallCommands,
    ITaskProgressService taskProgressService,
    IProcessExecutor processExecutor,
    IUninstallResidueReview review,
    IAppxDeployment deployment,
    IInteractiveUserService user) : IExternalAppUninstallService
{
    /// <summary>Runs the registered interactive uninstaller, or asks before using automatic removal.</summary>
    public async Task<OperationResult<bool>> UninstallAsync(
        ItemDefinition item,
        IProgress<TaskProgressDetail>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (found, command) = await uninstallCommands.GetAsync(item).ConfigureAwait(false);
            if (found && !string.IsNullOrWhiteSpace(command))
                return await UninstallViaRegistryAsync(item, command, cancellationToken).ConfigureAwait(false);

            var method = await DetermineAutomaticMethodAsync(item).ConfigureAwait(false);
            if (method == UninstallMethod.None)
                return OperationResult<bool>.Failed($"No uninstall method available for {item.Name}");

            cancellationToken.ThrowIfCancellationRequested();
            var consent = await review.ConfirmAutomaticUninstallAsync(item, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!consent)
                return OperationResult<bool>.Cancelled("Automatic uninstall declined by user");

            return method switch
            {
                UninstallMethod.WinGet => await UninstallViaWinGetAsync(item, cancellationToken).ConfigureAwait(false),
                UninstallMethod.Chocolatey => await UninstallViaChocolateyAsync(item, cancellationToken).ConfigureAwait(false),
                UninstallMethod.AppX => await UninstallViaAppxAsync(item, cancellationToken).ConfigureAwait(false),
                UninstallMethod.FileSystem => await UninstallViaFileSystemAsync(item, cancellationToken).ConfigureAwait(false),
                _ => OperationResult<bool>.Failed($"No uninstall method available for {item.Name}")
            };
        }
        catch (OperationCanceledException)
        {
            return OperationResult<bool>.Cancelled("Uninstall cancelled");
        }
    }

    /// <summary>Chooses an automatic provider only after native command discovery found none.</summary>
    private async Task<UninstallMethod> DetermineAutomaticMethodAsync(ItemDefinition item)
    {
        // Use the detection source to pick the most appropriate uninstall method.
        // Each source owns the path that knows how to undo what it detected — WinGet
        // can't see Chocolatey-installed packages, can't see Store-UI-installed AppX
        // packages, etc. Dispatching on DetectedVia keeps detection and uninstall
        // symmetric.
        switch (item.DetectedVia)
        {
            case DetectionSource.Chocolatey when !string.IsNullOrEmpty(item.ChocoPackageId):
                return UninstallMethod.Chocolatey;

            case DetectionSource.AppX when item.AppxPackageName?.Length > 0:
                return UninstallMethod.AppX;

            case DetectionSource.FileSystem when item.DetectionPaths?.Length > 0:
                return UninstallMethod.FileSystem;
        }

        // Catalogue identifiers supply automatic providers for other detection sources.
        if (!string.IsNullOrEmpty(item.MsStoreId) || (item.WinGetPackageId != null && item.WinGetPackageId.Any()))
            return UninstallMethod.WinGet;

        if (!string.IsNullOrEmpty(item.ChocoPackageId))
        {
            var chocoIds = await chocolateyService.GetInstalledPackageIdsAsync().ConfigureAwait(false);
            if (chocoIds.Contains(item.ChocoPackageId))
                return UninstallMethod.Chocolatey;
        }

        return UninstallMethod.None;
    }

    private async Task<OperationResult<bool>> UninstallViaWinGetAsync(ItemDefinition item, CancellationToken cancellationToken)
    {
        try
        {
            string? packageId;
            string? source;

            if (item.WinGetPackageId != null && item.WinGetPackageId.Any())
            {
                packageId = item.WinGetPackageId[0];
                source = "winget";
            }
            else if (!string.IsNullOrEmpty(item.MsStoreId))
            {
                packageId = item.MsStoreId;
                source = "msstore";
            }
            else
            {
                packageId = null;
                source = null;
            }

            if (string.IsNullOrEmpty(packageId))
            {
                return OperationResult<bool>.Failed($"No WinGet package ID for {item.Name}");
            }

            var success = await winGetPackageInstaller.UninstallPackageAsync(packageId, source, item.Name, cancellationToken).ConfigureAwait(false);

            if (!success)
            {
                // Try the remaining automatic providers covered by the user's consent.
                // AppX defends against detection mis-classification: if the item carries
                // an AppxPackageName but DetectedVia routed to WinGet (e.g. a Store-UI
                // install that winget surfaces as MSIX\... and fails to uninstall by ID),
                // the AppX path can still find and remove the actual installed package.
                if (!string.IsNullOrEmpty(item.ChocoPackageId))
                {
                    logService.LogWarning($"WinGet uninstall failed for {item.Name}, attempting Chocolatey fallback");
                    taskProgressService.UpdateProgress(0, $"WinGet uninstall failed for {item.Name}, trying Chocolatey...");
                    var chocoResult = await UninstallViaChocolateyAsync(item, cancellationToken).ConfigureAwait(false);
                    if (chocoResult.Success)
                        return chocoResult;
                }

                if (item.AppxPackageName?.Length > 0)
                {
                    logService.LogWarning($"WinGet uninstall failed for {item.Name}, attempting AppX fallback");
                    taskProgressService.UpdateProgress(0, $"Trying AppX fallback for {item.Name}...");
                    var appxResult = await UninstallViaAppxAsync(item, cancellationToken).ConfigureAwait(false);
                    if (appxResult.Success)
                        return appxResult;
                }

                return OperationResult<bool>.Failed($"Automatic uninstall failed for {item.Name}");
            }

            await CleanupStaleChocoRecordAsync(item, cancellationToken).ConfigureAwait(false);
            return OperationResult<bool>.Succeeded(true);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<bool>.Cancelled("Uninstall cancelled");
        }
        catch (Exception ex)
        {
            logService.LogError($"WinGet uninstall error for {item.Name}: {ex.Message}", ex);
            return OperationResult<bool>.Failed($"Automatic uninstall failed: {ex.Message}");
        }
    }

    private async Task<OperationResult<bool>> UninstallViaChocolateyAsync(ItemDefinition item, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(item.ChocoPackageId))
                return OperationResult<bool>.Failed($"No Chocolatey package ID for {item.Name}");

            if (!await chocolateyService.IsChocolateyInstalledAsync(cancellationToken).ConfigureAwait(false))
            {
                logService.LogWarning($"Chocolatey not available for uninstalling {item.Name}");
                return OperationResult<bool>.Failed($"Chocolatey is not installed");
            }

            var success = await chocolateyService.UninstallPackageAsync(item.ChocoPackageId, item.Name, cancellationToken).ConfigureAwait(false);

            if (!success)
            {
                return OperationResult<bool>.Failed($"Chocolatey uninstall failed for {item.Name}");
            }

            return OperationResult<bool>.Succeeded(true);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<bool>.Cancelled("Uninstall cancelled");
        }
        catch (Exception ex)
        {
            logService.LogError($"Chocolatey uninstall error for {item.Name}: {ex.Message}", ex);
            return OperationResult<bool>.Failed($"Chocolatey uninstall failed: {ex.Message}");
        }
    }

    /// <summary>Waits for the native UI to exit before reporting its actual outcome.</summary>
    private async Task<OperationResult<bool>> UninstallViaRegistryAsync(ItemDefinition item, string uninstallString, CancellationToken cancellationToken)
    {
        try
        {
            logService.LogInformation($"Uninstalling {item.Name} via registry: {uninstallString}");

            var (fileName, arguments) = ParseUninstallString(uninstallString);

            cancellationToken.ThrowIfCancellationRequested();
            // Cancellation stops the queue after this UI exits; it must not release cleanup while the uninstaller runs.
            var exitCode = await processExecutor.ShellExecuteAsync(fileName, arguments, waitForExit: true, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (exitCode is 3010 or 1641)
                return OperationResult<bool>.DeferredSuccess(true, "The uninstaller requires a restart; inspect leftovers after restarting Windows.");
            if (exitCode != 0)
                return OperationResult<bool>.Failed($"The uninstaller for {item.Name} did not confirm success (exit code: {exitCode?.ToString() ?? "unavailable"}).");

            logService.LogInformation($"Uninstall process for {item.Name} completed successfully");
            taskProgressService.UpdateProgress(100, $"Uninstall process for {item.Name} completed successfully");

            await CleanupStaleChocoRecordAsync(item, cancellationToken).ConfigureAwait(false);
            return OperationResult<bool>.Succeeded(true);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<bool>.Cancelled("Uninstall cancelled");
        }
        catch (Exception ex)
        {
            logService.LogError($"Registry uninstall error for {item.Name}: {ex.Message}", ex);
            return OperationResult<bool>.Failed($"Uninstall failed: {ex.Message}");
        }
    }

    // Best-effort: when WinGet or Registry has just removed the actual app, clear any
    // Chocolatey package record left behind (Chocolatey doesn't notice out-of-band
    // uninstalls, so its lib folder keeps reporting the package and the next detection
    // pass surfaces a ghost). No-op when the item wasn't tracked by Choco or the record
    // is already gone. Never fails the parent uninstall — this is hygiene, not correctness.
    private async Task CleanupStaleChocoRecordAsync(ItemDefinition item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.ChocoPackageId))
            return;

        try
        {
            if (!await chocolateyService.IsChocolateyInstalledAsync(cancellationToken).ConfigureAwait(false))
                return;

            await chocolateyService.CleanupStalePackageRecordAsync(item.ChocoPackageId, item.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logService.LogWarning($"Chocolatey ghost-record cleanup for {item.Name} errored: {ex.Message}");
        }
    }

    /// <summary>Removes matching packages only from the same interactive account used for discovery.</summary>
    private async Task<OperationResult<bool>> UninstallViaAppxAsync(ItemDefinition item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.AppxPackageName == null || item.AppxPackageName.Length == 0)
                return OperationResult<bool>.Failed($"No AppxPackageName configured for {item.Name}");

            cancellationToken.ThrowIfCancellationRequested();

            var accountSid = deployment.InteractiveAccountSid;
            var packages = (await deployment.GetPackagesAsync(accountSid, cancellationToken).ConfigureAwait(false))
                .Where(p => item.AppxPackageName!.Any(name =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (packages.Count == 0)
            {
                logService.LogWarning($"No installed AppX package found for {item.Name} (looked for: {string.Join(", ", item.AppxPackageName!)})");
                return OperationResult<bool>.Failed($"No installed AppX package found for {item.Name}");
            }

            logService.LogInformation($"AppX uninstall for {item.Name}: removing {packages.Count} package(s)");
            taskProgressService.UpdateProgress(10, $"Removing {item.Name}...");

            int removed = 0;
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullName = package.FullName;
                logService.LogInformation($"AppX: removing {fullName}");

                await deployment.RemovePackageAsync(fullName, accountSid, progress =>
                {
                    // DeploymentProgress.percentage is 0–100 but the registry/winget
                    // paths reserve 100% for the completion log line, so cap reports
                    // at 95% to keep the terminal output visually consistent. Also
                    // skip the WinRT API's initial 0% callback — we already reported
                    // 10% before calling RemovePackageAsync, and the bar mustn't go
                    // backwards.
                    var pct = Math.Min(95, progress);
                    if (pct <= 10) return;
                    taskProgressService.UpdateProgress(pct, $"Removing {item.Name}... {pct}%");
                }, cancellationToken).ConfigureAwait(false);
                removed++;
            }

            logService.LogInformation($"AppX uninstall for {item.Name} completed ({removed} package(s) removed)");
            taskProgressService.UpdateProgress(100, $"Uninstall of {item.Name} completed successfully");

            await CleanupStaleChocoRecordAsync(item, cancellationToken).ConfigureAwait(false);
            return OperationResult<bool>.Succeeded(true);
        }
        catch (OperationCanceledException)
        {
            return OperationResult<bool>.Cancelled("Uninstall cancelled");
        }
        catch (Exception ex)
        {
            logService.LogError($"AppX uninstall error for {item.Name}: {ex.Message}", ex);
            return OperationResult<bool>.Failed($"AppX uninstall failed: {ex.Message}");
        }
    }

    private Task<OperationResult<bool>> UninstallViaFileSystemAsync(ItemDefinition item, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.DetectionPaths == null || item.DetectionPaths.Length == 0)
                return Task.FromResult(OperationResult<bool>.Failed($"No detection paths configured for {item.Name}"));

            int deletedCount = 0;
            // Resolve every account-bound path before the first deletion.
            var paths = item.DetectionPaths.Select(path => AppPaths.ResolveDetectionPath(path, user)).ToArray();
            foreach (var expandedPath in paths)
            {
                if (Directory.Exists(expandedPath))
                {
                    logService.LogInformation($"Deleting directory for {item.Name}: {expandedPath}");
                    Directory.Delete(expandedPath, recursive: true);
                    deletedCount++;
                }
                else if (File.Exists(expandedPath))
                {
                    logService.LogInformation($"Deleting file for {item.Name}: {expandedPath}");
                    File.Delete(expandedPath);
                    deletedCount++;
                }
            }

            if (deletedCount > 0)
            {
                logService.LogInformation($"FileSystem uninstall for {item.Name} completed ({deletedCount} path(s) removed)");
                taskProgressService.UpdateProgress(100, $"Uninstall of {item.Name} completed successfully");
                return Task.FromResult(OperationResult<bool>.Succeeded(true));
            }

            logService.LogWarning($"No detection paths found on disk for {item.Name}");
            return Task.FromResult(OperationResult<bool>.Failed($"No files found to remove for {item.Name}"));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(OperationResult<bool>.Cancelled("Uninstall cancelled"));
        }
        catch (Exception ex)
        {
            logService.LogError($"FileSystem uninstall error for {item.Name}: {ex.Message}", ex);
            return Task.FromResult(OperationResult<bool>.Failed($"Uninstall failed: {ex.Message}"));
        }
    }

    private static readonly Regex MsiInstallToUninstallRegex = new(@"(?<!\S)/I(?=\s*\{)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SilentFlagsRegex = new(@"""[^""]*""|(?<!\S)(?:/S|/SILENT|/VERYSILENT|/quiet|/passive|/q[nbrf]?[+!\-]*|--silent|--quiet)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Preserves native command arguments while enabling its UI and turning MSI maintenance into removal.</summary>
    private (string FileName, string Arguments) ParseUninstallString(string uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
            return (string.Empty, string.Empty);

        uninstallString = uninstallString.Trim();

        if (uninstallString.StartsWith("\""))
        {
            var endQuoteIndex = uninstallString.IndexOf("\"", 1);
            if (endQuoteIndex > 0)
            {
                var fileName = uninstallString.Substring(1, endQuoteIndex - 1);
                var arguments = uninstallString.Length > endQuoteIndex + 1
                    ? uninstallString.Substring(endQuoteIndex + 1).Trim()
                    : string.Empty;

                arguments = InteractiveArguments(fileName, arguments);
                logService.LogInformation($"Parsed command - FileName: {fileName}, Arguments: {arguments}");
                return (fileName, arguments);
            }
        }

        // Registered executable paths may contain spaces even when their publisher omitted quotes.
        var executable = Regex.Match(uninstallString, @"^(.+?\.exe)(?=\s|$)", RegexOptions.IgnoreCase);
        var spaceIndex = executable.Success ? executable.Length : uninstallString.IndexOf(' ');
        if (spaceIndex > 0)
        {
            var fileName = uninstallString.Substring(0, spaceIndex);
            var arguments = uninstallString.Substring(spaceIndex).Trim();
            arguments = InteractiveArguments(fileName, arguments);
            logService.LogInformation($"Parsed command - FileName: {fileName}, Arguments: {arguments}");
            return (fileName, arguments);
        }

        var finalArgs = InteractiveArguments(uninstallString, string.Empty);
        logService.LogInformation($"Parsed command - FileName: {uninstallString}, Arguments: {finalArgs}");
        return (uninstallString, finalArgs);
    }

    private static string InteractiveArguments(string fileName, string existingArgs)
    {
        var arguments = SilentFlagsRegex.Replace(existingArgs, match => match.Value.StartsWith('"') ? match.Value : string.Empty).Trim();
        return Path.GetFileNameWithoutExtension(fileName).Equals("msiexec", StringComparison.OrdinalIgnoreCase)
            ? MsiInstallToUninstallRegex.Replace(arguments, "/X") : arguments;
    }
}
