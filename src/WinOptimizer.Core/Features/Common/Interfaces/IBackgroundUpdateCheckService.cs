using System;
using System.Threading;
using System.Threading.Tasks;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Core.Features.Common.Interfaces;

/// <summary>Runs daily automatic update checks after the application becomes ready.</summary>
public interface IBackgroundUpdateCheckService
{
    Task RunAsync(Action<VersionInfo> updateAvailable, CancellationToken cancellationToken);
}
