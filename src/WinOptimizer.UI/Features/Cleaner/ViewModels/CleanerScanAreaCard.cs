using CommunityToolkit.Mvvm.ComponentModel;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Cleaner.ViewModels;

internal enum CleanerAreaScanState { Ready, NotSelected, Queued, Scanning, Scanned }

/// <summary>Projects scan status alongside the existing area selection.</summary>
public sealed class CleanerScanAreaCard(CleanerChoice<CleanerArea> choice, ILocalizationService localization) : ObservableObject
{
    private CleanerAreaScanState _state;
    public CleanerChoice<CleanerArea> Choice { get; } = choice;
    public bool HasStatus => _state != CleanerAreaScanState.Ready;
    public bool IsActive => _state == CleanerAreaScanState.Scanning;
    public bool HasScanCheck => _state is CleanerAreaScanState.Scanning or CleanerAreaScanState.Queued or CleanerAreaScanState.Scanned;
    public string Status => HasStatus ? localization.GetString("Cleaner_Area_" + _state) : "";

    internal void SetState(CleanerAreaScanState state)
    {
        if (!SetProperty(ref _state, state, nameof(Status))) return;
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(HasScanCheck));
    }

    internal void RefreshText() => OnPropertyChanged(nameof(Status));
}
