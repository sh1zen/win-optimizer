using System.Text.Json;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>Provides typed lookups over one read of the interactive user's preferences.</summary>
public sealed class UserPreferencesSnapshot(IReadOnlyDictionary<string, object> values, ILogService log)
{
    public T Get<T>(string key, T defaultValue)
    {
        if (!values.TryGetValue(key, out var value)) return defaultValue;
        try { return ConvertValue(value, defaultValue); }
        catch (Exception ex)
        {
            log.Log(LogLevel.Error, $"Error converting preference value for key '{key}': {ex.Message}");
            if (ex.InnerException is { } inner) log.Log(LogLevel.Error, $"Inner exception: {inner.Message}");
            log.Log(LogLevel.Error, $"Stack trace: {ex.StackTrace}");
            return defaultValue;
        }
    }

    /// <summary>Uses the same type conversion for a snapshot and the synchronous preference reader.</summary>
    public static T ConvertValue<T>(object? value, T defaultValue)
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
