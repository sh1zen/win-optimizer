using WinOptimizer.Application.Configuration;
using Xunit;

namespace WinOptimizer.Application.Tests;

/// <summary>Protects the compile-time separation of workflows from desktop and Windows implementations.</summary>
public sealed class ApplicationBoundaryTests
{
    [Fact]
    public void ConfigurationAssembly_DoesNotReferencePresentationOrInfrastructure()
    {
        var references = typeof(ConfigurationService).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, r => r.Name is "win-optimizer" or "WinOptimizer.UI" or
            "WinOptimizer.Infrastructure" or "Microsoft.WinUI");
    }
}
