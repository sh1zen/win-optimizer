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
        var (attempted, ids) = await GetInstalledPackageIdsViaCom(cancellationToken).ConfigureAwait(false);
        if (ids is not null) return ids;
        cancellationToken.ThrowIfCancellationRequested();
        if (attempted) _logService.LogInformation("COM detection failed/timed out, falling back to CLI");

        _logService.LogInformation("COM not available, falling back to CLI for installed package detection");
        return await GetInstalledPackageIdsViaCli(cancellationToken).ConfigureAwait(false);
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
            return (true, null);
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
        var cacheDir = AppDataPaths.WinGetCache;
        _fileSystemService.CreateDirectory(cacheDir);
        var exportPath = _fileSystemService.CombinePath(cacheDir, $"winget-packages-{Guid.NewGuid():N}.json");

        const int maxRetries = 3;
        const int retryDelayMs = 2000;
        const int timeoutMs = 10_000;

        try
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (_fileSystemService.FileExists(exportPath))
                        _fileSystemService.DeleteFile(exportPath);

                    var arguments = $"export -o \"{exportPath}\" --accept-source-agreements --nowarn --disable-interactivity";
                    _logService.LogInformation($"[winget-bundled] Running: winget {arguments} (attempt {attempt}/{maxRetries})");
                    var result = await _runExport(arguments, timeoutMs, cancellationToken).ConfigureAwait(false);
                    if (result.ExitCode == WinGetExitCodes.FailedToOpenAllSources)
                        break;
                    if (result.ExitCode != 0)
                        throw new IOException($"WinGet export exited with code 0x{result.ExitCode:X8}.");
                    if (!_fileSystemService.FileExists(exportPath))
                        throw new IOException("WinGet did not write an inventory export.");

                    var jsonBytes = await _fileSystemService.ReadAllBytesAsync(exportPath, cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(jsonBytes);
                    var installedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var source in doc.RootElement.GetProperty("Sources").EnumerateArray())
                        foreach (var package in source.GetProperty("Packages").EnumerateArray())
                            if (package.GetProperty("PackageIdentifier").GetString() is { Length: > 0 } id)
                                installedPackageIds.Add(id);

                    _logService.LogInformation($"WinGet CLI (export): Found {installedPackageIds.Count} installed packages");
                    return installedPackageIds;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logService.LogWarning($"WinGet export attempt {attempt}/{maxRetries} failed: {ex.Message}");
                    if (attempt == maxRetries) throw new IOException("WinGet could not export the installed package inventory.", ex);
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }
            throw new IOException("WinGet could not open its package sources.");
        }
        finally
        {
            try
            {
                if (_fileSystemService.FileExists(exportPath))
                    _fileSystemService.DeleteFile(exportPath);
            }
            catch (Exception ex) { _logService.LogDebug($"Best-effort export file cleanup failed: {ex.Message}"); }
        }
    }
}
