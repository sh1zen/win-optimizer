using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Native;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>
/// Delegates to the native User32Api.SystemParametersInfo P/Invoke.
/// </summary>
public class SystemParametersService : ISystemParametersService
{
    public int SystemParametersInfo(int uAction, int uParam, string? lpvParam, int fuWinIni)
    {
        return User32Api.SystemParametersInfo(uAction, uParam, lpvParam, fuWinIni);
    }
}
