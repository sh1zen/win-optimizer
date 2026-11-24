using System.Text.Json;
using System.Text.Json.Serialization;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Infrastructure.Features.Common.Services;

namespace WinOptimizer.Infrastructure.Features.AdvancedTools.Experimental;

/// <summary>Loads a bounded data-only document selected by the user.</summary>
internal sealed class ExperimentalProfileFile : IExperimentalProfileFile
{
    private const int MaxBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public ExperimentalBuildProfile[] Read(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a JSON profile file.");
        AppDataStorage.ValidatePath(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) throw new InvalidDataException("The profile file exceeds 128 KiB.");
        var document = JsonSerializer.Deserialize<ProfileDocument>(stream, Format);
        if (document is not { SchemaVersion: 1 }) throw new InvalidDataException("The profile needs schemaVersion 1.");
        return document.Profiles;
    }

    private sealed record ProfileDocument(int SchemaVersion, ExperimentalBuildProfile[] Profiles);
}
