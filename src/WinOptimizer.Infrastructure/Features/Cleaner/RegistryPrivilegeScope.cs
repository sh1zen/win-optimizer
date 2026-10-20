using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WinOptimizer.Infrastructure.Features.Cleaner;

/// <summary>Enables a registry privilege for a synchronous native operation and restores its previous token state.</summary>
internal sealed class RegistryPrivilegeScope : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    private readonly WindowsIdentity _identity;
    private TokenPrivileges _previous;
    public RegistryPrivilegeScope(string name)
    {
        _identity = WindowsIdentity.GetCurrent(TokenAccessLevels.AdjustPrivileges | TokenAccessLevels.Query);
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(_identity.AccessToken, false, ref requested, Marshal.SizeOf<TokenPrivileges>(), out _previous, out _) || Marshal.GetLastWin32Error() == 1300)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Run win-optimizer as administrator to enable {name}.");
        }
        catch { _identity.Dispose(); throw; }
    }
    public void Dispose()
    {
        try
        {
            if (!RestoreTokenPrivileges(_identity.AccessToken, false, ref _previous, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to restore token privileges.");
        }
        finally { _identity.Dispose(); }
    }
    [DllImport("advapi32.dll", EntryPoint="LookupPrivilegeValueW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges requested, int length, out TokenPrivileges previous, out int returnLength);
    [DllImport("advapi32.dll", EntryPoint="AdjustTokenPrivileges", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges requested, int length, IntPtr previous, IntPtr returnLength);
}
