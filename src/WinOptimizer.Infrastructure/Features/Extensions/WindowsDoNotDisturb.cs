using System.Runtime.InteropServices;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Reads and changes the shell's manually selected notification profile.</summary>
internal static class WindowsDoNotDisturb
{
    private static readonly Guid ClassId = new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");
    private static readonly Guid InterfaceId = typeof(IQuietHoursSettings).GUID;
    private const uint LocalServer = 4;
    private const string Off = "Microsoft.QuietHoursProfile.Unrestricted";
    private const string Priority = "Microsoft.QuietHoursProfile.PriorityOnly";
    private const string Alarms = "Microsoft.QuietHoursProfile.AlarmsOnly";

    internal static bool? Read() => AccessProfile(null);

    internal static void Set(bool enabled) => AccessProfile(enabled);

    /// <summary>Keeps the shell COM object inside the worker's COM apartment.</summary>
    private static bool? AccessProfile(bool? requested)
    {
        CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        try
        {
            CoCreateInstance(in ClassId, 0, LocalServer, in InterfaceId, out var settings);
            try
            {
                var current = Read(settings);
                if (requested is not bool enabled || current == enabled) return current;
                if (current is null) throw new InvalidOperationException("The Windows notification profile is unavailable.");
                settings.SetUserSelectedProfile(enabled ? Priority : Off);
                var confirmed = Read(settings);
                if (confirmed != enabled) throw new IOException("Windows did not confirm the notification profile change.");
                return confirmed;
            }
            finally { Marshal.ReleaseComObject(settings); }
        }
        finally { CoUninitialize(); }
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    private static bool? Read(IQuietHoursSettings settings)
    {
        settings.GetUserSelectedProfile(out var profile);
        try
        {
            return Marshal.PtrToStringUni(profile) switch
            {
                Off => false,
                Priority or Alarms => true,
                _ => null
            };
        }
        finally { Marshal.FreeCoTaskMem(profile); }
    }

    // The shell exposes this interface through its local COM server.
    [ComImport, Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        void GetUserSelectedProfile(out nint profile);
        void SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profile);
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IQuietHoursSettings settings);
}
