using System.Text.Json;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;

namespace WinOptimizer.Infrastructure.Features.Optimize.Services;

/// <summary>Stores each machine-wide network value once, including absent overrides.</summary>
public sealed class NetworkTuningOriginalStore(IFileSystemService files)
{
    private readonly string _directory = AppDataPaths.NetworkTuningOriginals;

    internal NetworkTuningOriginalStore(IFileSystemService files, string directory) : this(files) => _directory = directory;

    internal Task<NetworkTuningValue?> ReadAsync(string id) => ReadFileAsync(GetPath(id));

    /// <summary>Returns the saved original or atomically publishes the first available value.</summary>
    internal async Task<NetworkTuningValue?> GetOrCaptureAsync(NetworkTuningState state)
    {
        var path = GetPath(state.Id);
        var original = await ReadFileAsync(path).ConfigureAwait(false);
        if (original != null || !state.IsAvailable) return original;

        files.CreateDirectory(_directory);
        var pending = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await files.WriteAllTextAsync(pending, JsonSerializer.Serialize(state.Value)).ConfigureAwait(false);
            try { files.MoveFile(pending, path); }
            catch (IOException) when (files.FileExists(path))
            {
                // Another process may have committed its first read while this one was staged.
                return await ReadFileAsync(path).ConfigureAwait(false)
                    ?? throw new IOException($"The original network value for '{state.Id}' disappeared during capture.");
            }
            return new(state.Value);
        }
        finally { files.DeleteFile(pending); }
    }

    private string GetPath(string id) => AppDataPaths.Child(_directory, id + ".json");

    private async Task<NetworkTuningValue?> ReadFileAsync(string path)
    {
        AppDataStorage.ValidatePath(path);
        try
        {
            var json = await files.ReadAllTextAsync(path).ConfigureAwait(false);
            return new(JsonSerializer.Deserialize<string>(json));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException ex) { throw new IOException($"The saved original network value is invalid: '{path}'.", ex); }
    }
}
