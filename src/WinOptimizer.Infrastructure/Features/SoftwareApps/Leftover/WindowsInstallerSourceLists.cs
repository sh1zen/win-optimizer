using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;

/// <summary>Read-only MSI source enumeration and verification of missing product source-list metadata.</summary>
internal interface IInstallerSourceLists
{
    uint Enumerate(string id, string? sid, uint context, uint options, uint index, StringBuilder source, ref uint length);
    bool IsProductSourceListAbsent(string product, string? sid, uint context);
}

/// <summary>Distinguishes a registered product without a source list from an inaccessible or damaged source list.</summary>
internal sealed class WindowsInstallerSourceLists : IInstallerSourceLists
{
    public uint Enumerate(string id, string? sid, uint context, uint options, uint index, StringBuilder source, ref uint length) =>
        MsiSourceListEnumSources(id, sid, context, options, index, source, ref length);

    public bool IsProductSourceListAbsent(string product, string? sid, uint context)
    {
        if (context != 4 && sid == null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value ?? throw new IOException("The MSI source-list account could not be identified.");
        }
        var (hive, path) = ProductRegistration(product, sid, context);
        bool registered = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var registration = root.OpenSubKey(path, writable: false);
            if (registration == null) continue;
            registered = true;
            using var sources = registration.OpenSubKey("SourceList", writable: false);
            if (sources != null) return false;
        }
        // A missing registration can indicate an inventory race or an unavailable user hive, not an empty source list.
        return registered;
    }

    /// <summary>Uses MSI's packed product code and the enumerated account/context rather than merged HKCR or the process user.</summary>
    internal static (RegistryHive Hive, string Path) ProductRegistration(string product, string? sid, uint context)
    {
        if (!Guid.TryParseExact(product, "B", out var guid)) throw new IOException("The MSI product code is invalid.");
        var code = guid.ToString("N").ToUpperInvariant().ToCharArray();
        Array.Reverse(code, 0, 8); Array.Reverse(code, 8, 4); Array.Reverse(code, 12, 4);
        for (int index = 16; index < code.Length; index += 2) (code[index], code[index + 1]) = (code[index + 1], code[index]);
        var packed = new string(code);
        if (context == 4 && sid == null) return (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\Products\" + packed);
        if (sid == null) throw new IOException("The MSI source-list account SID is missing.");
        try { sid = new SecurityIdentifier(sid).Value; }
        catch (ArgumentException ex) { throw new IOException("The MSI source-list account SID is invalid.", ex); }
        return context switch
        {
            1 => (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed\" + sid + @"\Installer\Products\" + packed),
            2 => (RegistryHive.Users, sid + @"\Software\Microsoft\Installer\Products\" + packed),
            _ => throw new IOException("The MSI source-list installation context is invalid.")
        };
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSourceListEnumSourcesW", ExactSpelling = true)]
    private static extern uint MsiSourceListEnumSources(string id, string? sid, uint context, uint options, uint index,
        StringBuilder source, ref uint length);
}
