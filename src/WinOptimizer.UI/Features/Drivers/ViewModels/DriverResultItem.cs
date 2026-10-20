using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Drivers.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;

namespace WinOptimizer.UI.Features.Drivers.ViewModels;

/// <summary>A compact driver row with independent selection and an explicit details action.</summary>
public sealed partial class DriverResultItem(string name, string date, long? sizeBytes, Func<string> details,
    DriverRemovalTarget? target, ILocalizationService localization, IDialogService dialogs) : ObservableObject, ISelectableResultItem
{
    private bool _isSelected;
    public string Name => name;
    public DriverRemovalTarget? Target => target;
    public bool ShowSelection => Target is not null;
    public bool CanClean => Target is not null;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value && CanClean); }
    public string Detail => Name;
    public int DetailMaxLines => 1;
    public ResultReport? Report => null;
    public string Date => DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        ? parsed.ToString("d", CultureInfo.CurrentCulture) : localization.GetString("Drivers_Unknown");
    public string Size => sizeBytes is not long bytes ? localization.GetString("Drivers_Unknown") : bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KB",
        _ => $"{bytes:N0} B"
    };
    public string Details => $"{localization.GetString("Drivers_Date")}: {Date}\n{localization.GetString("Drivers_Size")}: {Size}\n{details()}";

    /// <summary>Opens the complete metadata without changing checkbox selection.</summary>
    [RelayCommand]
    private Task ShowDetailsAsync() => dialogs.ShowInformationAsync(Details, Name);

    public void RefreshText() => OnPropertyChanged(string.Empty);
}
