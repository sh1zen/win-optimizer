// File: src/WinOptimizer.Infrastructure/Features/Common/Services/SpecialSettingHandlerRegistry.cs
using System.Collections.Generic;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public sealed class SpecialSettingHandlerRegistry(IReadOnlyDictionary<string, ISpecialSettingHandler> handlers)
    : ISpecialSettingHandlerRegistry
{
    public ISpecialSettingHandler? TryGet(string settingId)
        => handlers.TryGetValue(settingId, out var h) ? h : null;
}
