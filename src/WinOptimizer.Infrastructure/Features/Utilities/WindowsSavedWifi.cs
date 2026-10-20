using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using WinOptimizer.Core.Features.Utilities;

namespace WinOptimizer.Infrastructure.Features.Utilities;

/// <summary>Enumerates native WLAN profiles and requests readable keys in memory, without file exports.</summary>
internal sealed class WindowsSavedWifi : ISavedWifiSource
{
    public SavedWifiSnapshot Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check(WlanOpenHandle(2, 0, out _, out var client), "Open Windows Wi-Fi service");
        try
        {
            Check(WlanEnumInterfaces(client, 0, out var interfaces), "List Wi-Fi adapters");
            try
            {
                var profiles = new List<SavedWifiProfile>();
                var diagnostics = new List<string>();
                foreach (var adapter in ReadList<InterfaceInfo>(interfaces))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { ReadProfiles(client, adapter, profiles, diagnostics, cancellationToken); }
                    catch (UtilityOperationException ex) { diagnostics.Add(adapter.Description + ": " + ex.Message); }
                }
                return new(profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(profile => profile.Adapter, StringComparer.CurrentCultureIgnoreCase).ToArray(),
                    string.Join(Environment.NewLine, diagnostics));
            }
            finally { WlanFreeMemory(interfaces); }
        }
        finally { WlanCloseHandle(client, 0); }
    }

    /// <summary>Retains individual unreadable profiles and continues reading the remaining inventory.</summary>
    private static void ReadProfiles(nint client, InterfaceInfo adapter, List<SavedWifiProfile> profiles,
        List<string> diagnostics, CancellationToken cancellationToken)
    {
        var id = adapter.Id;
        Check(WlanGetProfileList(client, ref id, 0, out var list), "List saved profiles");
        try
        {
            foreach (var profile in ReadList<ProfileInfo>(list))
            {
                cancellationToken.ThrowIfCancellationRequested();
                nint xml = 0;
                try
                {
                    uint flags = 4; // WLAN_PROFILE_GET_PLAINTEXT_KEY; Windows enforces access permissions.
                    Check(WlanGetProfile(client, ref id, profile.Name, 0, out xml, ref flags, out _), "Read saved profile");
                    profiles.Add(ParseProfile(Marshal.PtrToStringUni(xml) ?? "", profile.Name, adapter.Description));
                }
                catch (Exception ex) when (ex is UtilityOperationException or XmlException)
                {
                    profiles.Add(new(profile.Name, adapter.Description, "", "", null, WifiPasswordState.Unavailable));
                    // XML parse exceptions can contain source text, so never publish their messages.
                    diagnostics.Add($"{adapter.Description} / {profile.Name}: " +
                        (ex is XmlException ? "Windows returned an invalid Wi-Fi profile." : ex.Message));
                }
                finally { if (xml != 0) WlanFreeMemory(xml); }
            }
        }
        finally { WlanFreeMemory(list); }
    }

    /// <summary>Requires an explicit unprotected key flag; encrypted blobs never become displayed passwords.</summary>
    internal static SavedWifiProfile ParseProfile(string xml, string name, string adapter)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root;
        if (root?.Name.LocalName != "WLANProfile") throw new XmlException("Invalid WLAN profile root.");
        var ns = root.Name.Namespace;
        var security = root.Element(ns + "MSM")?.Element(ns + "security");
        var auth = security?.Element(ns + "authEncryption");
        var authentication = auth?.Element(ns + "authentication")?.Value ?? "";
        var encryption = auth?.Element(ns + "encryption")?.Value ?? "";
        var key = security?.Element(ns + "sharedKey");
        var material = key?.Element(ns + "keyMaterial")?.Value;
        var unprotected = key?.Element(ns + "protected")?.Value is "false" or "0";
        var state = unprotected && !string.IsNullOrEmpty(material) ? WifiPasswordState.Available
            : authentication == "open" && encryption == "none" ? WifiPasswordState.OpenNetwork
            : auth?.Element(ns + "useOneX")?.Value is "true" or "1" ? WifiPasswordState.NotStored
            : WifiPasswordState.Unavailable;
        return new(name, adapter, authentication, encryption, state == WifiPasswordState.Available ? material : null, state);
    }

    /// <summary>Both WLAN list structures begin with two DWORDs followed by inline native entries.</summary>
    private static IEnumerable<T> ReadList<T>(nint list) where T : struct
    {
        var count = Marshal.ReadInt32(list);
        var size = Marshal.SizeOf<T>();
        for (var i = 0; i < count; i++) yield return Marshal.PtrToStructure<T>(list + 8 + checked(i * size));
    }

    private static void Check(uint code, string operation)
    {
        if (code != 0) throw new UtilityOperationException($"{operation}: {new Win32Exception(unchecked((int)code)).Message} (Windows error {code}). Check Wi-Fi service availability and account permissions, then refresh.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InterfaceInfo
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        public uint Flags;
    }

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanOpenHandle(uint version, nint reserved, out uint negotiatedVersion, out nint client);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanCloseHandle(nint client, nint reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanEnumInterfaces(nint client, nint reserved, out nint list);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanGetProfileList(nint client, ref Guid id, nint reserved, out nint list);
    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(nint client, ref Guid id, string name, nint reserved,
        out nint xml, ref uint flags, out uint access);
    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern void WlanFreeMemory(nint memory);
}
