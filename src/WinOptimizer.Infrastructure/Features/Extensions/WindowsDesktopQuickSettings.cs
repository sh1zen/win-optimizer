using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Windows.Devices.Radios;
using Windows.System;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Extensions;

public enum DesktopPowerMode { Efficiency, Balanced, Performance }
public enum DesktopRadio { WiFi, Bluetooth }
public enum DesktopSettingsPage { WiFi, Bluetooth, Power = 4 }

/// <summary>Current Windows control values. A missing value means Windows did not expose that control.</summary>
public sealed record DesktopQuickSettings(int? Brightness = null, DesktopVolume? Volume = null, bool? WiFi = null,
    bool? Bluetooth = null, DesktopPower? Battery = null, DesktopPowerMode? PowerMode = null,
    bool? NightLight = null, bool? DoNotDisturb = null);

public interface IDesktopQuickSettings
{
    Task<DesktopQuickSettings> ReadAsync(CancellationToken token);
    Task SetBrightnessAsync(int percent, CancellationToken token);
    Task SetVolumeAsync(int percent, CancellationToken token);
    Task SetMuteAsync(bool muted, CancellationToken token);
    Task SetRadioAsync(DesktopRadio radio, bool enabled, CancellationToken token);
    Task SetNightLightAsync(bool enabled, CancellationToken token);
    Task SetDoNotDisturbAsync(bool enabled, CancellationToken token);
    Task SetPowerModeAsync(DesktopPowerMode mode, CancellationToken token);
    Task OpenAsync(DesktopSettingsPage page, CancellationToken token);
}

/// <summary>Reads and applies live Windows settings for the Notch controls.</summary>
public sealed class WindowsDesktopQuickSettings(ILogService log) : IDesktopQuickSettings
{
    private static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public async Task<DesktopQuickSettings> ReadAsync(CancellationToken token)
    {
        var radiosTask = ReadRadiosAsync(token);
        var hardware = await Task.Run(() =>
        {
            var battery = Read(WindowsDesktopStatus.ReadPower);
            bool desktopAccount = Read<bool>(() => IsDesktopAccount()) == true;
            return new DesktopQuickSettings(Read(WindowsDesktopStatus.ReadBrightness), Read(WindowsDesktopAudio.ReadOutputVolume),
                Battery: battery, PowerMode: Read(() => ReadPowerMode(battery?.OnAc != false)),
                NightLight: desktopAccount ? Read(WindowsNightLight.Read) : null,
                DoNotDisturb: desktopAccount ? Read(WindowsDoNotDisturb.Read) : null);
        }, token).ConfigureAwait(false);
        var radios = await radiosTask.ConfigureAwait(false);
        return hardware with { WiFi = ReadRadioState(radios, RadioKind.WiFi), Bluetooth = ReadRadioState(radios, RadioKind.Bluetooth) };
    }

