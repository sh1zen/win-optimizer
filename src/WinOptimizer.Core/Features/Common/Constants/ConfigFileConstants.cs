using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinOptimizer.Core.Features.Common.Constants;

public static class ConfigFileConstants
{
    public const string CurrentVersion = "2.0";
    public const string FileExtension = ".win-optimizer";
    public const string FileFilter = "win-optimizer Configuration Files";
    public const string FilePattern = "*.win-optimizer";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
