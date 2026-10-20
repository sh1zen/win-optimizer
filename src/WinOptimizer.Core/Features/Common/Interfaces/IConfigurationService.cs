using System.Threading.Tasks;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IConfigurationService
{
    Task ExportConfigurationAsync();
    Task ImportConfigurationAsync();
    Task ImportRecommendedConfigurationAsync();
    /// <summary>Confirms and applies the version-appropriate Windows defaults configuration.</summary>
    Task RestoreWindowsDefaultsAsync();
    Task CreateUserBackupConfigAsync();
    Task ApplyReviewedConfigAsync();
    Task CancelReviewModeAsync();
}
