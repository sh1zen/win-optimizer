using Microsoft.Win32;

namespace WinOptimizer.Infrastructure.Features.Extensions;

public sealed record PreviewSettingsSnapshot(bool ShortcutEnabled, bool StartsWithWindows,
    int BackgroundOpacity = PreviewExtensionSettings.DefaultOpacity);

/// <summary>Controls the session shortcut and stores Preview settings for the desktop account.</summary>
public static class PreviewExtensionSettings
{
    public const int MinimumOpacity = 0;
    public const int MaximumOpacity = 10;
    public const int DefaultOpacity = 8;
    private const string SettingsKey = @"Software\win-optimizer\Preview";
    private const string OpacityValue = "BackgroundOpacity";

    internal sealed record Request(bool? ShortcutEnabled = null, bool? StartsWithWindows = null, bool Stop = false,
        int? BackgroundOpacity = null);

    public static Task<PreviewSettingsSnapshot> LoadAsync() => SendAsync(new());

    public static Task<PreviewSettingsSnapshot> SetStartupAsync(bool enabled) => SendAsync(new(StartsWithWindows: enabled));

    public static Task<PreviewSettingsSnapshot> SetBackgroundOpacityAsync(int opacity) => SendAsync(new(BackgroundOpacity: opacity));

    public static Task<PreviewSettingsSnapshot> SetShortcutAsync(bool enabled, string language) =>
        SendAsync(new(ShortcutEnabled: enabled), enabled ? language : null);

    internal static Task StopAsync() => SendAsync(new(Stop: true));

    private static Task<PreviewSettingsSnapshot> SendAsync(Request request, string? startLanguage = null) =>
        DesktopExtensionSettingsChannel.SendAsync<Request, PreviewSettingsSnapshot>(DesktopExtensionProcess.Preview, request, startLanguage);

    /// <summary>Applies settings in the desktop account and replies with the confirmed snapshot.</summary>
    public static async Task ReceiveAsync(Guid connection, Func<bool>? readShortcut = null, Action<bool>? setShortcut = null,
        Action? shutdown = null, Action<int>? updateOpacity = null)
    {
        var preview = DesktopExtensionProcess.Preview;
        var stopRequested = false;
        await DesktopExtensionSettingsChannel.ReceiveAsync<Request, PreviewSettingsSnapshot>(connection, request =>
        {
            var settings = Apply(request, preview.StartsWithWindows, preview.SetStartsWithWindows, readShortcut, setShortcut,
                ReadBackgroundOpacity, WriteBackgroundOpacity, updateOpacity);
            stopRequested = request.Stop;
            return Task.FromResult(settings);
        });
        // Acknowledge the updater while the resident dispatcher is still running.
        if (stopRequested) shutdown?.Invoke();
    }

    internal static PreviewSettingsSnapshot Apply(Request request, Func<bool> readStartup, Action<bool> setStartup,
        Func<bool>? readShortcut, Action<bool>? setShortcut, Func<int> readOpacity, Action<int> writeOpacity,
        Action<int>? updateOpacity = null)
    {
        if (request.BackgroundOpacity is { } proposed && !IsValidOpacity(proposed))
            throw new ArgumentOutOfRangeException(nameof(request.BackgroundOpacity), proposed, "Opacity must be between 0 and 10.");

        if (request.Stop)
            setShortcut?.Invoke(false);
        else if (request.ShortcutEnabled is { } active)
        {
            if (active && setShortcut is null) throw new InvalidOperationException("Preview must be running to enable the shortcut.");
            setShortcut?.Invoke(active);
        }
        if (request.StartsWithWindows is { } startup) setStartup(startup);
        if (request.BackgroundOpacity is { } opacity)
        {
            writeOpacity(opacity);
            updateOpacity?.Invoke(opacity);
        }
        return new(readShortcut?.Invoke() ?? false, readStartup(), readOpacity());
    }

    /// <summary>Loads the desktop account's saved opacity, including profiles created before the setting existed.</summary>
    public static int ReadBackgroundOpacity()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(OpacityValue) is int value && IsValidOpacity(value) ? value : DefaultOpacity;
    }

    private static void WriteBackgroundOpacity(int opacity)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey)
            ?? throw new IOException("Unable to open the Preview settings registry key.");
        key.SetValue(OpacityValue, opacity, RegistryValueKind.DWord);
    }

    private static bool IsValidOpacity(int value) => value is >= MinimumOpacity and <= MaximumOpacity;
}
