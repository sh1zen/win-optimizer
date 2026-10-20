// File: src/WinOptimizer.Infrastructure/Features/Common/Services/SpecialDiscoveryRegistry.cs
using System.Collections.Generic;
using WinOptimizer.Core.Features.Common.Interfaces;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

public sealed class SpecialDiscoveryRegistry(IReadOnlyList<ISpecialSettingHandler> handlers)
    : ISpecialDiscoveryRegistry
{
    public IEnumerable<ISpecialSettingHandler> All => handlers;
}
