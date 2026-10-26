using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Services;

/// <summary>Shares account-aware application-path expansion across discovery, removal and local metadata.</summary>
internal static class AppPaths
{
    /// <summary>Resolves a configured portable-app path without substituting the elevated account for a missing user folder.</summary>
    internal static string ResolveDetectionPath(string path, IInteractiveUserService user)
    {
        var expanded = Expand(path, folder =>
        {
            var root = user.GetInteractiveUserFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root) || !CleanerPaths.IsLocalPath(root) || root.Contains('%'))
                throw new IOException($"The interactive account's {folder} folder is unavailable.");
            return root;
        });
        if (!CleanerPaths.IsLocalPath(expanded) || expanded.Contains('%') ||
            expanded[3..].Contains(':') || expanded.IndexOfAny(['*', '?', '"', '\0', '\r', '\n']) >= 0)
            throw new IOException("The application detection path is not a resolved local path.");
        return Path.GetFullPath(expanded);
    }

    /// <summary>Normalizes documented Known Folder identifiers and user variables through the supplied account boundary.</summary>
    internal static string Expand(string path, Func<Environment.SpecialFolder, string> resolveFolder)
    {
        foreach (var (variable, folder) in new[]
        {
            ("%LOCALAPPDATA%", Environment.SpecialFolder.LocalApplicationData),
            ("%APPDATA%", Environment.SpecialFolder.ApplicationData),
            ("%USERPROFILE%", Environment.SpecialFolder.UserProfile),
        })
        {
            if (path.Contains(variable, StringComparison.OrdinalIgnoreCase))
                path = path.Replace(variable, resolveFolder(folder), StringComparison.OrdinalIgnoreCase);
        }
        if (path.Length > 38 && path[38] == '\\' && Guid.TryParse(path.AsSpan(0, 38), out var id))
        {
            Environment.SpecialFolder? folder = id.ToString("D").ToUpperInvariant() switch
            {
                "905E63B6-C1BF-494E-B29C-65B732D3D21A" or "6D809377-6AF0-444B-8957-A3773F02200E" => Environment.SpecialFolder.ProgramFiles,
                "7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E" => Environment.SpecialFolder.ProgramFilesX86,
                "F1B32785-6FBA-4FCF-9D55-7B8E7F157091" => Environment.SpecialFolder.LocalApplicationData,
                "3EB685DB-65F9-4CF6-A03A-E3EF65729F3D" => Environment.SpecialFolder.ApplicationData,
                "5E6C858F-0E22-4760-9AFE-EA3317B67173" => Environment.SpecialFolder.UserProfile,
                "F38BF404-1D43-42F2-9305-67DE0B28FC23" => Environment.SpecialFolder.Windows,
                "1AC14E77-02E7-4E5D-B744-2EB1AE5198B7" => Environment.SpecialFolder.System,
                _ => null,
            };
            if (folder is { } known && resolveFolder(known) is { Length: > 0 } root)
                path = root.TrimEnd('\\') + path[38..];
        }
        return Environment.ExpandEnvironmentVariables(path);
    }
}
