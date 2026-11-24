using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>An application advertised by the Windows Start menu.</summary>
public sealed record DesktopApplication(string Target, string Name);

public interface IDesktopApplications
{
    Task<IReadOnlyList<DesktopApplication>> ReadAsync(CancellationToken cancellationToken);
    Task<SoftwareBitmap?> ReadIconAsync(string target, CancellationToken cancellationToken);
}

/// <summary>Reads Windows AppsFolder entries and their registered icons on a worker.</summary>
public sealed class WindowsDesktopApplications : IDesktopApplications
{
    internal const string Folder = "shell:AppsFolder";
    private const string Prefix = Folder + "\\";

    public Task<IReadOnlyList<DesktopApplication>> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    public Task<SoftwareBitmap?> ReadIconAsync(string target, CancellationToken cancellationToken) =>
        IsTarget(target) ? WindowsPreviewIcon.ReadShellItemAsync(target, cancellationToken) :
            throw new ArgumentException("A Windows AppsFolder item is required.", nameof(target));

    /// <summary>Accepts a single AppsFolder item ID or the absolute local path used as its ID.</summary>
    public static bool IsTarget(string? target)
    {
        if (target is null || !target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var id = target[Prefix.Length..];
        if (id.Length == 0 || id != id.Trim() || id.Any(char.IsControl) || id.Contains('"')) return false;
        if (id.IndexOfAny(['\\', '/', ':']) < 0) return id is not ("." or "..");
        return id.Length > 3 && char.IsAsciiLetter(id[0]) && id[1] == ':' && id[2] == '\\' &&
            id.AsSpan(2).IndexOfAny(':', '/') < 0 &&
            !id.Split('\\').Any(part => part is "." or "..");
    }

    private static IReadOnlyList<DesktopApplication> Read(CancellationToken token)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
            shell = Activator.CreateInstance(shellType)!;
            folder = ((dynamic)shell).NameSpace(Folder);
            if (folder is null) throw new InvalidOperationException("Windows AppsFolder is unavailable.");
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            var apps = new Dictionary<string, DesktopApplication>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                object? item = ((dynamic)items).Item(index);
                if (item is null) continue;
                try
                {
                    string? name = ((dynamic)item).Name;
                    string? id = ((dynamic)item).Path;
                    var target = Prefix + id;
                    if (!string.IsNullOrWhiteSpace(name) && IsTarget(target))
                        apps.TryAdd(target, new DesktopApplication(target, name));
                }
                finally { Release(item); }
            }
            return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
