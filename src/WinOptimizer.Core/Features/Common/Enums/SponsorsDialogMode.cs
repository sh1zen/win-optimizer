namespace WinOptimizer.Core.Features.Common.Enums;

/// <summary>
/// Display mode for the sponsors dialog.
/// <see cref="Normal"/> shows a simple Close button. <see cref="Exit"/> adds a
/// "don't show again" checkbox and an immediately available Exit button when the
/// dialog is shown during application shutdown.
/// </summary>
public enum SponsorsDialogMode
{
    Normal,
    Exit
}
