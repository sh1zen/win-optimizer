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
    public void Collect_FormatsKnownHardwareAndUsesDeviceFallbackFromSyntheticWmi()
    {
        var provider = new SystemInfoProvider(_mockInteractiveUserService.Object, (_, query) =>
        {
            Dictionary<string, object?> row = (query.Split(" FROM ")[1]) switch
            {
                "Win32_ComputerSystem" => new()
                {
                    ["PCSystemType"] = 0, ["Model"] = "Fixture", ["Manufacturer"] = "Example",
                    ["TotalPhysicalMemory"] = 16L * 1024 * 1024 * 1024, ["PartOfDomain"] = true, ["Domain"] = "fixture.invalid"
                },
                "Win32_SystemEnclosure" => new() { ["ChassisTypes"] = new ushort[] { 9 } },
                "Win32_Processor" => new() { ["Name"] = "Fixture CPU", ["NumberOfLogicalProcessors"] = 8 },
                "Win32_VideoController" => new() { ["Name"] = "NVIDIA GeForce Fixture", ["AdapterDACType"] = "" },
                "Win32_Tpm" => new() { ["SpecVersion"] = "2.0, 0, 1.59" },
                _ => throw new InvalidOperationException($"Unexpected WMI query: {query}")
            };
            return [property => row[property]];
        });

        var info = provider.Collect();

        using var assertions = new AssertionScope();
        info.Should().NotBeNull();
        info.Architecture.Should().BeOneOf("x64", "x86", "arm64", "arm");
        info.OperatingSystem.Should().Contain("Windows");
        info.Cpu.Should().Be("Fixture CPU (8 cores)");
        info.Ram.Should().Be("16 GB");
        info.DeviceType.Should().Be("Laptop");
        info.DomainJoined.Should().Be("Yes (fixture.invalid)");
        info.Elevation.Should().BeOneOf("Admin", "Admin (OTS)", "Standard");
        info.DotNetRuntime.Should().Contain(".NET");
        info.Gpu.Should().StartWith("NVIDIA GeForce Fixture (");
        info.Tpm.Should().Be("2.0");
        info.FirmwareType.Should().BeOneOf("UEFI", "Legacy BIOS", "Unknown");
        info.SecureBoot.Should().BeOneOf("Enabled", "Disabled", "Not Supported", "Unknown");
    }

    [Fact]
    public void Collect_ReportsUnavailableWmiWithoutInventingHardware()
    {
        var provider = new SystemInfoProvider(_mockInteractiveUserService.Object,
            (_, _) => throw new UnauthorizedAccessException("Synthetic WMI boundary is unavailable."));

        var info = provider.Collect();

        info.Cpu.Should().Be("Unknown");
        info.Ram.Should().Be("Unknown");
        info.DeviceType.Should().Be("Unknown");
        info.DomainJoined.Should().Be("Unknown");
        info.Gpu.Should().Be("Unknown");
        info.Tpm.Should().Be("Not Detected");
    }
}
