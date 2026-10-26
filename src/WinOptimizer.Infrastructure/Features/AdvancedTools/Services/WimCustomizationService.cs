using System;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.AdvancedTools.Services;

/// <summary>Adds local answer files and drivers to extracted Windows media.</summary>
public class WimCustomizationService : IWimCustomizationService
{
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogService _logService;
    private readonly ILocalizationService _localization;
    private readonly IDriverCategorizer _driverCategorizer;
    private readonly IDismProcessRunner _dismProcessRunner;

    public WimCustomizationService(
        IFileSystemService fileSystemService,
        ILogService logService,
        ILocalizationService localization,
        IDriverCategorizer driverCategorizer,
        IDismProcessRunner dismProcessRunner)
    {
        _fileSystemService = fileSystemService;
        _logService = logService;
        _localization = localization;
        _driverCategorizer = driverCategorizer;
        _dismProcessRunner = dismProcessRunner;
    }

    public async Task<bool> AddDriversAsync(
        string workingDirectory,
        string? driverSourcePath = null,
        IProgress<TaskProgressDetail>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string sourceDirectory;

            if (string.IsNullOrEmpty(driverSourcePath))
            {
                progress?.Report(new TaskProgressDetail
                {
                    StatusText = _localization.GetString("Progress_ExportingDrivers"),
                    TerminalOutput = "This may take several minutes"
                });

                var tempDriverPath = _fileSystemService.CombinePath(_fileSystemService.GetTempPath(), $"WinOptimizerDrivers_{Guid.NewGuid()}");
                _fileSystemService.CreateDirectory(tempDriverPath);

                try
                {
                    var arguments = $"/Online /Export-Driver /Destination:\"{tempDriverPath}\"";

                    progress?.Report(new TaskProgressDetail
                    {
                        TerminalOutput = "Exporting drivers from current system..."
                    });

                    var (exitCode, _) = await _dismProcessRunner.RunProcessWithProgressAsync("dism.exe", arguments, progress, cancellationToken).ConfigureAwait(false);
                    if (exitCode != 0)
                    {
                        throw new Exception($"DISM Export-Driver failed with exit code: {exitCode}");
                    }

                    sourceDirectory = tempDriverPath;
                }
                catch (Exception ex)
                {
                    try { _fileSystemService.DeleteDirectory(tempDriverPath, recursive: true); } catch (Exception cleanupEx) { _logService.LogDebug($"Best-effort temp driver directory cleanup failed: {cleanupEx.Message}"); }
                    _logService.LogError($"Failed to export system drivers: {ex.Message}", ex);
                    return false;
                }
            }
            else
            {
                progress?.Report(new TaskProgressDetail
                {
                    StatusText = _localization.GetString("Progress_ValidatingDrivers"),
                    TerminalOutput = driverSourcePath
                });

                if (!_fileSystemService.DirectoryExists(driverSourcePath))
                {
                    _logService.LogError($"Driver source path does not exist: {driverSourcePath}");
                    return false;
                }

                sourceDirectory = driverSourcePath;
            }

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_CategorizingDrivers"),
                TerminalOutput = "Separating storage and post-install drivers"
            });

            var winpeDriverPath = _fileSystemService.CombinePath(workingDirectory, "sources", "$WinpeDriver$");
            var oemDriverPath = _fileSystemService.CombinePath(workingDirectory, "sources", "$OEM$", "$$", "Drivers");

            _logService.LogInformation($"Searching for drivers in: {sourceDirectory}");

            int copiedCount = await Task.Run(() => _driverCategorizer.CategorizeAndCopyDrivers(
                sourceDirectory,
                winpeDriverPath,
                oemDriverPath,
                workingDirectory
            ), cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(driverSourcePath))
            {
                try
                {
                    _fileSystemService.DeleteDirectory(sourceDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"Could not delete temp directory: {ex.Message}");
                }
            }

            if (copiedCount == 0)
            {
                _logService.LogWarning($"No drivers were found or copied from: {sourceDirectory}");
                return false;
            }

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_CreatingDriverScript"),
                TerminalOutput = "Setting up SetupComplete.cmd"
            });

            await CreateSetupCompleteScriptAsync(workingDirectory, cancellationToken).ConfigureAwait(false);

            _logService.LogInformation($"Successfully added {copiedCount} driver(s) - WinPE: {winpeDriverPath}, OEM: {oemDriverPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error adding drivers: {ex.Message}", ex);
            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_DriverAdditionFailed"),
                TerminalOutput = ex.Message
            });
            return false;
        }
    }

    public async Task<bool> AddXmlToImageAsync(string xmlPath, string workingDirectory)
    {
        try
        {
            if (!_fileSystemService.FileExists(xmlPath))
            {
                _logService.LogError($"XML file not found: {xmlPath}");
                return false;
            }

            if (!_fileSystemService.DirectoryExists(workingDirectory))
            {
                _logService.LogError($"Working directory not found: {workingDirectory}");
                return false;
            }

            var destPath = _fileSystemService.CombinePath(workingDirectory, "autounattend.xml");

            var xmlContent = await _fileSystemService.ReadAllTextAsync(xmlPath).ConfigureAwait(false);
            await _fileSystemService.WriteAllTextAsync(destPath, xmlContent).ConfigureAwait(false);

            _logService.LogInformation($"Added autounattend.xml to image: {destPath}");
            return true;
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error adding XML to image: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>Prepends one returning driver hook while retaining existing OEM setup commands.</summary>
    private async Task CreateSetupCompleteScriptAsync(string workingDirectory, CancellationToken ct)
    {
        var scriptsPath = _fileSystemService.CombinePath(workingDirectory, "sources", "$OEM$", "$$", "Setup", "Scripts");
        _fileSystemService.CreateDirectory(scriptsPath);
        const string driverName = "WinOptimizer-Drivers.cmd";
        var driverPath = _fileSystemService.CombinePath(scriptsPath, driverName);
        var driverScript = """
            @echo off
            setlocal
            set "LOGFILE=%SystemRoot%\Logs\DriverInstall.log"
            "%SystemRoot%\System32\pnputil.exe" /add-driver "%SystemRoot%\Drivers\*.inf" /subdirs /install >> "%LOGFILE%" 2>&1
            set "DriverExit=%ERRORLEVEL%"
            echo Driver installation exit code: %DriverExit% >> "%LOGFILE%"
            exit /b %DriverExit%
            """;
        await _fileSystemService.WriteAllTextAsync(driverPath, driverScript, ct).ConfigureAwait(false);
        var setupPath = _fileSystemService.CombinePath(scriptsPath, "SetupComplete.cmd");
        var existing = _fileSystemService.FileExists(setupPath)
            ? await _fileSystemService.ReadAllBytesAsync(setupPath, ct).ConfigureAwait(false) : [];
        if (existing.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || existing.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            throw new IOException("SetupComplete.cmd uses UTF-16, which cmd.exe cannot execute. Preserve the file and select media with an ANSI or UTF-8 setup script.");
        int preamble = existing.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var hook = System.Text.Encoding.ASCII.GetBytes($"@call \"%~dp0{driverName}\"\r\n");
        if (preamble != 0 || !existing.AsSpan().StartsWith(hook))
        {
            // cmd.exe treats a UTF-8 BOM as command text. Keep the payload without its preamble.
            byte[] updated = existing.AsSpan(preamble).StartsWith(hook)
                ? existing[preamble..] : [.. hook, .. existing.AsSpan(preamble)];
            await _fileSystemService.WriteAllBytesAsync(setupPath, updated, ct).ConfigureAwait(false);
        }
        _logService.LogInformation($"Prepared driver setup hook at: {setupPath}");
    }
}