    private T? Read<T>(Func<T?> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or COMException or UnauthorizedAccessException or
            Win32Exception or SecurityException or InvalidOperationException)
        {
            log.LogDebug($"Could not read a Notch Windows setting: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<Radio>> ReadRadiosAsync(CancellationToken token)
    {
        try { return await Radio.GetRadiosAsync().AsTask(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            log.LogDebug($"Could not read Windows radios: {ex.Message}");
            return [];
        }
    }

    private static bool? ReadRadioState(IReadOnlyList<Radio> radios, RadioKind kind)
    {
        var states = radios.Where(radio => radio.Kind == kind).Select(radio => radio.State).ToArray();
        if (states.Contains(RadioState.On)) return true;
        return states.Contains(RadioState.Off) ? false : null;
    }

    public Task SetBrightnessAsync(int percent, CancellationToken token) =>
        Task.Run(() => WindowsDesktopStatus.SetBrightness(percent), token);

    public Task SetVolumeAsync(int percent, CancellationToken token) =>
        Task.Run(() => WindowsDesktopAudio.SetOutputVolume(percent), token);

    public Task SetMuteAsync(bool muted, CancellationToken token) =>
        Task.Run(() => WindowsDesktopAudio.SetOutputMute(muted), token);

    /// <summary>Waits for the shell to accept the Night light transition before reporting the result.</summary>
    public Task SetNightLightAsync(bool enabled, CancellationToken token) => Task.Run(async () =>
    {
        RequireDesktopAccount();
        WindowsNightLight.Set(enabled);
        await Task.Delay(150, token).ConfigureAwait(false);
        if (WindowsNightLight.Read() != enabled) throw new IOException("Windows did not confirm the Night light change.");
    }, token);

    public Task SetDoNotDisturbAsync(bool enabled, CancellationToken token) => Task.Run(() =>
    {
        RequireDesktopAccount();
        WindowsDoNotDisturb.Set(enabled);
    }, token);

    /// <summary>Scope per-user controls to the account whose shell owns this desktop.</summary>
    private static bool IsDesktopAccount()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User is { } sid && sid.Equals(DesktopExtensionProcess.DesktopUser());
    }

    private static void RequireDesktopAccount()
    {
        if (!IsDesktopAccount()) throw new UnauthorizedAccessException("This control requires the Windows desktop account.");
    }

    /// <summary>Requests control from the calling UI thread and waits for Windows to report the new state.</summary>
    public async Task SetRadioAsync(DesktopRadio radio, bool enabled, CancellationToken token)
    {
        var kind = radio switch
        {
            DesktopRadio.WiFi => RadioKind.WiFi,
            DesktopRadio.Bluetooth => RadioKind.Bluetooth,
            _ => throw new ArgumentOutOfRangeException(nameof(radio))
        };
        if (await Radio.RequestAccessAsync().AsTask(token).ConfigureAwait(false) != RadioAccessStatus.Allowed)
            throw new UnauthorizedAccessException("Windows denied radio control.");
        var radios = (await Radio.GetRadiosAsync().AsTask(token).ConfigureAwait(false))
            .Where(item => item.Kind == kind && item.State is RadioState.On or RadioState.Off).ToArray();
        if (radios.Length == 0) throw new InvalidOperationException("There is no available radio of this kind.");
        var target = enabled ? RadioState.On : RadioState.Off;
        foreach (var item in radios)
            if (item.State != target && await item.SetStateAsync(target).AsTask(token).ConfigureAwait(false) != RadioAccessStatus.Allowed)
                throw new UnauthorizedAccessException("Windows rejected the radio change.");
        for (int attempt = 0; attempt < 15; attempt++)
        {
            if (radios.All(item => item.State == target)) return;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new IOException("Windows did not confirm the radio change.");
    }

    /// <summary>Changes the selected Windows power mode for the device's current power source.</summary>
    public Task SetPowerModeAsync(DesktopPowerMode mode, CancellationToken token) => Task.Run(() =>
    {
        var id = mode switch
        {
            DesktopPowerMode.Efficiency => Efficiency,
            DesktopPowerMode.Balanced => Guid.Empty,
            DesktopPowerMode.Performance => Performance,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            throw new InvalidOperationException("Windows power mode is unavailable.");
        uint status = WindowsDesktopStatus.ReadPower()?.OnAc == false
            ? PowerSetUserConfiguredDCPowerMode(in id) : PowerSetUserConfiguredACPowerMode(in id);
        if (status != 0) throw new Win32Exception((int)status);
    }, token);

    private static DesktopPowerMode? ReadPowerMode(bool onAc)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return null;
        try
        {
            uint status = onAc ? PowerGetUserConfiguredACPowerMode(out var id) : PowerGetUserConfiguredDCPowerMode(out id);
            if (status != 0) return null;
            if (id == Efficiency) return DesktopPowerMode.Efficiency;
            if (id == Performance) return DesktopPowerMode.Performance;
            return id == Guid.Empty ? DesktopPowerMode.Balanced : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    public async Task OpenAsync(DesktopSettingsPage page, CancellationToken token)
    {
        var target = page switch
        {
            DesktopSettingsPage.WiFi => "network-wifi",
            DesktopSettingsPage.Bluetooth => "bluetooth",
            DesktopSettingsPage.Power => "powersleep",
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };
        if (!await Launcher.LaunchUriAsync(new Uri($"ms-settings:{target}")).AsTask(token))
            throw new InvalidOperationException("Windows could not open the settings page.");
    }

    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetUserConfiguredACPowerMode(in Guid mode);
    [DllImport("powrprof.dll", ExactSpelling = true)]
    private static extern uint PowerSetUserConfiguredDCPowerMode(in Guid mode);
}
