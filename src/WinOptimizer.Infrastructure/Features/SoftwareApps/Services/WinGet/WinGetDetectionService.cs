using WinOptimizer.Core.Features.Common.Constants;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Management.Deployment;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet.Utilities;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services.WinGet;

/// <summary>
/// Handles WinGet package detection — installed package enumeration and installer type lookup.
/// Uses COM API with CLI fallback.
/// </summary>
public class WinGetDetectionService : IWinGetDetectionService
{
    private readonly WinGetComSession _comSession;
    private readonly ILogService _logService;
    private readonly IInteractiveUserService _interactiveUserService;
    private readonly IFileSystemService _fileSystemService;

    private readonly Func<(bool Attempted, HashSet<string>? Ids)> _readComInventory;
    private readonly Func<string, int, CancellationToken, Task<WinGetCliRunner.WinGetCliResult>> _runExport;
    private readonly TimeProvider _clock = TimeProvider.System;

    private const int ComOperationTimeoutSeconds = 15;

    public WinGetDetectionService(
        WinGetComSession comSession,
        ILogService logService,
        IInteractiveUserService interactiveUserService,
        IFileSystemService fileSystemService)
    {
        _comSession = comSession;
        _logService = logService;
        _interactiveUserService = interactiveUserService;
        _fileSystemService = fileSystemService;
        _readComInventory = ReadComInventory;
        _runExport = RunExportAsync;
    }

    internal WinGetDetectionService(WinGetComSession comSession, ILogService logService,
        IInteractiveUserService interactiveUserService, IFileSystemService fileSystemService,
        Func<(bool Attempted, HashSet<string>? Ids)>? readComInventory,
        Func<string, int, CancellationToken, Task<WinGetCliRunner.WinGetCliResult>> runExport,
        TimeProvider clock) : this(comSession, logService, interactiveUserService, fileSystemService)
    {
        _readComInventory = readComInventory ?? _readComInventory;
        _runExport = runExport;
        _clock = clock;
    }

    public async Task<HashSet<string>> GetInstalledPackageIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (attempted, ids) = await GetInstalledPackageIdsViaCom(cancellationToken).ConfigureAwait(false);
            if (ids is not null) return ids;
            if (attempted) _logService.LogInformation("COM detection failed/timed out, falling back to CLI");

