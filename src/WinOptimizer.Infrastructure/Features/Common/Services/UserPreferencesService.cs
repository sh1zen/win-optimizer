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

    // Serializes read-modify-write operations so concurrent preference saves retain every change.
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public UserPreferencesService(ILogService logService, IInteractiveUserService interactiveUserService, IFileSystemService fileSystemService)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _interactiveUserService = interactiveUserService ?? throw new ArgumentNullException(nameof(interactiveUserService));
        _fileSystemService = fileSystemService ?? throw new ArgumentNullException(nameof(fileSystemService));
    }

    /// <summary>Uses the interactive account's preferences in the common application data root.</summary>
    private string GetPreferencesFilePath()
    {
        var path = AppDataPaths.Preferences(_interactiveUserService);
        AppDataStorage.ValidatePath(path);
        _fileSystemService.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public async Task<Dictionary<string, object>> GetPreferencesAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await GetPreferencesUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<Dictionary<string, object>> GetPreferencesUnsafeAsync()
    {
        try
        {
            string filePath = GetPreferencesFilePath();

            if (!_fileSystemService.FileExists(filePath))
            {
                _logService.Log(LogLevel.Info, $"User preferences file does not exist at '{filePath}', returning empty preferences");
                return new Dictionary<string, object>();
            }

            string json = await _fileSystemService.ReadAllTextAsync(filePath).ConfigureAwait(false);

            if (string.IsNullOrEmpty(json))
            {
                _logService.Log(LogLevel.Warning, "Preferences file exists but is empty");
                return new Dictionary<string, object>();
            }

            var preferences = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (preferences != null)
            {
                _logService.Log(LogLevel.Info, $"Successfully loaded {preferences.Count} preferences");

                return preferences;
            }
            else
            {
                _logService.Log(LogLevel.Warning, "Deserialized preferences is null, returning empty dictionary");
                return new Dictionary<string, object>();
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error getting user preferences: {ex.Message}");
            if (ex.InnerException != null)
            {
                _logService.Log(LogLevel.Error, $"Inner exception: {ex.InnerException.Message}");
            }
            return new Dictionary<string, object>();
        }
    }

    public async Task<OperationResult> SavePreferencesAsync(Dictionary<string, object> preferences)
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SavePreferencesUnsafeAsync(preferences).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<OperationResult> SavePreferencesUnsafeAsync(Dictionary<string, object> preferences)
    {
        try
        {
            string filePath = GetPreferencesFilePath();

            string json = JsonSerializer.Serialize(preferences, WriteOptions);

            string? directory = _fileSystemService.GetDirectoryName(filePath);
            if (directory != null && !_fileSystemService.DirectoryExists(directory))
            {
                _fileSystemService.CreateDirectory(directory);
            }

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

    public async Task<T> GetPreferenceAsync<T>(string key, T defaultValue)
    {
        // GetPreferencesAsync acquires _fileLock for us; no extra locking needed here.
        var preferences = await GetPreferencesAsync().ConfigureAwait(false);

        if (preferences.TryGetValue(key, out var value))
        {
            try
            {
                return ReadPreferenceValue(value, defaultValue);
            }
            catch (Exception ex)
            {
                _logService.Log(LogLevel.Error, $"Error converting preference value for key '{key}': {ex.Message}");
                if (ex.InnerException != null)
                {
                    _logService.Log(LogLevel.Error, $"Inner exception: {ex.InnerException.Message}");
                }
                _logService.Log(LogLevel.Error, $"Stack trace: {ex.StackTrace}");
                return defaultValue;
            }
        }

        return defaultValue;
    }

    public async Task<OperationResult> SetPreferenceAsync<T>(string key, T value)
    {
        // Hold the file lock across the whole read-modify-write so concurrent
        // SetPreferenceAsync calls don't clobber each other's writes.
        await _fileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var preferences = await GetPreferencesUnsafeAsync().ConfigureAwait(false);

            preferences[key] = value!;

            var result = await SavePreferencesUnsafeAsync(preferences).ConfigureAwait(false);

            if (result.Success)
            {
                _logService.Log(LogLevel.Info, $"Successfully saved preference '{key}'");
            }
            else
            {
                _logService.Log(LogLevel.Error, $"Failed to save preference '{key}'");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error setting preference '{key}': {ex.Message}");
            return OperationResult.Failed(ex.Message, ex);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Synchronous version for use during startup to avoid async deadlocks on UI thread.
    /// </summary>
    public T GetPreference<T>(string key, T defaultValue)
    {
        try
        {
            string filePath = GetPreferencesFilePath();

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
                return ReadPreferenceValue(value, defaultValue);
            }
        }
        catch (Exception ex)
        {
            _logService.Log(LogLevel.Error, $"Error getting preference '{key}' synchronously: {ex.Message}");
        }

        return defaultValue;
    }

    /// <summary>Reads the requested JSON type using the same contract for both preference APIs.</summary>
    private static T ReadPreferenceValue<T>(object? value, T defaultValue)
    {
        if (value is T typedValue) return typedValue;
        if (value is null) return defaultValue;
        if (value is JsonElement element)
        {
            var storedValue = element.Deserialize<T>();
            return storedValue is null ? defaultValue : storedValue;
        }
        throw new JsonException($"Expected a preference value of type {typeof(T).Name}.");
    }

}
