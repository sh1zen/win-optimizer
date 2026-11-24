using System.Text.Json.Serialization;
using WinOptimizer.Infrastructure.Features.Extensions;

namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Saved desktop items, Overview configuration and the place used for weather requests.</summary>
internal sealed record NotchContent(NotchShortcut[] Shortcuts, DesktopWeatherLocation? WeatherLocation = null)
{
    internal const int MaximumShortcuts = 12;
    internal static NotchContent Empty { get; } = new([]);
    public bool? OverviewEnabled { get; init; }
    public NotchView[] OverviewTiles { get; init; } = NotchOverviewLayout.Default();

    internal NotchContent Normalize()
    {
        var location = WeatherLocation is { IsValid: true } ? WeatherLocation : null;
        return new((Shortcuts ?? []).Where(item => item is not null && (item.AppName is null
                ? IsLocalPath(item.Path)
                : !item.IsFolder && !string.IsNullOrWhiteSpace(item.AppName) && WindowsDesktopApplications.IsTarget(item.Path)))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(MaximumShortcuts).ToArray(), location)
        {
            OverviewEnabled = OverviewEnabled,
            OverviewTiles = NotchOverviewLayout.Normalize(OverviewTiles)
        };
    }

    internal static bool IsLocalPath(string? path) => path is { Length: > 2 } &&
        char.IsAsciiLetter(path[0]) && path[1] == ':' && Path.IsPathFullyQualified(path) &&
        path.AsSpan(2).IndexOfAny('\0', ':') < 0;
}

/// <summary>A shortcut stores a shell target and its basic presentation metadata.</summary>
public sealed record NotchShortcut(string Path, bool IsFolder)
{
    /// <summary>The display name of an AppsFolder entry. Local pins derive their names from their paths.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppName { get; init; }

    public string Name
    {
        get
        {
            if (AppName is { } appName) return appName;
            var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
            if (name.Length == 0) return Path;
            return !IsFolder && name.Length > 4 && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }

    public string Glyph => AppName is not null ? "\uECAA" : IsFolder ? "\uE8B7" : "\uE7C3";
}
