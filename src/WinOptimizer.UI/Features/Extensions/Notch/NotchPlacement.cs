namespace WinOptimizer.UI.Features.Extensions.Notch;

/// <summary>Stores the frame outline and the selected Windows display device.</summary>
internal sealed record NotchPlacement(bool Floating = false, string? DisplayDevice = null)
{
    internal static NotchPlacement Default { get; } = new();

    internal NotchPlacement Normalize() => DisplayDevice is { Length: > 0 and < 128 } device && !device.Any(char.IsControl)
        ? this : this with { DisplayDevice = null };
}
