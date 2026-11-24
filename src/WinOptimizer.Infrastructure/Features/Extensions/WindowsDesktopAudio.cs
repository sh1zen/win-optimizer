using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinOptimizer.Infrastructure.Features.Extensions;

public sealed record DesktopMicrophoneReading(bool IsInUse, string? Application = null);
public readonly record struct DesktopVolume(int Percent, bool IsMuted);

/// <summary>Reads Core Audio session state and endpoint peaks without opening an audio stream.</summary>
public static class WindowsDesktopAudio
{
    private const int ActiveDevice = 1;
    private const int Capture = 1, Render = 0, Multimedia = 1;
    private const int ActivationContext = 23; // CLSCTX_ALL.
    private const int ActiveSession = 1;
    private const int DeviceNotFound = unchecked((int)0x80070490);
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    public static double? ReadOutputPeak() => WithDefaultEndpoint(Render, endpoint =>
    {
        var iid = typeof(AudioMeter).GUID;
        endpoint.Activate(ref iid, ActivationContext, 0, out var instance);
        try
        {
            ((AudioMeter)instance).GetPeakValue(out var peak);
            return Math.Clamp((double)peak, 0, 1);
        }
        finally { Release(instance); }
    });

    /// <summary>Reads the current level and mute state of the default playback endpoint.</summary>
    public static DesktopVolume? ReadOutputVolume() => WithOutputVolume(volume =>
    {
        volume.GetMasterVolumeLevelScalar(out var level);
        volume.GetMute(out var muted);
        return new DesktopVolume((int)Math.Round(Math.Clamp(level, 0, 1) * 100), muted);
    });

    /// <summary>Changes the default playback level while preserving mute.</summary>
    public static void SetOutputVolume(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        UpdateOutput(volume => volume.SetMasterVolumeLevelScalar(percent / 100f, 0));
    }

    public static void SetOutputMute(bool muted) => UpdateOutput(volume => volume.SetMute(muted, 0));

    private static void UpdateOutput(Action<AudioEndpointVolume> update)
    {
        bool? applied = WithOutputVolume(volume => { update(volume); return true; });
        if (applied is null) throw new InvalidOperationException("There is no default playback device.");
    }

    /// <summary>Runs an operation against the default playback endpoint's volume controls.</summary>
    private static T? WithOutputVolume<T>(Func<AudioEndpointVolume, T> action) where T : struct =>
        WithDefaultEndpoint(Render, endpoint =>
        {
            var iid = typeof(AudioEndpointVolume).GUID;
            endpoint.Activate(ref iid, ActivationContext, 0, out var instance);
            try { return action((AudioEndpointVolume)instance); }
            finally { Release(instance); }
        });

    private static T? WithDefaultEndpoint<T>(int flow, Func<Device, T> action) where T : struct
    {
        var enumerator = (DeviceEnumerator)(object)new DeviceEnumeratorClass();
        try
        {
            var result = enumerator.GetDefaultAudioEndpoint(flow, Multimedia, out var endpoint);
            if (result == DeviceNotFound) return null;
            Marshal.ThrowExceptionForHR(result);
            try { return action(endpoint); }
            finally { Release(endpoint); }
        }
        finally { Release(enumerator); }
    }

    public static DesktopMicrophoneReading ReadMicrophone()
    {
        var enumerator = (DeviceEnumerator)(object)new DeviceEnumeratorClass();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(Capture, ActiveDevice, out var endpoints));
            try
            {
                endpoints.GetCount(out var devices);
                for (uint i = 0; i < devices; i++)
                {
                    endpoints.Item(i, out var endpoint);
                    try
                    {
                        if (FindCaptureSession(endpoint) is { } active) return active;
                    }
                    catch (COMException ex) when (ex.HResult == DeviceInvalidated) { }
                    finally { Release(endpoint); }
                }
            }
            finally { Release(endpoints); }
        }
        finally { Release(enumerator); }
        return new(false);
    }

    private static DesktopMicrophoneReading? FindCaptureSession(Device endpoint)
    {
        var iid = typeof(AudioSessionManager).GUID;
        endpoint.Activate(ref iid, ActivationContext, 0, out var manager);
        try
        {
            ((AudioSessionManager)manager).GetSessionEnumerator(out var sessions);
            try
            {
                sessions.GetCount(out var count);
                for (int i = 0; i < count; i++)
                {
                    sessions.GetSession(i, out var instance);
                    try
                    {
                        var session = (AudioSession)instance;
                        session.GetState(out var state);
                        if (state == ActiveSession)
                            return new(true, session.GetProcessId(out var pid) >= 0 ? GetProcessName(pid) : null);
                    }
                    finally { Release(instance); }
                }
                return null;
            }
            finally { Release(sessions); }
        }
        finally { Release(manager); }
    }

    private static string? GetProcessName(uint pid)
    {
        if (pid == 0 || pid > int.MaxValue) return null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // A capture session can disappear between enumeration and process identification.
            return null;
        }
    }

    private static void Release(object instance) => Marshal.ReleaseComObject(instance);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class DeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int states, out DeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out Device device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out Device device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Device
    {
        void Activate(ref Guid iid, int context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioMeter
    {
        void GetPeakValue(out float peak);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioEndpointVolume
    {
        // Include earlier IAudioEndpointVolume slots in their native order.
        void RegisterControlChangeNotify(nint callback);
        void UnregisterControlChangeNotify(nint callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, nint context);
        void SetMasterVolumeLevelScalar(float level, nint context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, nint context);
        void SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioSessionManager
    {
        // Include inherited IAudioSessionManager slots before GetSessionEnumerator.
        void GetAudioSessionControl(nint sessionGuid, uint flags, out nint control);
        void GetSimpleAudioVolume(nint sessionGuid, uint flags, out nint volume);
        void GetSessionEnumerator(out AudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioSession
    {
        // Include inherited IAudioSessionControl slots in their native order.
        void GetState(out int state);
        void GetDisplayName(out nint name);
        void SetDisplayName(nint name, nint context);
        void GetIconPath(out nint path);
        void SetIconPath(nint path, nint context);
        void GetGroupingParam(out Guid group);
        void SetGroupingParam(ref Guid group, nint context);
        void RegisterAudioSessionNotification(nint notification);
        void UnregisterAudioSessionNotification(nint notification);
        void GetSessionIdentifier(out nint identifier);
        void GetSessionInstanceIdentifier(out nint identifier);
        [PreserveSig] int GetProcessId(out uint processId);
    }
}
