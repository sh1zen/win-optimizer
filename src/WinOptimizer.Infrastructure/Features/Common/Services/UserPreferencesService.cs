using WinOptimizer.Core.Features.Common.Constants;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>
/// Service for managing user preferences stored as JSON.
/// </summary>
public class UserPreferencesService : IUserPreferencesService
{
    private readonly ILogService _logService;
    private readonly IInteractiveUserService _interactiveUserService;
    private readonly IFileSystemService _fileSystemService;
    private readonly TimeMachineStore? _timeMachine;

    // Serializes read-modify-write operations so concurrent preference saves retain every change.
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public UserPreferencesService(ILogService logService, IInteractiveUserService interactiveUserService,
        IFileSystemService fileSystemService, TimeMachineStore? timeMachine = null)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _interactiveUserService = interactiveUserService ?? throw new ArgumentNullException(nameof(interactiveUserService));
        _fileSystemService = fileSystemService ?? throw new ArgumentNullException(nameof(fileSystemService));
        _timeMachine = timeMachine;
    }

    /// <summary>Uses the interactive account's preferences in the common application data root.</summary>
    private string GetPreferencesFilePath() => EnsurePreferencesPath(AppDataPaths.Preferences(_interactiveUserService));

    private string EnsurePreferencesPath(string path)
    {
        AppDataStorage.ValidatePath(path);
        _fileSystemService.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Serializes access to this account's preference document across app processes.</summary>
    private Task<Stream> LockDocumentAsync(string path)
    {
        var lockPath = path + ".lock";
        AppDataStorage.ValidatePath(lockPath);
        return _fileSystemService.OpenExclusiveFileAsync(lockPath);
    }

    public async Task<Dictionary<string, object>> GetPreferencesAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetPreferencesFilePath();
            using var document = await LockDocumentAsync(path).ConfigureAwait(false);
            return await GetPreferencesUnsafeAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error getting user preferences: {ex.Message}");
            return new Dictionary<string, object>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> GetPreferencesUnsafeAsync(string filePath)
    {
        try
        {
            if (!_fileSystemService.FileExists(filePath))
            {
                _logService.Log(LogLevel.Info, $"User preferences file does not exist at '{filePath}', returning empty preferences");
                return new Dictionary<string, object>();
            }

            string json = await _fileSystemService.ReadAllTextAsync(filePath).ConfigureAwait(false);

            if (string.IsNullOrEmpty(json))
            {
                throw new JsonException("The preferences file is empty.");
            }

            var preferences = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (preferences != null)
            {
                _logService.Log(LogLevel.Info, $"Successfully loaded {preferences.Count} preferences");

                return preferences;
            }
            else
            {
                throw new JsonException("The preferences file must contain an object.");
            }
        }
        catch (JsonException ex)
        { throw new IOException("The preferences file is invalid. Restore or repair it before saving settings.", ex); }
    }

    public async Task<OperationResult> SavePreferencesAsync(Dictionary<string, object> preferences)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetPreferencesFilePath();
            using var document = await LockDocumentAsync(path).ConfigureAwait(false);
            var previous = _timeMachine is null ? null : await GetPreferencesUnsafeAsync(path).ConfigureAwait(false);
            return await SaveTrackedPreferencesAsync(previous, preferences, path).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<OperationResult> SavePreferencesUnsafeAsync(Dictionary<string, object> preferences, string? resolvedPath = null)
    {
        try
        {
            string filePath = resolvedPath is null ? GetPreferencesFilePath() : EnsurePreferencesPath(resolvedPath);

            string json = JsonSerializer.Serialize(preferences, WriteOptions);

            await _fileSystemService.WriteAllTextAsync(filePath, json).ConfigureAwait(false);

            if (_fileSystemService.FileExists(filePath))
            {
                _logService.Log(LogLevel.Info, $"User preferences saved successfully to '{filePath}'");
                return OperationResult.Succeeded();
            }
            else
            {
                _logService.Log(LogLevel.Error, $"File not found after writing: '{filePath}'");
                return OperationResult.Failed($"File not found after writing: '{filePath}'");
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error saving user preferences: {ex.Message}");
            if (ex.InnerException != null)
            {
                _logService.Log(LogLevel.Error, $"Inner exception: {ex.InnerException.Message}");
            }
            return OperationResult.Failed(ex.Message, ex);
        }
    }

    public async Task<UserPreferencesSnapshot> ReadSnapshotAsync() =>
        new(await GetPreferencesAsync().ConfigureAwait(false), _logService);

    public async Task<T> GetPreferenceAsync<T>(string key, T defaultValue) =>
        (await ReadSnapshotAsync().ConfigureAwait(false)).Get(key, defaultValue);

    public async Task<OperationResult> SetPreferenceAsync<T>(string key, T value)
    {
        var result = await UpdatePreferencesAsync(new Dictionary<string, object> { [key] = value! }).ConfigureAwait(false);
        _logService.Log(result.Success ? LogLevel.Info : LogLevel.Error,
            result.Success ? $"Successfully saved preference '{key}'" : $"Failed to save preference '{key}': {result.ErrorMessage}");
        return result;
    }

    /// <summary>Owns the complete read/merge/write transaction for one preference change set.</summary>
    public async Task<OperationResult> UpdatePreferencesAsync(IReadOnlyDictionary<string, object> changes)
    {
        // Hold the file lock across the whole read-modify-write so concurrent
        // SetPreferenceAsync calls don't clobber each other's writes.
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Use the same account path on both sides of the read/modify/write transaction.
            var filePath = GetPreferencesFilePath();
            using var document = await LockDocumentAsync(filePath).ConfigureAwait(false);
            var preferences = await GetPreferencesUnsafeAsync(filePath).ConfigureAwait(false);

            var updated = new Dictionary<string, object>(preferences);
            foreach (var (key, value) in changes) updated[key] = value;
            return await SaveTrackedPreferencesAsync(preferences, updated, filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error updating preferences: {ex.Message}");
            return OperationResult.Failed(ex.Message, ex);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>Records user-facing choices; excludes startup markers, paths and credentials.</summary>
    internal static IReadOnlyList<string> TimeMachineKeys { get; } =
    [
        "Language", "Theme", "Cleaner", "Drivers", "SoftwareAppsViewMode", "SoftwareAppsSortMode",
        UserPreferenceKeys.AppColor, UserPreferenceKeys.BlurWindowBackground, UserPreferenceKeys.SidebarExpanded, UserPreferenceKeys.UiZoomFactor,
        UserPreferenceKeys.Updates, UserPreferenceKeys.EnableWindowsVersionFilter
    ];

    internal async Task<IReadOnlyList<TimeMachineState>> ReadTimeMachinePreferencesAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetPreferencesFilePath();
            using var document = await LockDocumentAsync(path).ConfigureAwait(false);
            var values = await GetPreferencesUnsafeAsync(path).ConfigureAwait(false);
            return TimeMachineKeys.Select(key => PreferenceState(key, values)).ToArray();
        }
        finally { _fileLock.Release(); }
    }

    internal async Task<int> ReadTimeMachineRetentionAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetPreferencesFilePath();
            using var document = await LockDocumentAsync(path).ConfigureAwait(false);
            var values = await GetPreferencesUnsafeAsync(path).ConfigureAwait(false);
            return TimeMachineRetention.Normalize(values.TryGetValue(TimeMachineRetention.PreferenceKey, out var value)
                ? UserPreferencesSnapshot.ConvertValue(value, TimeMachineRetention.DefaultDays) : TimeMachineRetention.DefaultDays);
        }
        finally { _fileLock.Release(); }
    }

    internal async Task RestoreTimeMachinePreferenceAsync(string key, TimeMachineValue desired, TimeMachineValue expected)
    {
        if (!TimeMachineKeys.Contains(key)) throw new IOException("This preference is unavailable for restore.");
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = GetPreferencesFilePath();
            using var document = await LockDocumentAsync(path).ConfigureAwait(false);
            var previous = await GetPreferencesUnsafeAsync(path).ConfigureAwait(false);
            if (!PreferenceState(key, previous).Value.SameAs(expected))
                throw new IOException("The preference changed after review. Compare again.");
            var updated = new Dictionary<string, object>(previous);
            if (desired.Json is null) updated.Remove(key);
            else updated[key] = JsonSerializer.Deserialize<JsonElement>(desired.Json);
            var result = await SaveTrackedPreferencesAsync(previous, updated, path).ConfigureAwait(false);
            if (!result.Success) throw new IOException(result.ErrorMessage);
        }
        finally { _fileLock.Release(); }
    }

    private static TimeMachineState PreferenceState(string key, Dictionary<string, object> values) =>
        new(new(TimeMachineArea.Preference, key), key,
            values.TryGetValue(key, out var value) ? TimeMachineValue.From(value) : TimeMachineValue.Missing);

    private async Task<OperationResult> SaveTrackedPreferencesAsync(Dictionary<string, object>? previous,
        Dictionary<string, object> updated, string path)
    {
        if (_timeMachine is null || previous is null)
            return await SavePreferencesUnsafeAsync(updated, path).ConfigureAwait(false);

        List<TimeMachineUndo> entries = [];
        foreach (var key in TimeMachineKeys)
        {
            var before = PreferenceState(key, previous);
            var after = PreferenceState(key, updated).Value;
            if (!before.Value.SameAs(after)) entries.Add(_timeMachine.PrepareUndo(before, after));
        }
        var result = await SavePreferencesUnsafeAsync(updated, path).ConfigureAwait(false);
        if (result.Success)
            foreach (var entry in entries)
                _timeMachine.Record(LogTopic.Settings, entry.Before.Label, "App preference changed", entry);
        return result;
    }

    /// <summary>
    /// Synchronous version for use during startup to avoid async deadlocks on UI thread.
    /// </summary>
    public T GetPreference<T>(string key, T defaultValue)
    {
        try
        {
            string filePath = GetPreferencesFilePath();
            using var document = LockDocumentAsync(filePath).GetAwaiter().GetResult();

            if (!_fileSystemService.FileExists(filePath))
            {
                return defaultValue;
            }

            string json = _fileSystemService.ReadAllText(filePath);

            if (string.IsNullOrEmpty(json))
            {
                return defaultValue;
            }

            var preferences = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (preferences != null && preferences.TryGetValue(key, out var value))
            {
                return UserPreferencesSnapshot.ConvertValue(value, defaultValue);
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error getting preference '{key}' synchronously: {ex.Message}");
        }

        return defaultValue;
    }

}
