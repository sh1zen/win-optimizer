using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WinOptimizer.Infrastructure.Features.Common.Utilities;

/// <summary>Reads the stored Shell Link target without resolving or activating the shortcut.</summary>
internal static class WindowsShellLink
{
    public static string ReadRawTarget(string path)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
        var link = Activator.CreateInstance(type)!;
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(32768);
            // SLGP_RAWPATH preserves environment variables for the caller's account context.
            ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 4);
            return target.ToString();
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    // GetPath is the first IShellLinkW method; no other interface slots are invoked.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
    }
}
