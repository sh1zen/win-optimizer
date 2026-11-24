using System.Text.Json;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Infrastructure.Features.Common.Services;

namespace WinOptimizer.Infrastructure.Features.AdvancedTools.Experimental;

/// <summary>Atomically checkpoints the recovery receipt in protected application state.</summary>
internal sealed class ExperimentalRecoveryStore : IExperimentalRecoveryStore
{
    private static string StatePath => Path.Combine(AppDataPaths.State, "ExperimentalFeatures.v1.json");
    private static readonly JsonSerializerOptions Format = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public IDisposable ReserveWrite()
    {
        var path = Path.Combine(AppDataPaths.State, "ExperimentalFeatures.lock");
        AppDataStorage.ValidatePath(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public ExperimentalRecovery Read()
    {
        AppDataStorage.ValidatePath(StatePath);
        try
        {
            using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var state = JsonSerializer.Deserialize<ExperimentalRecovery>(stream, Format)
                ?? throw new InvalidDataException("The experimental feature recovery file is empty.");
            ValidateReceipt(state.Last);
            ValidateReceipt(state.Undo);
            foreach (var item in state.Deferred ?? [])
            {
                if (item?.Last is null) throw new InvalidDataException("The deferred feature recovery receipt is invalid.");
                ValidateReceipt(item.Last);
                ValidateReceipt(item.Undo);
            }
            if (state.Pending.Any(entry => entry.Key == 0 || entry.Value is null || entry.Value.Persistent is { IsValid: false }))
                throw new InvalidDataException("The experimental feature restart record is invalid.");
            return state;
        }
        catch (FileNotFoundException) { return new(null, null, []); }
    }

    public void Save(ExperimentalRecovery state)
    {
        var path = StatePath;
        AppDataStorage.ValidatePath(path);
        var temp = Path.Combine(AppDataPaths.State, $"ExperimentalFeatures-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, Format);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void ValidateReceipt(ExperimentalReceipt? receipt)
    {
        if (receipt is null) return;
        if (receipt.Items.Length == 0 || receipt.Items.Any(item => item is null || item.Before.Id == 0 ||
            item.Before.Persistent is { IsValid: false } || item.Before.Runtime?.Value is { IsValid: false } ||
            item.Persistent is { IsValid: false } || item.Runtime is { IsValid: false } ||
            !Enum.IsDefined(item.RuntimeStage) || !Enum.IsDefined(item.PersistenceStage)) ||
            receipt.Items.Select(item => item.Before.Id).Distinct().Count() != receipt.Items.Length)
            throw new InvalidDataException("The experimental feature recovery receipt is invalid.");
    }
}
