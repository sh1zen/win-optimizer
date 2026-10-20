using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Customize.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Customize.Services;

/// <summary>Owns an explicitly opened Classes hive; callers dispose it after a single registry operation.</summary>
internal sealed record ContextMenuRegistryRoot(string Id, RegistryKey Key);

/// <summary>Reads static verbs and COM context-menu handlers without loading or executing registered programs.</summary>
public sealed class WindowsContextMenuStore : IContextMenuStore
{
    private const string DisabledValue = "LegacyDisable";
    private readonly Func<IReadOnlyList<ContextMenuRegistryRoot>> _openRoots;
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _programNames = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Registration(string RootId, string Path, bool Handler, string Evidence, ContextMenuEntry Entry);

    public WindowsContextMenuStore(IInteractiveUserService user) : this(() => OpenRoots(user)) { }
    internal WindowsContextMenuStore(Func<IReadOnlyList<ContextMenuRegistryRoot>> openRoots) => _openRoots = openRoots;

    /// <summary>Enumerates each physical scope/view separately, retaining exact registry evidence for edits.</summary>
    public IReadOnlyList<ContextMenuEntry> Read()
    {
        _registrations.Clear();
        _programNames.Clear();
        var roots = _openRoots();
        try
        {
            foreach (var root in roots)
            {
                foreach (var type in root.Key.GetSubKeyNames())
                {
                    ReadType(root, type, roots);
                    if (!type.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase)) continue;
                    using var associations = root.Key.OpenSubKey(type);
                    if (associations != null)
                        foreach (var association in associations.GetSubKeyNames()) ReadType(root, type + "\\" + association, roots);
                }
                ReadType(root, @"Directory\Background", roots);
            }
            return _registrations.Values.Select(value => value.Entry)
                .OrderBy(entry => entry.ProgramName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        }
        finally { foreach (var root in roots) root.Key.Dispose(); }
    }

    private void ReadType(ContextMenuRegistryRoot root, string type, IReadOnlyList<ContextMenuRegistryRoot> roots)
    {
        foreach (bool handler in new[] { false, true })
        {
            var parent = type + (handler ? @"\shellex\ContextMenuHandlers" : @"\shell");
            using var key = root.Key.OpenSubKey(parent);
            if (key == null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                var path = parent + "\\" + name;
                using var verb = root.Key.OpenSubKey(path);
                if (verb == null) continue;
                var registration = ReadRegistration(root, verb, path, handler, roots);
                if (registration != null) _registrations[registration.Entry.Id] = registration;
            }
        }
    }

