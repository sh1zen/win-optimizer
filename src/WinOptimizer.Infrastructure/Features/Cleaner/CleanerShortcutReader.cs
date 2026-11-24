using WinOptimizer.Infrastructure.Features.Common.Utilities;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Reads shortcut evidence without expanding another account's variables in the calling process.</summary>
public interface ICleanerShortcutReader { string ReadTarget(string path); }

public sealed class WindowsCleanerShortcutReader : ICleanerShortcutReader
{
    public string ReadTarget(string path) => WindowsShellLink.ReadRawTarget(path);
}
