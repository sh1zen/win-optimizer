using FluentIcons.Common;

namespace WinOptimizer.UI.Features.Disks;

internal enum DiskTool { Delete, Permissions, Shadow, Hash, Usb, Recovery, Optimize }

/// <summary>Defines the disk destinations shared by the overview, tool pages and search.</summary>
internal static class DiskToolCatalog
{
    public static IReadOnlyList<DiskTool> Tools { get; } = Enum.GetValues<DiskTool>();
    public static string TitleKey(this DiskTool tool) => $"Disks_{tool}";
    public static string DescriptionKey(this DiskTool tool) => $"{tool.TitleKey()}Description";

    public static Icon GetIcon(this DiskTool tool) => tool switch
    {
        DiskTool.Delete => Icon.LockClosed,
        DiskTool.Permissions => Icon.ShieldCheckmark,
        DiskTool.Shadow => Icon.History,
        DiskTool.Hash => Icon.NumberSymbol,
        DiskTool.Usb => Icon.UsbStick,
        DiskTool.Recovery => Icon.DocumentSearch,
        DiskTool.Optimize => Icon.ArrowSync,
        _ => throw new ArgumentOutOfRangeException(nameof(tool))
    };
}