    /// <summary>Leaves hidden/programmatic verbs and unidentified commands outside this toggle editor.</summary>
    private Registration? ReadRegistration(ContextMenuRegistryRoot root, RegistryKey key, string path, bool handler,
        IReadOnlyList<ContextMenuRegistryRoot> roots)
    {
        string target;
        string raw;
        bool enabled;
        var leaf = path[(path.LastIndexOf('\\') + 1)..];
        var label = StringValue(key, "MUIVerb") ?? StringValue(key, "") ?? leaf;
        if (handler)
        {
            raw = StringValue(key, "") ?? leaf;
            enabled = !raw.StartsWith('-');
            if (!Guid.TryParse(enabled ? raw : raw[1..], out var clsid)) return null;
            using var clsidKey = OpenClassId(roots, clsid);
            using var server = clsidKey?.OpenSubKey("InprocServer32");
            target = StringValue(server, "") ?? "";
            label = StringValue(clsidKey, "") ?? leaf;
        }
        else
        {
            // These registrations have independent visibility rules that a simple toggle cannot reverse.
            if (key.GetValueNames().Contains("ProgrammaticAccessOnly", StringComparer.OrdinalIgnoreCase)) return null;
            using var command = key.OpenSubKey("command");
            raw = StringValue(command, "") ?? "";
            target = raw;
            enabled = !key.GetValueNames().Contains(DisabledValue, StringComparer.OrdinalIgnoreCase);
        }
        var executable = ExecutablePath(target);
        if (executable.Length == 0) return null;
        var program = ProgramName(executable);
        if (string.IsNullOrWhiteSpace(program)) return null;
        if (label.StartsWith('@') || Guid.TryParse(label, out _)) label = leaf;
        var id = root.Id + "\\" + path;
        // Include the complete value set relevant to identity/state, including original registry types.
        var evidence = raw + "\0" + target + "\0" + string.Join("\0", key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => name + ":" + key.GetValueKind(name) + ":" + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)))));
        return new(root.Id, path, handler, evidence,
            new ContextMenuEntry(id, program, label.Replace("&", ""), root.Id + "\\" + path, enabled)
            {
                ProgramPath = executable,
                TargetType = path[..path.IndexOf(handler ? @"\shellex\" : @"\shell\", StringComparison.OrdinalIgnoreCase)],
                IsMachineWide = root.Id.StartsWith("Machine", StringComparison.Ordinal),
            });
    }

    /// <summary>Rechecks discovery evidence immediately before changing only the visibility value and verifying readback.</summary>
    public ContextMenuEntry SetEnabled(ContextMenuEntry original, bool enabled)
    {
        if (!_registrations.TryGetValue(original.Id, out var expected) || expected.Entry != original)
            throw new InvalidOperationException("Refresh the context menu inventory before editing this entry.");
        var roots = _openRoots();
        try
        {
            var root = roots.SingleOrDefault(root => root.Id == expected.RootId)
                ?? throw new IOException("The registration's registry scope is unavailable. Refresh and retry.");
            using var key = root.Key.OpenSubKey(expected.Path, writable: true)
                ?? throw new IOException("The context menu registration was removed. Refresh and retry.");
            var current = ReadRegistration(root, key, expected.Path, expected.Handler, roots);
            if (current != expected)
                throw new InvalidOperationException("The context menu registration changed outside this editor. Refresh and retry.");
            if (original.IsEnabled == enabled) return original;
            if (expected.Handler)
            {
                var value = StringValue(key, "") ?? expected.Path[(expected.Path.LastIndexOf('\\') + 1)..];
                var kind = key.GetValueNames().Contains("") ? key.GetValueKind("") : RegistryValueKind.String;
                key.SetValue("", enabled ? value[1..] : "-" + value, kind);
            }
            else if (enabled) key.DeleteValue(DisabledValue, throwOnMissingValue: true);
            else key.SetValue(DisabledValue, "", RegistryValueKind.String);
            key.Flush();
            var updated = ReadRegistration(root, key, expected.Path, expected.Handler, roots);
            if (updated == null || updated.Entry.IsEnabled != enabled)
                throw new IOException("Windows did not retain the requested context menu state. Refresh and retry.");
            _registrations[original.Id] = updated;
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return updated.Entry;
        }
        finally { foreach (var root in roots) root.Key.Dispose(); }
    }

    private static string? StringValue(RegistryKey? key, string name) =>
        key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    /// <summary>Reads a program's display metadata once per inventory and avoids probing network registrations.</summary>
    private string ProgramName(string executable)
    {
        if (_programNames.TryGetValue(executable, out var name)) return name;
        name = Path.GetFileNameWithoutExtension(executable);
        if (!executable.StartsWith(@"\\", StringComparison.Ordinal) && File.Exists(executable))
        {
            var product = FileVersionInfo.GetVersionInfo(executable).ProductName;
            if (!string.IsNullOrWhiteSpace(product)) name = product;
        }
        return _programNames[executable] = name;
    }

    /// <summary>Uses interactive-user COM registrations before the machine catalogue, matching Explorer's merged view.</summary>
    private static RegistryKey? OpenClassId(IReadOnlyList<ContextMenuRegistryRoot> roots, Guid clsid)
    {
        var path = @"CLSID\" + clsid.ToString("B");
        foreach (var candidate in roots)
        {
            var key = candidate.Key.OpenSubKey(path);
            if (key != null) return key;
        }
        return null;
    }

    /// <summary>Extracts a fully qualified executable/DLL from a quoted or unquoted registration without invoking it.</summary>
    internal static string ExecutablePath(string command)
    {
        var value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            value = end > 1 ? value[1..end] : "";
        }
        else
        {
            var end = new[] { ".exe", ".dll" }.Select(ext => value.IndexOf(ext, StringComparison.OrdinalIgnoreCase))
                .Where(index => index >= 0).DefaultIfEmpty(-1).Min();
            value = end < 0 ? "" : value[..(end + 4)];
        }
        return Path.IsPathFullyQualified(value) && (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ? value : "";
    }

    private static IReadOnlyList<ContextMenuRegistryRoot> OpenRoots(IInteractiveUserService user)
    {
        var roots = new List<ContextMenuRegistryRoot>();
        try
        {
            if (user.IsOtsElevation && string.IsNullOrWhiteSpace(user.InteractiveUserSid))
                throw new InvalidOperationException("The interactive user's registry identity is unavailable.");
            var userKey = user.IsOtsElevation
                ? Registry.Users.OpenSubKey(user.InteractiveUserSid + @"\Software\Classes")
                : Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            if (userKey != null) roots.Add(new("User", userKey));
            // Classes menu keys are shared across WOW64 views. Enumerate once and resolve COM in native Explorer's view.
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            var classes = machine.OpenSubKey(@"Software\Classes");
            if (classes != null) roots.Add(new("Machine (" + (view == RegistryView.Registry64 ? "64" : "32") + "-bit)", classes));
            return roots;
        }
        catch { foreach (var root in roots) root.Key.Dispose(); throw; }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
