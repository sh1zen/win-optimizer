using System.Threading.Tasks;

namespace WinOptimizer.Core.Features.Common.Interfaces;

public interface IGlobalSettingsPreloader
{
    Task PreloadAllSettingsAsync();
    bool IsPreloaded { get; }
}
