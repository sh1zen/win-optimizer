using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Core.Features.AdvancedTools;

/// <summary>Checks the files required by the Windows installation media workflow.</summary>
public static class WindowsSetupMedia
{
    public static bool HasRequiredFiles(IFileSystemService files, string directory)
    {
        var sources = files.CombinePath(directory, "sources");
        return files.DirectoryExists(files.CombinePath(directory, "boot")) &&
            HasContent(files.CombinePath(directory, "setup.exe")) &&
            HasContent(files.CombinePath(sources, "boot.wim")) &&
            (HasContent(files.CombinePath(sources, "install.wim")) ||
             HasContent(files.CombinePath(sources, "install.esd")));

        bool HasContent(string path) => files.FileExists(path) && files.GetFileSize(path) > 0;
    }
}
