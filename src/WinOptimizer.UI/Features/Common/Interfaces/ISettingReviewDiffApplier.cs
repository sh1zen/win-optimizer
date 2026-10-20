using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Optimize.ViewModels;

namespace WinOptimizer.UI.Features.Common.Interfaces;

/// <summary>
/// Applies review-mode diff state to a SettingItemViewModel.
/// </summary>
public interface ISettingReviewDiffApplier
{
    /// <summary>
    /// Checks for an eagerly-computed diff from ConfigReviewService, or falls back to
    /// computing a diff against the active config. Sets review mode properties on the ViewModel.
    /// </summary>
    void ApplyReviewDiffToViewModel(SettingItemViewModel viewModel, SettingStateResult currentState);
}
