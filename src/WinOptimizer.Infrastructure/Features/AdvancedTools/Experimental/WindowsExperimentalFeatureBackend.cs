using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.AdvancedTools.Experimental;

/// <summary>Uses Windows' runtime configuration API and the separately writable startup override store.</summary>
internal sealed class WindowsExperimentalFeatureBackend(IWindowsVersionService version, IInteractiveUserService user)
    : IExperimentalFeatureBackend
{
    private const uint Startup = 0, Runtime = 1;
    private const int ObjectNameNotFound = unchecked((int)0xc0000034), NotFound = unchecked((int)0xc0000225);
    private const int BufferOverflow = unchecked((int)0x80000005), BufferTooSmall = unchecked((int)0xc0000023);
    private const uint MaximumFlagConfigurations = 100_000;
    private const int FeatureConfigurationBootItem = 17, BootPending = 1, LastKnownGoodPending = 2, RollbackPending = 3;

    public ExperimentalWindows GetWindows()
    {
        var os = Environment.OSVersion.Version;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var current = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        using var identity = WindowsIdentity.GetCurrent();
        var canWrite = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        bool? english = user.IsOtsElevation ? null : GetEnglishDisplayLanguage();
        return new(os.Major, os.Minor, version.GetWindowsBuildNumber(), current?.GetValue("UBR") is int revision ? revision : -1,
            RuntimeInformation.OSArchitecture, current?.GetValue("DisplayVersion") as string ?? "",
            string.Equals(current?.GetValue("InstallationType") as string, "Client", StringComparison.OrdinalIgnoreCase),
            QueryBootTime(), english, canWrite);
    }

    public ExperimentalObservation Read(uint id)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        Observe(() => Query(id, Runtime), out var runtime, out var runtimeError);
        Observe(() => Query(id, Startup).Value, out var startup, out var startupError);
        Observe(() =>
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return WindowsFeaturePersistence.Read(machine, id);
        }, out var persistent, out var persistenceError);
        return new(id, runtime.Value, startup, persistent, runtime.Stamp, runtimeError, startupError, persistenceError);
    }

    public ExperimentalFlagInventory ReadFlagInventory(CancellationToken cancellationToken) =>
        new(ReadFlagStore(Runtime, RtlQueryAllFeatureConfigurations, cancellationToken),
            ReadFlagStore(Startup, RtlQueryAllFeatureConfigurations, cancellationToken));

    /// <summary>Reads one store independently so a failed query cannot hide the other store's results.</summary>
    internal static ExperimentalFlagStore ReadFlagStore(uint store, FlagQuery query, CancellationToken token)
    {
        Observe(() => QueryFlagConfigurations(store, query, token), out var configurations, out var error);
        return new(configurations ?? [], error);
    }

    /// <summary>Retries a growing snapshot using the native count as both capacity and result length.</summary>
    private static ExperimentalFlagConfiguration[] QueryFlagConfigurations(uint store, FlagQuery query, CancellationToken token)
    {
        const string operation = "Enumerate Windows feature configurations";
        token.ThrowIfCancellationRequested();
        ulong stamp = 0;
        uint count = 0;
        var status = query(store, ref stamp, null, ref count);
        if (status == 0 && count == 0) return [];
        if (status is not (0 or BufferOverflow or BufferTooSmall)) Check(status, operation);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (count is 0 or > MaximumFlagConfigurations)
                throw new InvalidDataException($"Windows returned an unexpected feature configuration count ({count}).");
            var buffer = new NativeFeature[count];
            status = query(store, ref stamp, buffer, ref count);
            if (status is BufferOverflow or BufferTooSmall && count > buffer.Length) continue;
            Check(status, operation);
            if (count > buffer.Length)
                throw new InvalidDataException($"Windows reported {count} feature configurations for a buffer of {buffer.Length}.");

            var configurations = new ExperimentalFlagConfiguration[count];
            for (var index = 0; index < configurations.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = buffer[index];
                if (entry.Id == 0) throw new InvalidDataException("Windows returned an empty feature ID.");
                configurations[index] = new(entry.Id, entry.Flags & 15, DecodeState(entry.Flags));
            }
            return configurations;
        }
        throw new IOException("Windows feature configuration kept growing during discovery. Refresh and retry.");
    }

    public void Write(ExperimentalWindows windows, ExperimentalObservation before, ExperimentalStore store,
        FeatureOverride? value)
    {
        var current = GetWindows();
        if (!current.CanWrite || !current.SameRelease(windows) || current.BootTime != windows.BootTime)
            throw new InvalidOperationException("Windows or the current write permissions changed during the operation.");
        ArgumentOutOfRangeException.ThrowIfZero(before.Id);
        if (value is { IsValid: false }) throw new ArgumentException("Invalid feature override.", nameof(value));

        switch (store)
        {
            case ExperimentalStore.Runtime:
                WriteRuntime(before, value);
                break;
            case ExperimentalStore.Persistence:
                WritePersistence(before, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(store));
        }
    }

    private static void WriteRuntime(ExperimentalObservation before, FeatureOverride? value)
    {
        var (observed, stamp) = Query(before.Id, Runtime);
        if (observed != before.Runtime || observed?.MasksUserOverride == true)
            throw new IOException($"Feature {before.Id} runtime configuration changed or is masked by a higher priority.");
        if (stamp == 0 || stamp != before.RuntimeStamp)
            throw new IOException($"Feature {before.Id} runtime change stamp is unavailable or has changed.");

        var update = new FeatureUpdate(before.Id, value?.AsRuntime());
        Check(RtlSetFeatureConfigurations(ref stamp, Runtime, in update, 1), "Runtime write");
        var after = Query(before.Id, Runtime).Value;
        if (after?.MasksUserOverride == true || after?.UserOverride != value?.AsRuntime())
            throw new IOException($"Feature {before.Id} runtime override could not be verified.");
    }

    private static void WritePersistence(ExperimentalObservation before, FeatureOverride? value)
    {
        var status = RtlGetSystemBootStatus(FeatureConfigurationBootItem, out var bootState, sizeof(int), 0);
        if (status == ObjectNameNotFound)
        {
            Check(RtlCreateBootStatusDataFile(null), "Initialize feature boot status");
            Check(RtlGetSystemBootStatus(FeatureConfigurationBootItem, out bootState, sizeof(int), 0), "Read feature boot status");
        }
        else Check(status, "Read feature boot status");
        if (bootState is LastKnownGoodPending or RollbackPending || bootState is < 0 or > 4)
            throw new IOException($"Windows feature configuration is in boot state {bootState}. Restart Windows and refresh.");

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        WindowsFeaturePersistence.Write(machine, before.Id, before.Persistent, value);
        Check(RtlGetSystemBootStatus(FeatureConfigurationBootItem, out bootState, sizeof(int), 0), "Recheck feature boot status");
        if (bootState is LastKnownGoodPending or RollbackPending || bootState is < 0 or > 4)
            throw new IOException($"Windows changed to feature boot state {bootState}. Restart Windows and refresh.");
        if (bootState != BootPending)
        {
            bootState = BootPending;
            Check(RtlSetSystemBootStatus(FeatureConfigurationBootItem, in bootState, sizeof(int), 0), "Schedule feature boot configuration");
        }
        Check(RtlGetSystemBootStatus(FeatureConfigurationBootItem, out bootState, sizeof(int), 0), "Verify feature boot status");
        if (bootState != BootPending)
            throw new IOException($"Windows did not confirm that boot feature configuration is pending (state {bootState}).");
    }

    private static (ObservedFeature? Value, ulong Stamp) Query(uint id, uint store)
    {
        var before = store == Runtime ? RtlQueryFeatureConfigurationChangeStamp() : 0;
        ulong stamp = 0;
        var status = RtlQueryFeatureConfiguration(id, store, ref stamp, out var config);
        if (store == Runtime && (before == 0 || before != RtlQueryFeatureConfigurationChangeStamp()))
            throw new IOException("Runtime feature configuration changed during discovery. Refresh and retry.");
        if (status is ObjectNameNotFound or NotFound)
            return (null, before);
        Check(status, store == Runtime ? "Runtime query" : "Startup snapshot query");
        if (config.Id != id) throw new InvalidDataException($"Windows returned an unexpected feature ID ({config.Id}).");
        return (Decode(config.Flags, config.Payload), before);
    }

    private static bool? GetEnglishDisplayLanguage()
    {
        try { return CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).TwoLetterISOLanguageName == "en"; }
        catch (CultureNotFoundException) { return null; }
    }

    internal static ObservedFeature Decode(uint flags, uint payload)
    {
        var state = DecodeState(flags) ?? throw new InvalidDataException("Windows returned an unsupported feature state.");
        var value = new FeatureOverride(state, (flags >> 6) & 1, (flags >> 8) & 63, (flags >> 14) & 3, payload);
        return new(flags & 15, value);
    }

    private static FeatureOverrideState? DecodeState(uint flags) => ((flags >> 4) & 3) switch
    {
        0 => FeatureOverrideState.Default,
        1 => FeatureOverrideState.Disabled,
        2 => FeatureOverrideState.Enabled,
        _ => null
    };

    private static void Observe<T>(Func<T> read, out T value, out string? error)
    {
        try { value = read(); error = null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or
            Win32Exception or EntryPointNotFoundException)
        {
            value = default!;
            error = ex.Message;
        }
    }

    private static void Check(int status, string operation)
    {
        if (status == 0) return;
        var error = new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
        throw new IOException($"{operation}: NTSTATUS 0x{status:X8} ({error.Message}).", error);
    }

    private static long QueryBootTime()
    {
        const int systemTimeOfDayInformation = 3;
        Check(NtQuerySystemInformation(systemTimeOfDayInformation, out var time, 48, out _), "Read Windows boot session");
        return time.BootTime > 0 ? time.BootTime : throw new IOException("Windows boot time is unavailable.");
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private struct TimeOfDay { public long BootTime; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFeature { public uint Id, Flags, Payload; }

    internal delegate int FlagQuery(uint type, ref ulong stamp, NativeFeature[]? configurations, ref uint count);

    // Field order matches the Windows RTL_FEATURE_CONFIGURATION_UPDATE ABI.
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct FeatureUpdate(uint id, FeatureOverride? value)
    {
        public readonly uint Id = id;
        public readonly uint Priority = FeatureOverride.UserPriority;
        public readonly uint State = (uint)(value?.State ?? FeatureOverrideState.Default);
        public readonly uint Options = value?.Options ?? 0;
        public readonly uint Variant = value?.Variant ?? 0;
        public readonly uint PayloadKind = value?.PayloadKind ?? 0;
        public readonly uint Payload = value?.Payload ?? 0;
        public readonly uint Operation = value is null ? 4u : 3u; // ResetState or FeatureState | VariantState
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlQueryFeatureConfiguration(uint id, uint type, ref ulong stamp, out NativeFeature config);

    [DllImport("ntdll.dll")]
    private static extern int RtlQueryAllFeatureConfigurations(uint type, ref ulong stamp,
        [Out] NativeFeature[]? configurations, ref uint count);

    [DllImport("ntdll.dll")]
    private static extern ulong RtlQueryFeatureConfigurationChangeStamp();

    [DllImport("ntdll.dll")]
    private static extern int RtlSetFeatureConfigurations(ref ulong stamp, uint type, in FeatureUpdate update, int count);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetSystemBootStatus(int item, out int state, int size, nint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlSetSystemBootStatus(int item, in int state, int size, nint returnLength);

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RtlCreateBootStatusDataFile(string? path);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, out TimeOfDay info, int size, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
