using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinOptimizer.Core.Features.Common.Native;
using WinOptimizer.Core.Features.Extensions;

namespace WinOptimizer.Infrastructure.Features.Extensions;

/// <summary>Asks the current desktop's Explorer to launch a non-elevated companion.</summary>
public sealed class DesktopExtensionProcess
{
    public static DesktopExtensionProcess Preview { get; } = new("Preview", "win-optimizer-preview.exe", "WinOptimizer.Preview");
    public static DesktopExtensionProcess Widgets { get; } = new("Desktop widgets", DesktopWidgetLaunch.Executable, null);

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string? _runValue;
    public string Name { get; }
    public string Executable { get; }

    private DesktopExtensionProcess(string name, string executable, string? runValue)
    {
        Name = name;
        Executable = executable;
        _runValue = runValue;
    }

    internal void OpenSettingsWorker(Guid connection, string? startLanguage)
    {
        var arguments = $"{DesktopExtensionSettingsChannel.WorkerArgument} {connection:N}";
        if (startLanguage is not null) arguments += $" \"{CultureInfo.GetCultureInfo(startLanguage).Name}\"";
        Launch(arguments);
    }

    private void Launch(string arguments)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, Executable);
        if (!File.Exists(executable)) throw new FileNotFoundException($"{Name} executable is missing.", executable);
        OpenInDesktopShell(executable, arguments, AppContext.BaseDirectory, Name, 0);
    }

    /// <summary>Opens an explicit local path through the interactive user's Explorer.</summary>
    public static void OpenDesktopItem(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: 3 } root ||
            !char.IsAsciiLetter(root[0]) || root[1] != ':')
            throw new ArgumentException("A fully qualified local file or folder is required.", nameof(path));
        OpenInDesktopShell(Path.GetFullPath(path), "", "", "the selected item", 1);
    }

    /// <summary>Opens a pinned local item or Windows application through the interactive Explorer.</summary>
    public static void OpenDesktopShortcut(string target)
    {
        if (WindowsDesktopApplications.IsTarget(target))
            OpenInDesktopShell(target, "", "", "the selected application", 1);
        else
            OpenDesktopItem(target);
    }

    private static void OpenInDesktopShell(string path, string arguments, string directory, string name, int show)
    {
        var (shellId, _) = GetDesktopProcess();

        object? windows = null;
        object? desktop = null;
        object? view = null;
        object? application = null;
        try
        {
            // Use the automation object hosted by the desktop itself, not a new Shell.Application object.
            var shellWindows = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            windows = Activator.CreateInstance(shellWindows)!;
            const int desktopClass = 8, needDispatch = 1;
            desktop = ((dynamic)windows).FindWindowSW(0, 0, desktopClass, out int desktopWindow, needDispatch);
            if (desktop is null || GetWindowThreadProcessId(desktopWindow, out var desktopId) == 0 || desktopId != shellId)
                throw new InvalidOperationException("The Windows desktop is not available in Explorer.");
            view = ((dynamic)desktop).Document;
            application = ((dynamic)view!).Application;
            ((dynamic)application!).ShellExecute(path, arguments, directory, "open", show);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"Explorer could not start {name} (0x{ex.HResult:X8}): {ex.Message}", ex);
        }
        finally
        {
            Release(application);
            Release(view);
            Release(desktop);
            Release(windows);
        }
    }

    internal static SecurityIdentifier DesktopUser()
    {
        var (shellId, sessionId) = GetDesktopProcess();
        // Session services report the shell's SID even when the main window used another administrator account.
        if (!WTSEnumerateProcesses(0, 0, 1, out var entries, out var count))
            throw LastError("Unable to identify the desktop account.");
        try
        {
            var size = Marshal.SizeOf<SessionProcess>();
            for (var index = 0; index < count; index++)
            {
                var entry = Marshal.PtrToStructure<SessionProcess>(entries + index * size);
                if (entry.ProcessId == shellId && entry.SessionId == sessionId && entry.UserSid != 0)
                    return new SecurityIdentifier(entry.UserSid);
            }
        }
        finally { UserTokenApi.WTSFreeMemory(entries); }
        throw new InvalidOperationException("Unable to identify the account running the Windows desktop.");
    }

    private static (uint ProcessId, uint SessionId) GetDesktopProcess()
    {
        var shell = GetShellWindow();
        if (shell == 0 || GetWindowThreadProcessId(shell, out uint shellId) == 0 ||
            !UserTokenApi.ProcessIdToSessionId(shellId, out uint shellSession) ||
            !UserTokenApi.ProcessIdToSessionId((uint)Environment.ProcessId, out uint ownSession) ||
            shellSession != ownSession || shellSession == 0)
            throw new InvalidOperationException("An interactive Windows desktop in this session is required.");

        return (shellId, shellSession);
    }

    /// <summary>Checks the actual process token before any desktop helper initialization or document access.</summary>
    public static void RequireNonElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        RequireMediumToken(identity.AccessToken);
    }

    /// <summary>Requests graceful exit from this installation's companions before replacing their files.</summary>
    public static Task StopForUpdateAsync() => Task.WhenAll(Preview.StopAsync(), Widgets.StopAsync());
    public static Task StopWidgetsAsync() => Widgets.StopAsync();

    public const string ShowMonitorArgument = "--show-sysmonitor";
    public const string StopWidgetsArgument = "--stop-widgets";
    public const string OpenMonitorArgument = "--open-sysmonitor";
    public const string OpenNotchArgument = "--open-notch";

    /// <summary>Starts a widget with the elevated token already used by the interactive application.</summary>
    public static void LaunchWidgets(bool startup = false, bool showMonitor = false) =>
        StartElevated(Widgets.Executable, startup ? DesktopWidgetLaunch.StartupArgument : showMonitor ? ShowMonitorArgument : "");

    public static void OpenWidgetSettings(bool notch) =>
        StartElevated("win-optimizer.exe", notch ? OpenNotchArgument : OpenMonitorArgument);

    public static bool AreWidgetsRunning()
    {
        using var current = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Widgets.Executable));
        try { return candidates.Any(candidate => Widgets.IsOwnProcess(candidate, current.SessionId)); }
        finally { foreach (var candidate in candidates) candidate.Dispose(); }
    }

    private static void StartElevated(string name, string arguments)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, name);
        if (!File.Exists(executable)) throw new FileNotFoundException($"The application executable {name} is missing.", executable);
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false })
            ?? throw new InvalidOperationException($"Unable to start {name}.");
    }

    private bool IsOwnProcess(Process candidate, int sessionId)
    {
        try
        {
            return !candidate.HasExited && candidate.SessionId == sessionId && string.Equals(candidate.MainModule?.FileName,
                Path.Combine(AppContext.BaseDirectory, Executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException) when (candidate.HasExited) { return false; }
    }

    private async Task StopAsync()
    {
        using var current = Process.GetCurrentProcess();
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var candidate in candidates)
            {
                if (!IsOwnProcess(candidate, current.SessionId)) continue;
                if (ReferenceEquals(this, Widgets)) StartElevated(Executable, StopWidgetsArgument);
                else await PreviewExtensionSettings.StopAsync().ConfigureAwait(false);
                await candidate.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        { throw new TimeoutException($"{Name} has not exited. Try the update again after it stops."); }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static void RequireMediumToken(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 20, out var elevated, sizeof(uint), out _))
            throw LastError("Unable to verify token elevation.");
        if (!GetTokenInformation(token, 26, out var uiAccess, sizeof(uint), out _))
            throw LastError("Unable to verify token UI access.");
        _ = ReadTokenInformation(token, 25, 0, 0, out var length);
        if (length is 0 or > 1024) throw LastError("Unable to verify token integrity.");
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!ReadTokenInformation(token, 25, buffer, length, out _))
                throw LastError("Unable to verify token integrity.");
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            if (elevated != 0 || uiAccess != 0 || sid.Value is not ("S-1-16-8192" or "S-1-16-8448"))
                throw new InvalidOperationException("Desktop extensions require a non-elevated interactive desktop token.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Reads or changes the exact startup entry in the companion's own account.</summary>
    public bool StartsWithWindows()
    {
        if (_runValue is null) return true;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(_runValue) as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
    }

    public void SetStartsWithWindows(bool enabled)
    {
        RequireNonElevated();
        if (_runValue is null) throw new InvalidOperationException("This companion uses scheduled sign-in startup.");
        if (enabled && StartupCommand.Length > 260)
            throw new IOException("Windows sign-in startup requires a shorter application path.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(_runValue, StartupCommand);
        else if (string.Equals(key.GetValue(_runValue) as string, StartupCommand, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(_runValue, throwOnMissingValue: false);
    }

    private string StartupCommand => $"\"{Path.Combine(AppContext.BaseDirectory, Executable)}\" --startup";
    private static Win32Exception LastError(string message)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{message} ({code}: {new Win32Exception(code).Message})");
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [StructLayout(LayoutKind.Sequential)]
    private struct SessionProcess
    {
        public uint SessionId, ProcessId;
        public nint ProcessName, UserSid;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateProcesses(nint server, uint reserved, uint version, out nint entries, out uint count);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint information, uint length, out uint actualLength);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadTokenInformation(SafeAccessTokenHandle token, int informationClass, nint information, uint length, out uint actualLength);
}
