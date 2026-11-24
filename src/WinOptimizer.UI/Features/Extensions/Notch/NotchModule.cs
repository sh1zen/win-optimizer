using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Extensions.Notch;

[Flags]
public enum NotchModule
{
    None = 0,
    Media = 1,
    Focus = 2,
    Calendar = 4,
    Batteries = 8,
    Cpu = 32,
    Memory = 64,
    Gpu = 128,
    Clock = 256,
    Traffic = 512,
    Uptime = 1024,
    Shortcuts = 2048,
    Weather = 4096,
    AudioVisualizer = 16384,
    StatusAlerts = 32768,
    FrameRate = 131072,
    QuickSettings = 262144
}

/// <summary>Projects one module from the extension's confirmed preferences.</summary>
public sealed class NotchModuleOption(NotchExtension extension, ILocalizationService localization, NotchModule module) : ObservableObject
{
    public NotchModule Module { get; } = module;
    public string Title => GetTitle(localization, Module);
    public string Description => localization.GetString(TitleKey(Module) + "Description");
    public bool IsEnabled => extension.Modules.HasFlag(Module);
    public bool CanEdit => extension.CanEdit;
    public bool IsPinnable => NotchExtension.PinnableModules.HasFlag(Module);
    public bool IsPinned => extension.PinnedModules.HasFlag(Module);
    public bool CanEditPin => IsEnabled && CanEdit;
    public string PinLabel => localization.GetString("Notch_CompactPin", Title);
    public Symbol Icon => Module switch
    {
        NotchModule.Media => Symbol.MusicNote2,
        NotchModule.Focus => Symbol.Timer,
        NotchModule.Calendar => Symbol.Calendar,
        NotchModule.Batteries => Symbol.Battery4,
        NotchModule.Cpu => Symbol.DeveloperBoard,
        NotchModule.Memory => Symbol.Ram,
        NotchModule.Gpu => Symbol.DesktopPulse,
        NotchModule.Clock or NotchModule.Uptime => Symbol.Clock,
        NotchModule.Traffic => Symbol.ArrowSwap,
        NotchModule.Shortcuts => Symbol.Folder,
        NotchModule.Weather => Symbol.WeatherSunny,
        NotchModule.AudioVisualizer => Symbol.Pulse,
        NotchModule.StatusAlerts => Symbol.Alert,
        NotchModule.FrameRate => Symbol.TopSpeed,
        NotchModule.QuickSettings => Symbol.Settings,
        _ => throw new ArgumentOutOfRangeException(nameof(Module))
    };

    internal void Refresh() => OnPropertyChanged(string.Empty);
    internal Task SetEnabledAsync(bool enabled) => extension.SetModuleAsync(Module, enabled);
    internal Task SetPinnedAsync(bool pinned) => extension.SetPinnedAsync(Module, pinned);
    internal void RefreshEditability()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditPin));
    }

    internal static string GetTitle(ILocalizationService localization, NotchModule module) => localization.GetString(TitleKey(module));

    private static string TitleKey(NotchModule module) => module switch
    {
        NotchModule.Media => "Notch_MediaTab",
        NotchModule.Focus => "Notch_FocusTab",
        NotchModule.Calendar => "Notch_Calendar",
        NotchModule.Batteries => "Notch_Batteries",
        NotchModule.Cpu => "Notch_Cpu",
        NotchModule.Memory => "Notch_Memory",
        NotchModule.Gpu => "Notch_Gpu",
        NotchModule.Clock => "Notch_Clock",
        NotchModule.Traffic => "Notch_Traffic",
        NotchModule.Uptime => "Notch_Uptime",
        NotchModule.Shortcuts => "Notch_Shortcuts",
        NotchModule.Weather => "Notch_Weather",
        NotchModule.AudioVisualizer => "Notch_AudioVisualizer",
        NotchModule.StatusAlerts => "Notch_StatusAlerts",
        NotchModule.FrameRate => "Notch_FrameRate",
        NotchModule.QuickSettings => "Notch_QuickSettings",
        _ => throw new ArgumentOutOfRangeException(nameof(module))
    };
}
