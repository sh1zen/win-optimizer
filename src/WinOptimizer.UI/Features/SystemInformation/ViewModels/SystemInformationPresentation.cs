using System.Globalization;
using FluentIcons.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;

namespace WinOptimizer.UI.Features.SystemInformation.ViewModels;

/// <summary>A localized property/value row retaining its original typed inventory property.</summary>
public sealed class SystemInformationPropertyViewModel(SystemInformationProperty property, ILocalizationService localization) : ObservableObject
{
    public SystemInformationProperty Property { get; } = property;
    public string Label => localization.GetString(Property.LabelKey);
    public string Value => FormatValue(Property.Value, localization);
    public void RefreshText() => OnPropertyChanged(string.Empty);

    /// <summary>Formats quantities only at the UI boundary, retaining unknown, zero and false as different values.</summary>
    public static string FormatValue(SystemInformationValue value, ILocalizationService localization)
    {
        var unknown = localization.GetString("SystemInfo_Unknown");
        var culture = CultureInfo.CurrentUICulture;
        return value switch
        {
            SystemInformationText text => string.IsNullOrWhiteSpace(text.Value) ? unknown : text.Value,
            SystemInformationBoolean flag => flag.Value is bool enabled ? localization.GetString(enabled ? "SystemInfo_Yes" : "SystemInfo_No") : unknown,
            SystemInformationDate date => date.Value?.ToLocalTime().ToString("g", culture) ?? unknown,
            SystemInformationNumber { Value: ulong number, Unit: SystemInformationUnit.Bytes } => number switch
            {
                >= 1099511627776 => $"{(number / 1099511627776d).ToString("N2", culture)} TiB",
                >= 1073741824 => $"{(number / 1073741824d).ToString("N2", culture)} GiB",
                >= 1048576 => $"{(number / 1048576d).ToString("N2", culture)} MiB",
                >= 1024 => $"{(number / 1024d).ToString("N2", culture)} KiB",
                _ => $"{number.ToString("N0", culture)} B"
            },
            SystemInformationNumber { Value: ulong number, Unit: SystemInformationUnit.BitsPerSecond } => $"{number.ToString("N0", culture)} bit/s",
            SystemInformationNumber { Value: ulong number, Unit: SystemInformationUnit.Megahertz } => $"{number.ToString("N0", culture)} MHz",
            SystemInformationNumber { Value: ulong number, Unit: SystemInformationUnit.Pixels } => $"{number.ToString("N0", culture)} px",
            SystemInformationNumber { Value: ulong number, Unit: SystemInformationUnit.Percent } => $"{number.ToString("N0", culture)}%",
            SystemInformationNumber { Value: ulong number } => number.ToString("N0", culture),
            _ => unknown
        };
    }
}

/// <summary>One real device with individually addressable properties rather than a formatted text block.</summary>
public sealed class SystemInformationDeviceViewModel(SystemInformationItem item, ILocalizationService localization) : ObservableObject
{
    public SystemInformationItem Item { get; } = item;
    public string Name => string.IsNullOrWhiteSpace(Item.Name) ? localization.GetString("SystemInfo_Unknown") : Item.Name;
    public IReadOnlyList<SystemInformationPropertyViewModel> Properties { get; } =
        item.Properties.Select(property => new SystemInformationPropertyViewModel(property, localization)).ToArray();

    public void RefreshText()
    {
        foreach (var property in Properties) property.RefreshText();
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>A navigation category and overview entry backed by the same snapshot devices.</summary>
public sealed class SystemInformationCategoryViewModel(SystemInformationSection? section, ILocalizationService localization) : ObservableObject
{
    public SystemInformationSection? Section { get; } = section;
    public bool IsOverview => Section is null;
    public string Title => localization.GetString(IsOverview ? "SystemInfo_Summary" : "SystemInfo_Section_" + Section!.Category);
    public IReadOnlyList<SystemInformationDeviceViewModel> Items { get; } =
        section?.Items.Select(item => new SystemInformationDeviceViewModel(item, localization)).ToArray() ?? [];
    public string CountText => IsOverview ? "" :
        string.Format(localization.GetString(Items.Count == 1 ? "SystemInfo_EntrySingular" : "SystemInfo_EntryCount"), Items.Count);
    public string EmptyMessage => localization.GetString(Section?.Error is not null ? "SystemInfo_Unavailable" : "SystemInfo_NoDevices");
    public string OverviewValue => Items.FirstOrDefault()?.Name ?? EmptyMessage;
    public string OverviewDetail => Items.Count == 0 ? "" : string.Join("   ·   ",
        Items[0].Properties.Where(property => IsOverviewProperty(property.Property.LabelKey))
            .Select(property => $"{property.Label}: {property.Value}"));

    /// <summary>Uses specific hardware symbols available in the shared family's regular 20-pixel font.</summary>
    public Icon Icon => Section?.Category switch
    {
        SystemInformationCategory.Machine => Icon.DesktopTower,
        SystemInformationCategory.Windows => Icon.Window,
        SystemInformationCategory.Processors => Icon.DeveloperBoard,
        SystemInformationCategory.Motherboard => Icon.DeveloperBoardLightning,
        SystemInformationCategory.Memory => Icon.Ram,
        SystemInformationCategory.Graphics => Icon.Desktop,
        SystemInformationCategory.Storage => Icon.HardDrive,
        SystemInformationCategory.Volumes => Icon.Storage,
        SystemInformationCategory.Network => Icon.NetworkCheck,
        SystemInformationCategory.Audio => Icon.Speaker2,
        SystemInformationCategory.Battery => Icon.BatteryCharge,
        SystemInformationCategory.Firmware => Icon.Settings,
        SystemInformationCategory.Drivers => Icon.PlugConnected,
        _ => Icon.Layer
    };

    private bool IsOverviewProperty(string key) => Section?.Category switch
    {
        SystemInformationCategory.Machine => key is "SystemInfo_Field_Model" or "SystemInfo_Field_TotalMemory",
        SystemInformationCategory.Windows => key is "SystemInfo_Field_Version" or "SystemInfo_Field_Build",
        SystemInformationCategory.Processors => key is "SystemInfo_Field_Cores" or "SystemInfo_Field_MaximumClock",
        SystemInformationCategory.Memory => key is "SystemInfo_Field_Capacity" or "SystemInfo_Field_ConfiguredClock",
        SystemInformationCategory.Storage or SystemInformationCategory.Volumes => key is "SystemInfo_Field_Capacity" or "SystemInfo_Field_FreeSpace",
        SystemInformationCategory.Graphics or SystemInformationCategory.Drivers => key == "SystemInfo_Field_DriverVersion",
        SystemInformationCategory.Battery => key == "SystemInfo_Field_Charge",
        SystemInformationCategory.Network => key == "SystemInfo_Field_LinkSpeed",
        _ => key is "SystemInfo_Field_Manufacturer" or "SystemInfo_Field_Version"
    };

    public void RefreshText()
    {
        foreach (var item in Items) item.RefreshText();
        OnPropertyChanged(string.Empty);
    }
}
