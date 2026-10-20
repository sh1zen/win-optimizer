using WinOptimizer.UI.Features.Common.Interfaces;

namespace WinOptimizer.UI.Features.Optimize.Interfaces;

/// <summary>
/// Marker interface identifying a feature ViewModel that belongs to the Optimize page.
/// Enables <see cref="IEnumerable{IOptimizationFeatureViewModel}"/> injection via DI.
/// </summary>
public interface IOptimizationFeatureViewModel : ISettingsFeatureViewModel
{
}
