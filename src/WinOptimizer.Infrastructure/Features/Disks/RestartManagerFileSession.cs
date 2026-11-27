using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using WinOptimizer.Core.Features.Disks;

namespace WinOptimizer.Infrastructure.Features.Disks;

/// <summary>Owns one file's lock query and the lifetime of its shutdown session.</summary>
internal interface IFileLockSession : IDisposable
{
    IReadOnlyList<DiskProcess> GetLocks();
    void ForceShutdown();
}

/// <summary>Registers one file with Windows Restart Manager to identify or close locking applications.</summary>
internal sealed class RestartManagerFileSession : IFileLockSession
{
    private const int MaxApplicationName = 255;
    private const int MaxServiceName = 63;
    private readonly uint _session;

    public RestartManagerFileSession(string file)
    {
        Check(RmStartSession(out _session, 0, new StringBuilder(33)));
        var result = RmRegisterResources(_session, 1, [file], 0, IntPtr.Zero, 0, IntPtr.Zero);
        if (result == 0) return;
        Dispose();
        Check(result);
    }

    public IReadOnlyList<DiskProcess> GetLocks()
    {
        const int moreData = 234;
        uint count = 0;
        RestartProcess[]? buffer = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var result = RmGetList(_session, out var needed, ref count, buffer, out _);
            if (result == 0)
                return buffer?.Take((int)count).Select(process => new DiskProcess(process.Identity.Id,
                    string.IsNullOrWhiteSpace(process.Service) ? process.Application : process.Service)).ToArray() ?? [];
            if (result != moreData) Check(result);
            count = needed;
            buffer = new RestartProcess[count];
        }
        throw new IOException("The processes holding this file changed repeatedly. Try again.");
    }

    public void ForceShutdown() => Check(RmShutdown(_session, 1, IntPtr.Zero));

    public void Dispose() => RmEndSession(_session);

    private static void Check(int code)
    {
        if (code != 0) throw new Win32Exception(code);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessIdentity
    {
        public int Id;
        public FILETIME Start;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestartProcess
    {
        public ProcessIdentity Identity;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxApplicationName + 1)] public string Application;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxServiceName + 1)] public string Service;
        public uint Type, Status, Session, Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
        uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] RestartProcess[]? processes, out uint reasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint session, uint flags, IntPtr status);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);
}
