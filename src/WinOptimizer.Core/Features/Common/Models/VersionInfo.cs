using System;

namespace WinOptimizer.Core.Features.Common.Models;

/// <summary>Describes a published release identified by a date-based version tag.</summary>
public sealed record VersionInfo
{
    public string Version { get; init; } = string.Empty;
    public DateTime ReleaseDate { get; init; }
    public bool IsUpdateAvailable { get; init; }

    /// <summary>Parses a stable version tag in vYY.MM.DD format.</summary>
    public static VersionInfo FromTag(string tag)
    {
        if (string.IsNullOrEmpty(tag) || !tag.StartsWith("v"))
            return new VersionInfo();

        string versionString = tag.Substring(1); // Remove 'v' prefix

        string[] parts = versionString.Split('.');

        if (parts.Length != 3)
            return new VersionInfo();

        if (!int.TryParse(parts[0], out int year) ||
            !int.TryParse(parts[1], out int month) ||
            !int.TryParse(parts[2], out int day))
            return new VersionInfo();

        // Construct a date from the version components
        DateTime releaseDate;
        try
        {
            releaseDate = new DateTime(2000 + year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Invalid date components (e.g., month 13, day 32)
            return new VersionInfo();
        }

        return new VersionInfo
        {
            Version = tag,
            ReleaseDate = releaseDate
        };
    }

    public bool IsNewerThan(VersionInfo other)
    {
        if (other == null)
            return true;

        return ReleaseDate > other.ReleaseDate;
    }

    public override string ToString()
    {
        return Version;
    }
}