            _logService.LogInformation("COM not available, falling back to CLI for installed package detection");
            return await GetInstalledPackageIdsViaCli(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error in GetInstalledPackageIdsAsync: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Bounds activation, connection and enumeration together so a blocked COM call cannot delay the CLI fallback indefinitely.</summary>
    private async Task<(bool Attempted, HashSet<string>? Ids)> GetInstalledPackageIdsViaCom(CancellationToken cancellationToken)
    {
        var work = Task.Run(_readComInventory, cancellationToken);
        try
        {
            return await work.WaitAsync(TimeSpan.FromSeconds(ComOperationTimeoutSeconds), _clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLateComFault(work);
            _logService.LogWarning($"COM package enumeration hard timeout after {ComOperationTimeoutSeconds}s — activation or inventory is unresponsive");
            return (true, null);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) ObserveLateComFault(work);
            _logService.LogError($"Error getting installed packages via COM API: {ex.Message}");
            return (true, null);
        }
    }

    /// <summary>Observes native work that can outlive the caller's deadline.</summary>
    private static void ObserveLateComFault(Task work) => _ = work.ContinueWith(task => { _ = task.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private (bool Attempted, HashSet<string>? Ids) ReadComInventory()
    {
        if (!_comSession.EnsureComInitialized() || _comSession.PackageManager is not { } manager ||
            _comSession.Factory is not { } factory) return (false, null);

        var installedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compositeOptions = factory.CreateCreateCompositePackageCatalogOptions();
        foreach (var catalog in manager.GetPackageCatalogs())
        {
            compositeOptions.Catalogs.Add(catalog);
            _logService.LogInformation($"Added catalog: {catalog.Info.Name}");
        }

        if (compositeOptions.Catalogs.Count == 0)
        {
            _logService.LogWarning("No package catalogs available");
            return (true, installedPackageIds);
        }
        compositeOptions.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;

        var compositeCatalogRef = manager.CreateCompositePackageCatalog(compositeOptions);
        var connectResult = compositeCatalogRef.Connect();
        if (connectResult.Status != ConnectResultStatus.Ok)
        {
            _logService.LogWarning($"Failed to connect to composite catalog: {connectResult.Status} — falling back to CLI");
            return (true, null);
        }

        var findOptions = factory.CreateFindPackagesOptions();
        var filter = factory.CreatePackageMatchFilter();
        filter.Field = PackageMatchField.Id;
        filter.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
        filter.Value = "";
        findOptions.Filters.Add(filter);

        _logService.LogInformation("WinGet COM: calling FindPackages...");
        foreach (var match in connectResult.PackageCatalog.FindPackages(findOptions).Matches)
        {
            if (match.CatalogPackage?.Id is { Length: > 0 } packageId)
                installedPackageIds.Add(packageId);
        }

        _logService.LogInformation($"WinGet COM API: Found {installedPackageIds.Count} installed packages");
        if (installedPackageIds.Count > 0) return (true, installedPackageIds);

        _logService.LogWarning("COM returned 0 installed packages — possible database corruption, falling back to CLI");
        return (true, null);
    }

    private Task<WinGetCliRunner.WinGetCliResult> RunExportAsync(string arguments, int timeoutMs, CancellationToken ct) =>
        WinGetCliRunner.RunAsync(arguments, cancellationToken: ct, timeoutMs: timeoutMs,
            exePathOverride: WinGetCliRunner.GetBundledWinGetExePath(), interactiveUserService: _interactiveUserService);

    private async Task<HashSet<string>> GetInstalledPackageIdsViaCli(CancellationToken cancellationToken)
    {
        var installedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cacheDir = AppDataPaths.WinGetCache;
        _fileSystemService.CreateDirectory(cacheDir);
        var exportPath = _fileSystemService.CombinePath(cacheDir, $"winget-packages-{Guid.NewGuid():N}.json");

        const int maxRetries = 3;
        const int retryDelayMs = 2000;
        const int timeoutMs = 10_000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // Clean up any previous export file
                if (_fileSystemService.FileExists(exportPath))
                    _fileSystemService.DeleteFile(exportPath);

                var arguments = $"export -o \"{exportPath}\" --accept-source-agreements --nowarn --disable-interactivity";
                _logService?.LogInformation($"[winget-bundled] Running: winget {arguments} (attempt {attempt}/{maxRetries})");

                var result = await _runExport(arguments, timeoutMs, cancellationToken).ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    _logService?.LogWarning($"winget export failed with exit code 0x{result.ExitCode:X8} (attempt {attempt}/{maxRetries})");

                    // FailedToOpenAllSources = sources not initialized (no internet / no AppInstaller)
                    // No point retrying — the sources won't appear on their own
                    if (result.ExitCode == WinGetExitCodes.FailedToOpenAllSources)
                    {
                        _logService?.LogWarning("Sources not available — skipping retries");
                        return installedPackageIds;
                    }

                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    return installedPackageIds;
                }

                if (!_fileSystemService.FileExists(exportPath))
                {
                    _logService?.LogWarning($"winget export succeeded but file not found (attempt {attempt}/{maxRetries})");
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    return installedPackageIds;
                }

                // Parse JSON: root.Sources[].Packages[].PackageIdentifier
                var jsonBytes = await _fileSystemService.ReadAllBytesAsync(exportPath, cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(jsonBytes);

                if (doc.RootElement.TryGetProperty("Sources", out var sourcesElement))
                {
                    foreach (var source in sourcesElement.EnumerateArray())
                    {
                        if (source.TryGetProperty("Packages", out var packagesElement))
                        {
                            foreach (var package in packagesElement.EnumerateArray())
                            {
                                if (package.TryGetProperty("PackageIdentifier", out var idElement))
                                {
                                    var id = idElement.GetString();
                                    if (!string.IsNullOrEmpty(id))
                                        installedPackageIds.Add(id);
                                }
                            }
                        }
                    }
                }

                _logService?.LogInformation($"WinGet CLI (export): Found {installedPackageIds.Count} installed packages");
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService?.LogError($"Error getting installed packages via winget export (attempt {attempt}/{maxRetries}): {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Clean up export file
        try
        {
            if (_fileSystemService.FileExists(exportPath))
                _fileSystemService.DeleteFile(exportPath);
        }
        catch (Exception ex) { _logService?.LogDebug($"Best-effort export file cleanup failed: {ex.Message}"); }

        return installedPackageIds;
    }

    public async Task<string?> GetInstallerTypeAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            return null;

        try
        {
            // Try COM first
            if (_comSession.EnsureComInitialized())
            {
                var package = await FindPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
                if (package?.DefaultInstallVersion != null)
                {
                    var catalogInfo = package.DefaultInstallVersion.PackageCatalog?.Info;
                    if (catalogInfo != null)
                    {
                        _logService?.LogInformation($"Package {packageId} from catalog: {catalogInfo.Name}");
                    }
                }
            }

            // CLI fallback: parse "winget show" output for Installer Type
            var result = await WinGetCliRunner.RunAsync(
                $"show --id {packageId} --accept-source-agreements --disable-interactivity",
                cancellationToken: cancellationToken,
                timeoutMs: 60_000,
                interactiveUserService: _interactiveUserService).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                foreach (var rawLine in result.StandardOutput.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("Installer Type:", StringComparison.OrdinalIgnoreCase))
                    {
                        var installerType = line.Substring("Installer Type:".Length).Trim();
                        if (!string.IsNullOrEmpty(installerType))
                        {
                            _logService?.LogInformation($"Package {packageId} installer type: {installerType}");
                            return installerType;
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logService?.LogWarning($"Could not determine installer type for {packageId}: {ex.Message}");
            return null;
        }
    }

    private async Task<CatalogPackage?> FindPackageAsync(string packageId, CancellationToken cancellationToken)
    {
        if (!_comSession.EnsureComInitialized() || _comSession.PackageManager == null || _comSession.Factory == null)
            return null;

        return await Task.Run(() =>
        {
            try
            {
                var catalogs = _comSession.PackageManager.GetPackageCatalogs().ToArray();

                foreach (var catalogRef in catalogs)
                {
                    var connectResult = catalogRef.Connect();
                    if (connectResult.Status != ConnectResultStatus.Ok)
                        continue;

                    var findOptions = _comSession.Factory.CreateFindPackagesOptions();
                    var filter = _comSession.Factory.CreatePackageMatchFilter();
                    filter.Field = PackageMatchField.Id;
                    filter.Option = PackageFieldMatchOption.EqualsCaseInsensitive;
                    filter.Value = packageId;
                    findOptions.Filters.Add(filter);

                    var findResult = connectResult.PackageCatalog.FindPackages(findOptions);

                    var match = findResult.Matches.ToArray().FirstOrDefault();
                    if (match != null)
                    {
                        return match.CatalogPackage;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logService?.LogError($"Error finding package {packageId}: {ex.Message}");
                return null;
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
