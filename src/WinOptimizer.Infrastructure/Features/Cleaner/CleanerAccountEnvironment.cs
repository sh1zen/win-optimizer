using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Resolves file and registry reference variables in their owning account without borrowing another user's environment.</summary>
internal static class CleanerAccountEnvironment
{
    public static string Expand(string text, RegistryView view, ICleanerRegistrySession account) => Regex.Replace(text, "%[^%]+%", match =>
    {
        var variable = match.Value.ToUpperInvariant();
        return variable switch
        {
            "%USERPROFILE%" => account.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "%LOCALAPPDATA%" => account.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "%APPDATA%" => account.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "%PROGRAMFILES%" when view == RegistryView.Registry32 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "%SYSTEMROOT%" or "%WINDIR%" or "%SYSTEMDRIVE%" or "%PROGRAMFILES%" or "%PROGRAMFILES(X86)%" or "%PROGRAMW6432%" or "%PROGRAMDATA%" or "%ALLUSERSPROFILE%" or "%PUBLIC%" => Environment.ExpandEnvironmentVariables(match.Value),
            _ => account.IsInteractive ? Environment.ExpandEnvironmentVariables(match.Value) : match.Value
        };
    });
}
