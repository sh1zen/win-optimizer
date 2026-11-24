using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.AdvancedTools;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Exceptions;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.AdvancedTools.Services;

public class IsoService : IIsoService
{
    private static readonly Regex DriveLetterRegex = new(@"\A[A-Z]\z", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogService _logService;
    private readonly ILocalizationService _localization;
    private readonly IProcessExecutor _processExecutor;
    private readonly IDismProcessRunner _dismProcessRunner;
    private readonly IOscdimgToolManager _oscdimgToolManager;

    public IsoService(
        IFileSystemService fileSystemService,
        ILogService logService,
        ILocalizationService localization,
        IProcessExecutor processExecutor,
        IDismProcessRunner dismProcessRunner,
        IOscdimgToolManager oscdimgToolManager)
    {
        _fileSystemService = fileSystemService;
        _logService = logService;
        _localization = localization;
        _processExecutor = processExecutor;
        _dismProcessRunner = dismProcessRunner;
        _oscdimgToolManager = oscdimgToolManager;
    }

    public Task<bool> ValidateIsoFileAsync(string isoPath)
    {
        if (!_fileSystemService.FileExists(isoPath))
        {
            _logService.LogError($"ISO file not found: {isoPath}");
            return Task.FromResult(false);
        }

        var extension = _fileSystemService.GetExtension(isoPath).ToLowerInvariant();
        if (extension != ".iso")
        {
            _logService.LogError($"Invalid file extension: {extension}. Expected .iso");
            return Task.FromResult(false);
        }

        // Check if it's a valid ISO by attempting to read it
        try
        {
            var fileSize = _fileSystemService.GetFileSize(isoPath);
            if (fileSize < 1024 * 1024) // Less than 1MB
            {
                _logService.LogError("ISO file is too small to be valid");
                return Task.FromResult(false);
            }

            _logService.LogInformation($"ISO file validated: {isoPath} ({fileSize:N0} bytes)");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error validating ISO: {ex.Message}", ex);
            return Task.FromResult(false);
        }
    }

    public async Task<bool> ExtractIsoAsync(
        string isoPath,
        string workingDirectory,
        IProgress<TaskProgressDetail>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var isoMounted = false;
        var isoLiteral = isoPath.Replace("'", "''");
        var workingLiteral = workingDirectory.Replace("'", "''");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(isoPath);
            var workspacePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            var sourceRelativePath = Path.GetRelativePath(workspacePath, sourcePath);
            if (!Path.IsPathRooted(sourceRelativePath) && sourceRelativePath != ".." &&
                !sourceRelativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Move the source ISO outside the extraction folder before extracting it.");

            if (!await ValidateIsoFileAsync(isoPath).ConfigureAwait(false))
            {
                return false;
            }

            var isoFileSize = _fileSystemService.GetFileSize(isoPath);
            var requiredSpace = isoFileSize + (2L * 1024 * 1024 * 1024);

            await _dismProcessRunner.CheckDiskSpaceAsync(workingDirectory, requiredSpace, "ISO extraction").ConfigureAwait(false);

            if (_fileSystemService.DirectoryExists(workingDirectory))
            {
                _logService.LogInformation($"Clearing existing working directory: {workingDirectory}");

                try
                {
                    var script = $@"
                        Get-ChildItem -LiteralPath '{workingLiteral}' -Recurse -Force | ForEach-Object {{ $_.Attributes = 'Normal' }}
                        Remove-Item -LiteralPath '{workingLiteral}' -Recurse -Force -ErrorAction Stop
                    ";

                    var removeResult = await _processExecutor.ExecuteAsync(
                        "powershell.exe",
                        $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                        cancellationToken).ConfigureAwait(false);
                    var errorOutput = removeResult.StandardError;

                    if (_fileSystemService.DirectoryExists(workingDirectory))
                    {
                        _logService.LogError($"Failed to delete working directory. It may be in use by another process: {errorOutput}");
                        throw new InvalidOperationException(
                            $"Could not delete the existing working directory '{workingDirectory}'. " +
                            "It may be open in Windows Explorer or being used by another process. " +
                            "Please close it or delete it manually and try again."
                        );
                    }

                    _logService.LogInformation("Working directory cleared successfully");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception cleanupEx)
                {
                    _logService.LogError($"Failed to clear working directory: {cleanupEx.Message}", cleanupEx);
                    throw new InvalidOperationException($"Could not clear existing working directory: {cleanupEx.Message}", cleanupEx);
                }
            }

            _fileSystemService.CreateDirectory(workingDirectory);

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_MountingIso"),
                TerminalOutput = $"ISO: {isoPath}"
            });

            _logService.LogInformation($"Mounting ISO: {isoPath}");

            var mountResult = await _processExecutor.ExecuteAsync(
                "powershell.exe",
                $"-NoProfile -Command \"Mount-DiskImage -ImagePath '{isoLiteral}' -ErrorAction Stop\"").ConfigureAwait(false);
            if (!mountResult.Succeeded)
                throw new IOException($"Could not mount the ISO: {mountResult.StandardError}");
            // Record a successful native mount before observing cancellation and releasing it in finally.
            isoMounted = true;
            cancellationToken.ThrowIfCancellationRequested();

            var volumeResult = await _processExecutor.ExecuteAsync(
                "powershell.exe",
                $"-NoProfile -Command \"$ErrorActionPreference = 'Stop'; (Get-DiskImage -ImagePath '{isoLiteral}' | Get-Volume).DriveLetter\"",
                cancellationToken).ConfigureAwait(false);
            var driveLetter = volumeResult.StandardOutput.Trim();
            if (!volumeResult.Succeeded || !DriveLetterRegex.IsMatch(driveLetter))
                throw new IOException($"Windows could not identify the drive for the mounted ISO. {volumeResult.StandardError}".Trim());
            var mountedPath = $"{driveLetter}:\\";
            _logService.LogInformation($"ISO mounted to: {mountedPath}");

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_CopyingIsoContents"),
                TerminalOutput = $"Source: {mountedPath}"
            });

            await Task.Run(() => CopyDirectory(mountedPath, workingDirectory, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_DismountingIso"),
                TerminalOutput = "Cleaning up..."
            });

            await DismountIsoAsync(isoLiteral).ConfigureAwait(false);
            isoMounted = false;

            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsSetupMedia.HasRequiredFiles(_fileSystemService, workingDirectory))
                throw new IOException("The extracted media is missing Windows Setup or its boot and installation images.");

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_IsoExtractionCompleted"),
                TerminalOutput = $"Extracted to: {workingDirectory}"
            });

            _logService.LogInformation($"ISO extracted successfully to: {workingDirectory}");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logService.LogInformation("ISO extraction was cancelled");
            throw;
        }
        catch (InsufficientDiskSpaceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error extracting ISO: {ex.Message}", ex);
            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_IsoExtractionFailed"),
                TerminalOutput = ex.Message
            });
            return false;
        }
        finally
        {
            if (isoMounted)
            {
                try { await DismountIsoAsync(isoLiteral).ConfigureAwait(false); }
                catch (Exception ex) { _logService.LogWarning($"Could not release the mounted ISO: {ex.Message}"); }
            }
        }
    }

    private async Task DismountIsoAsync(string isoLiteral)
    {
        var result = await _processExecutor.ExecuteAsync("powershell.exe",
            $"-NoProfile -Command \"Dismount-DiskImage -ImagePath '{isoLiteral}' -ErrorAction Stop\"").ConfigureAwait(false);
        if (!result.Succeeded)
            throw new IOException($"Windows could not dismount the ISO: {result.StandardError}");
    }

    /// <summary>Owns the temporary ISO and publishes it only after successful creation and validation.</summary>
    public async Task<bool> CreateIsoAsync(
        string workingDirectory,
        string outputPath,
        IProgress<TaskProgressDetail>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? stagedPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(workingDirectory))
            {
                _logService.LogError("Working directory path is empty.");
                return false;
            }

            var oscdimgPath = _oscdimgToolManager.GetOscdimgPath();

            if (!await _oscdimgToolManager.IsOscdimgAvailableAsync().ConfigureAwait(false))
            {
                _logService.LogError("oscdimg.exe is not available. Please download it first.");
                return false;
            }

            var workingDirSize = _fileSystemService.GetDirectoryFileSize(workingDirectory);

            var requiredSpace = workingDirSize + (2L * 1024 * 1024 * 1024);

            await _dismProcessRunner.CheckDiskSpaceAsync(outputPath, requiredSpace, "ISO creation").ConfigureAwait(false);

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_CreatingBootableIso"),
                TerminalOutput = $"Output: {outputPath}"
            });

            var efisysPath = _fileSystemService.CombinePath(workingDirectory, "efi", "microsoft", "boot", "efisys.bin");
            var etfsbootPath = _fileSystemService.CombinePath(workingDirectory, "boot", "etfsboot.com");

            if (!_fileSystemService.FileExists(etfsbootPath))
                throw new FileNotFoundException($"Boot file not found: {etfsbootPath}");

            if (!_fileSystemService.FileExists(efisysPath))
                throw new FileNotFoundException($"UEFI boot file not found: {efisysPath}");

            var outputDir = _fileSystemService.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !_fileSystemService.DirectoryExists(outputDir))
                _fileSystemService.CreateDirectory(outputDir);

            cancellationToken.ThrowIfCancellationRequested();
            stagedPath = outputPath + $".{Guid.NewGuid():N}.tmp.iso";
            var arguments = $"-m -o -u2 -udfver102 -bootdata:2#p0,e,b\"{etfsbootPath}\"#pEF,e,b\"{efisysPath}\" \"{workingDirectory}\" \"{stagedPath}\"";

            progress?.Report(new TaskProgressDetail
            {
                TerminalOutput = "Running oscdimg.exe...\nThis may take several minutes..."
            });

            var (exitCode, _) = await _dismProcessRunner.RunProcessWithProgressAsync(oscdimgPath, arguments, progress, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new Exception($"oscdimg.exe failed with exit code: {exitCode}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await ValidateIsoFileAsync(stagedPath).ConfigureAwait(false))
            {
                _logService.LogError("The generated ISO is missing or invalid. The destination has been preserved.");
                return false;
            }

            var isoFileSize = _fileSystemService.GetFileSize(stagedPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (_fileSystemService.FileExists(outputPath))
                _fileSystemService.ReplaceFile(stagedPath, outputPath);
            else
                _fileSystemService.MoveFile(stagedPath, outputPath);
            stagedPath = null;
            _logService.LogInformation($"ISO created successfully: {outputPath} ({isoFileSize:N0} bytes)");

            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_IsoCreatedSuccess"),
                TerminalOutput = $"Location: {outputPath}\nSize: {isoFileSize / (1024 * 1024):F2} MB"
            });

            return true;
        }
        catch (OperationCanceledException)
        {
            _logService.LogInformation("ISO creation was cancelled");
            throw;
        }
        catch (InsufficientDiskSpaceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error creating ISO: {ex.Message}", ex);
            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_IsoCreationFailed"),
                TerminalOutput = ex.Message
            });
            return false;
        }
        finally
        {
            // Only this attempt's temporary artifact is eligible for cleanup.
            if (stagedPath is not null)
            {
                try { if (_fileSystemService.FileExists(stagedPath)) _fileSystemService.DeleteFile(stagedPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { _logService.LogWarning($"Could not remove incomplete ISO '{stagedPath}': {ex.Message}"); }
            }
        }
    }

    public async Task<bool> CleanupWorkingDirectoryAsync(string workingDirectory)
    {
        try
        {
            if (!_fileSystemService.DirectoryExists(workingDirectory))
            {
                return true;
            }

            _logService.LogInformation($"Cleaning up working directory: {workingDirectory}");

            await Task.Run(() =>
            {
                _fileSystemService.DeleteDirectory(workingDirectory, recursive: true);
            }).ConfigureAwait(false);

            _logService.LogInformation("Working directory cleaned up successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logService.LogError($"Error cleaning up working directory: {ex.Message}", ex);
            return false;
        }
    }

    private void CopyDirectory(string sourceDir, string destDir, IProgress<TaskProgressDetail>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (directories, files) = _fileSystemService.ReadDirectoryContents(sourceDir);

        _fileSystemService.CreateDirectory(destDir);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = _fileSystemService.GetFileName(file);
            var targetFilePath = _fileSystemService.CombinePath(destDir, fileName);
            progress?.Report(new TaskProgressDetail
            {
                StatusText = _localization.GetString("Progress_CopyingFile", fileName),
                TerminalOutput = fileName
            });
            _fileSystemService.CopyFile(file, targetFilePath, true);
        }

        foreach (var subDir in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subDirName = _fileSystemService.GetFileName(subDir);
            var newDestDir = _fileSystemService.CombinePath(destDir, subDirName);
            CopyDirectory(subDir, newDestDir, progress, cancellationToken);
        }
    }
}
