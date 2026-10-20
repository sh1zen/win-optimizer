using FluentAssertions;
using FluentAssertions.Execution;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class SystemInfoProviderTests
{
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();

    [Fact]
    public void Constructor_NullInteractiveUserService_ThrowsArgumentNullException()
    {
        var act = () => new SystemInfoProvider(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("interactiveUserService");
    }

    [Fact]
    public void Collect_ReturnsCompleteRecognizedSystemInformation()
    {
        var provider = new SystemInfoProvider(_mockInteractiveUserService.Object);

        var info = provider.Collect();

        using var assertions = new AssertionScope();
        info.Should().NotBeNull();
        info.Architecture.Should().BeOneOf("x64", "x86", "arm64", "arm");
        info.OperatingSystem.Should().Contain("Windows");
        info.Cpu.Should().Contain("cores");
        info.Ram.Should().Contain("GB");
        info.DeviceType.Should().MatchRegex("Desktop|Laptop|Workstation|Slate|Server|Other");
        info.Elevation.Should().BeOneOf("Admin", "Admin (OTS)", "Standard");
        info.DotNetRuntime.Should().Contain(".NET");
        info.Gpu.Should().NotBeNullOrEmpty();
        info.FirmwareType.Should().BeOneOf("UEFI", "Legacy BIOS", "Unknown");
        info.SecureBoot.Should().BeOneOf("Enabled", "Disabled", "Not Supported", "Unknown");
    }
}
