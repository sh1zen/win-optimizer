using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsComponentStatusSourceTests
{
    [Fact]
    public async Task GetStatusAsync_NoComponents_ReturnsEmptyCaseInsensitiveStatusWithoutNativeQueries()
    {
        var log = new Mock<ILogService>(MockBehavior.Strict);
        var result = await new WindowsComponentStatusSource(log.Object)
            .GetStatusAsync(Array.Empty<string>(), Array.Empty<string>());
        result.Capabilities.Should().BeEmpty();
        result.Features.Should().BeEmpty();
        result.Capabilities.Should().BeOfType<Dictionary<string, bool>>().Which.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
        result.Features.Should().BeOfType<Dictionary<string, bool>>().Which.Comparer.Should().Be(StringComparer.OrdinalIgnoreCase);
        log.VerifyNoOtherCalls();
    }
}
