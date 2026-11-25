using WinOptimizer.Core.Features.Common.Constants;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public class VersionService : IVersionService
{
    // Dev-only override: when WIN_OPTIMIZER_LOCAL_INSTALLER points at an existing
    // file, the in-app updater skips the GitHub round-trip entirely and uses
    // that file as the "downloaded" installer. Used to test the silent-install
    // flow (issue #649 and friends) without publishing a real release. Inert
    // unless both conditions hold (env var set AND file exists), and every
    // activation emits a loud [DEV MODE] warning log.
    private const string LocalInstallerEnvVar = "WIN_OPTIMIZER_LOCAL_INSTALLER";
    private const string InstallerFileName = "win-optimizer.Installer.exe";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/sh1zen/win-optimizer/releases/latest";
    private const string ReleaseDownloadRoot = "https://github.com/sh1zen/win-optimizer/releases/download/";
    private const string UserAgent = "win-optimizer-Update-Checker";

    private readonly ILogService _logService;
    private readonly IFileSystemService _fileSystemService;
    private readonly HttpClient _httpClient;
    private string? _downloadedInstallerPath;

    public VersionService(ILogService logService, IProcessExecutor processExecutor, IFileSystemService fileSystemService, HttpClient httpClient)
    {
        _logService = logService;
        ArgumentNullException.ThrowIfNull(processExecutor);
        _fileSystemService = fileSystemService ?? throw new ArgumentNullException(nameof(fileSystemService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public VersionInfo GetCurrentVersion()
    {
        try
        {
            // Get the assembly version
            Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            string? location = assembly.Location;

            if (string.IsNullOrEmpty(location))
            {
                _logService.Log(LogLevel.Error, "Could not determine assembly location for version check");
                return CreateDefaultVersion();
            }

            // Read the release's informational version.
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(location);
            string version = versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "v0.0.0";

            // Trim any build metadata (anything after the + symbol)
            int plusIndex = version.IndexOf('+');
            if (plusIndex > 0)
            {
                version = version.Substring(0, plusIndex);
            }

            // If the version doesn't start with 'v', add it
            if (!version.StartsWith("v"))
            {
                version = $"v{version}";
            }

            return VersionInfo.FromTag(version);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error getting current version: {ex.Message}", ex);
            return CreateDefaultVersion();
        }
    }

    public async Task<VersionInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var logTopic = _logService.BeginTopic(LogTopic.Updates);
        // Dev-only short-circuit. See LocalInstallerEnvVar docs.
        if (TryGetLocalInstallerOverride() is { } localPath)
        {
            _logService.Log(LogLevel.Warning,
                $"[DEV MODE] {LocalInstallerEnvVar} set to {localPath}. Skipping GitHub release lookup and reporting a synthetic update.");
            return new VersionInfo
            {
                Version = "v99.99.99-localdev",
                ReleaseDate = DateTime.UtcNow,
                IsUpdateAvailable = true,
            };
        }

        const int maxRetries = 3;
        int delayMs = 2000;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _logService.Log(LogLevel.Info, attempt == 1
                    ? "Checking for updates..."
                    : $"Checking for updates (attempt {attempt}/{maxRetries})...");

                using var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
                if (!IsPublishedStableRelease(release.RootElement))
                {
                    _logService.Log(LogLevel.Info, "Only published stable releases are eligible for updates.");
                    return new VersionInfo();
                }

                // Extract the tag name (version) from the response
                string tagName = release.RootElement.GetProperty("tag_name").GetString() ?? "v0.0.0";

                VersionInfo latestVersion = VersionInfo.FromTag(tagName);
                if (string.IsNullOrEmpty(latestVersion.Version))
                {
                    _logService.Log(LogLevel.Warning, $"Release tag is not a valid stable version: {tagName}");
                    return new VersionInfo();
                }

                // Compare with current version
                VersionInfo currentVersion = GetCurrentVersion();
                latestVersion = latestVersion with { IsUpdateAvailable = latestVersion.IsNewerThan(currentVersion) };

                _logService.Log(LogLevel.Info, $"Current version: {currentVersion.Version}, Latest version: {latestVersion.Version}, Update available: {latestVersion.IsUpdateAvailable}");

                return latestVersion;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Real cancellation — don't retry, propagate immediately
                throw;
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientError(ex))
            {
                _logService.Log(LogLevel.Warning, $"Update check attempt {attempt}/{maxRetries} failed: {ex.Message}. Retrying in {delayMs / 1000}s...");
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs *= 2;
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error checking for updates: {ex.Message}", ex);
                throw;
            }
        }
    }

    private static bool IsTransientError(Exception ex)
    {
        // DNS resolution failures, timeouts, and connection refused are transient
        if (ex is HttpRequestException)
            return true;
        // Only retry on HTTP timeout, not on user-initiated cancellation
        if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException)
            return true;
        return false;
    }

    // Use the same release endpoint for version checks and installer metadata.
    private async Task<JsonDocument> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPublishedStableRelease(JsonElement release) =>
        !(release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) &&
        !(release.TryGetProperty("draft", out var draft) && draft.GetBoolean());

    /// <summary>Verifies the published installer before making it available to the relaunch action.</summary>
    public async Task DownloadAndInstallUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var logTopic = _logService.BeginTopic(LogTopic.Updates);
        _downloadedInstallerPath = null;
        // Dev-only short-circuit. See LocalInstallerEnvVar docs.
        if (TryGetLocalInstallerOverride() is { } localPath)
        {
            _logService.Log(LogLevel.Warning,
                $"[DEV MODE] {LocalInstallerEnvVar} set to {localPath}. Skipping HTTP download and using the local file as the staged installer.");
            _downloadedInstallerPath = localPath;
            // Match the async signature without doing real I/O.
            await Task.CompletedTask.ConfigureAwait(false);
            return;
        }

        _logService.Log(LogLevel.Info, "Downloading update...");

        // Pin the download to the release that supplied the digest.
        using var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (!IsPublishedStableRelease(release.RootElement) ||
            !release.RootElement.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            tag.GetString() is not { } version || string.IsNullOrEmpty(VersionInfo.FromTag(version).Version))
            throw new InvalidDataException("The latest GitHub release is not a published stable version.");
        byte[] expectedDigest = ReadInstallerDigest(release.RootElement);
        string downloadUrl = $"{ReleaseDownloadRoot}{Uri.EscapeDataString(version)}/{InstallerFileName}";

        // Stage beside the destination so validation and publication use the same file.
        _fileSystemService.CreateDirectory(AppDataPaths.Downloads);
        string installerPath = _fileSystemService.CombinePath(AppDataPaths.Downloads, InstallerFileName);
        string stagedPath = installerPath + $".{Guid.NewGuid():N}.download";

        // Download the installer using streaming to avoid loading the entire file into memory
        using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        downloadRequest.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await _httpClient.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        try
        {
            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var fileStream = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                fileStream.Position = 0;
                byte[] actualDigest = await SHA256.HashDataAsync(fileStream, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
                    throw new InvalidDataException("The downloaded installer does not match the SHA-256 digest published by GitHub.");

                fileStream.Position = 0;
                ValidateInstaller(fileStream);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagedPath, installerPath, overwrite: true);
            _downloadedInstallerPath = installerPath;
            _logService.Log(LogLevel.Info, $"Update downloaded to {installerPath}");
        }
        finally { File.Delete(stagedPath); }
    }

    // Require the exact installer asset and a complete SHA-256 value from GitHub.
    private static byte[] ReadInstallerDigest(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The GitHub release is missing its installer assets.");

        byte[]? expected = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || name.GetString() != InstallerFileName)
                continue;

            if (expected is not null)
                throw new InvalidDataException("The GitHub release contains multiple assets with the installer name.");

            if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String ||
                digest.GetString() is not { Length: 71 } value || !value.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("The GitHub release is missing a valid SHA-256 digest for the installer.");

            try { expected = Convert.FromHexString(value.AsSpan(7)); }
            catch (FormatException ex)
            {
                throw new InvalidDataException("The GitHub release contains a malformed installer SHA-256 digest.", ex);
            }
        }

        return expected ?? throw new InvalidDataException("The GitHub release is missing the installer asset.");
    }

    private static void ValidateInstaller(Stream file)
    {
        try
        {
            using var reader = new PEReader(file, PEStreamOptions.LeaveOpen);
            var headers = reader.PEHeaders;
            var characteristics = headers.CoffHeader.Characteristics;
            if (headers.PEHeader is null || (characteristics & Characteristics.ExecutableImage) == 0 ||
                (characteristics & Characteristics.Dll) != 0 || headers.SectionHeaders.Length == 0 ||
                headers.SectionHeaders.Any(section => section.SizeOfRawData > 0 &&
                    (long)section.PointerToRawData + section.SizeOfRawData > file.Length))
                throw new BadImageFormatException();
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException("The downloaded update is not a complete Windows installer.", ex);
        }
    }

    public void LaunchInstallerAndRestart()
    {
        var installerPath = _downloadedInstallerPath;
        if (string.IsNullOrEmpty(installerPath))
            throw new InvalidOperationException("No update has been downloaded.");

        string appDir = AppContext.BaseDirectory;
        bool isPortable = _fileSystemService.FileExists(_fileSystemService.CombinePath(appDir, "portable.marker"));
        string appExePath = _fileSystemService.CombinePath(appDir, "win-optimizer.exe");

        string installerArgs = BuildInstallerArgs(appDir, isPortable);

        _logService.Log(LogLevel.Info, $"Launching installer (portable: {isPortable}), app will restart after install...");

        // Use cmd /c to: run installer (wait for it to finish) then relaunch the app.
        // The caller should exit the application immediately after this call.
        var cmdArgs = $"/c start /wait \"\" \"{installerPath}\" {installerArgs} && start \"\" \"{appExePath}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmdArgs,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    /// <summary>
    /// Returns the local installer path if WIN_OPTIMIZER_LOCAL_INSTALLER is set and
    /// points at an existing file, otherwise null. Inert in normal user runs.
    /// </summary>
    private string? TryGetLocalInstallerOverride()
    {
        string? path = Environment.GetEnvironmentVariable(LocalInstallerEnvVar);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!_fileSystemService.FileExists(path))
        {
            _logService.Log(LogLevel.Warning,
                $"[DEV MODE] {LocalInstallerEnvVar}={path} but the file does not exist. Falling back to normal update flow.");
            return null;
        }
        return path;
    }

    /// <summary>
    /// Builds the Inno Setup installer arguments for an in-app silent update.
    /// Always pins the install directory to <paramref name="appDir"/> via /DIR=
    /// (issue #649): without /DIR, Inno's UsePreviousAppDir/DefaultDirName chain
    /// can resolve {app} to C:\Program Files\win-optimizer for regular installs at a
    /// custom path, or to ~\Desktop\win-optimizer for portable installs, silently
    /// relocating the install. The installer script (win-optimizer.Installer.iss)
    /// initializes its folder picker from Inno's resolved /DIR value and keeps
    /// that exact destination during silent updates.
    /// </summary>
    /// <remarks>
    /// Internal for testability via the test project's InternalsVisibleTo grant.
    /// Pure function — no side effects.
    /// </remarks>
    internal static string BuildInstallerArgs(string appDir, bool isPortable)
    {
        string dirArg = $"/DIR=\"{appDir.TrimEnd('\\', '/')}\"";
        return isPortable
            ? $"/SILENT /SUPPRESSMSGBOXES {dirArg} /MERGETASKS=\"portableinstall\""
            : $"/SILENT /SUPPRESSMSGBOXES {dirArg} /MERGETASKS=\"regularinstall\\desktopicon,regularinstall\\startmenuicon\"";
    }

    private VersionInfo CreateDefaultVersion()
    {
        // Create a default version based on the current date
        DateTime now = DateTime.Now;
        string versionTag = $"v{now.Year - 2000:D2}.{now.Month:D2}.{now.Day:D2}";

        return new VersionInfo
        {
            Version = versionTag,
            ReleaseDate = now
        };
    }
}
