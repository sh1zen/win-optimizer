using System.Xml.Linq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Disks;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Runs the registered Microsoft Windows File Recovery package against two distinct local volumes.</summary>
internal static class DiskFileRecovery
{
    private const string PackageFamily = "Microsoft.WindowsFileRecovery_8wekyb3d8bbwe";

    public static async Task<IReadOnlyList<string>> RunAsync(IProcessExecutor processes, string source, string destination,
        DiskRecoveryMode mode, string? filter, CancellationToken ct)
    {
        var arguments = Arguments(source, destination, mode, filter);
        var sourceDrive = DiskPaths.Drive(source);
        DiskPaths.Drive(destination);
        if (string.Equals(DiskPaths.VolumeId(source), DiskPaths.VolumeId(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a different destination volume.");
        if (mode == DiskRecoveryMode.Regular && !sourceDrive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Regular recovery requires NTFS. Select Extensive for this volume.");

        var package = new PackageManager().FindPackagesForUser("", PackageFamily)
            .FirstOrDefault(package => !package.IsResourcePackage && package.SignatureKind == PackageSignatureKind.Store)
            ?? throw new IOException("Install Windows File Recovery from the Microsoft Store for the account running WinOptimizer, then try again.");
        var installed = package.InstalledLocation.Path;
        var executable = ResolveExecutable(installed, XDocument.Load(Path.Join(installed, "AppxManifest.xml")));
        if (!File.Exists(executable))
            throw new IOException("The Windows File Recovery executable is missing. Repair or reinstall Windows File Recovery.");
        var previous = ResultFolders(destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ct.ThrowIfCancellationRequested();

        // WinFR prompts interactively, including its confirmation and destination-full handling.
        var exit = await processes.ShellExecuteAsync(executable, arguments, waitForExit: true).ConfigureAwait(false);
        if (exit is null) throw new IOException("Windows File Recovery could not be started.");
        if (exit != 0)
            throw new IOException($"Windows File Recovery exited with code {exit}. Check the destination for any partial results.");
        return ResultFolders(destination).Where(path => !previous.Contains(path)).ToArray();
    }

    internal static string Arguments(string source, string destination, DiskRecoveryMode mode, string? filter)
    {
        if (!DiskPaths.IsDriveRoot(source) || !DiskPaths.IsDriveRoot(destination) ||
            string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose two different local volumes.");
        var option = mode switch
        {
            DiskRecoveryMode.Regular => "/regular",
            DiskRecoveryMode.Extensive => "/extensive",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var command = $"{source[..2]} {destination[..2]} {option}";
        filter = filter?.Trim();
        if (string.IsNullOrEmpty(filter)) return command;
        if (filter.Any(character => char.IsControl(character) || character is '"' or '/' or ':' or '<' or '>' or '|') ||
            filter.StartsWith(@"\\", StringComparison.Ordinal) || filter.Split('\\').Any(part => part is "." or ".."))
            throw new ArgumentException("Enter a name, wildcard or path relative to the source volume.");
        // A trailing backslash must be doubled before a closing Windows argument quote.
        var trailing = filter.Length - filter.TrimEnd('\\').Length;
        return $"{command} /n \"{filter}{new string('\\', trailing)}\"";
    }

    /// <summary>Finds the registered CLI entry point even when the Store package places it in a subdirectory.</summary>
    internal static string ResolveExecutable(string installed, XDocument manifest)
    {
        var extension = manifest.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Extension" && (string?)element.Attribute("Category") == "windows.appExecutionAlias" &&
            element.Descendants().Any(alias => alias.Name.LocalName == "ExecutionAlias" &&
                string.Equals((string?)alias.Attribute("Alias"), "winfr.exe", StringComparison.OrdinalIgnoreCase)));
        var relative = (string?)extension?.Attribute("Executable") ??
            (string?)extension?.Ancestors().FirstOrDefault(element => element.Name.LocalName == "Application")?.Attribute("Executable");
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new IOException("The installed Windows File Recovery package does not declare a valid CLI executable.");
        var executable = Path.GetFullPath(relative, installed);
        var withinPackage = Path.GetRelativePath(installed, executable);
        if (Path.IsPathRooted(withinPackage) || withinPackage.StartsWith(@"..\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The installed Windows File Recovery package declares an invalid CLI executable.");
        return executable;
    }

    private static IEnumerable<string> ResultFolders(string root) =>
        Directory.EnumerateDirectories(root, "Recovery_*")
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
}
