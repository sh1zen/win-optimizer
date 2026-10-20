using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.UI.Features.SoftwareApps;

/// <summary>
/// Builds the localized confirmation for removing selected applications.
/// Pure factory — no UI types.
/// </summary>
public static class AppOperationConfirmation
{
    public static ConfirmationRequest Build(
        IEnumerable<string> itemNames,
        string? checkboxText,
        ILocalizationService localization)
    {
        return new ConfirmationRequest
        {
            Title = localization.GetString("Dialog_ConfirmRemoval"),
            Message = localization.GetString("Dialog_ItemsWillBeRemoved"),
            Items = itemNames.ToList(),
            CheckboxText = checkboxText,
            CheckboxInitiallyChecked = true,
            ConfirmButtonText = localization.GetString("Button_Continue"),
            CancelButtonText = localization.GetString("Button_Cancel")
        };
    }
}
