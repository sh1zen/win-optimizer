using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace WinOptimizer.UI.Features.Extensions;

/// <summary>Publishes the actual SysMonitor window state within one installation and Windows session.</summary>
internal static class SystemMonitorAvailability
{
    private static string EventName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var scope = $"{identity.User}|{Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).ToUpperInvariant()}";
        return $@"Local\WinOptimizer.SysMonitor.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))}";
    }

    public static EventWaitHandle Create() => new(false, EventResetMode.ManualReset, EventName());

    public static bool IsRunning()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(), out var readiness)) return false;
        using (readiness) return readiness.WaitOne(0);
    }
}
