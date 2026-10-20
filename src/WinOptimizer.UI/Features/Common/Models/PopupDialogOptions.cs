using Microsoft.UI.Xaml.Controls;

namespace WinOptimizer.UI.Features.Common.Models;

/// <summary>Supplies dialog content and actions while the shared handler owns its frame and lifetime.</summary>
public sealed record PopupDialogOptions
{
    public object? Title { get; init; }
    public object? Content { get; init; }
    public string PrimaryButtonText { get; init; } = "";
    public string SecondaryButtonText { get; init; } = "";
    public string CloseButtonText { get; init; } = "";
    public ContentDialogButton DefaultButton { get; init; } = ContentDialogButton.None;
    public bool IsPrimaryButtonEnabled { get; init; } = true;
    public double MinWidth { get; init; }
}
