using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsInstalledProgramSourceTests
{
    [Fact]
    public async Task InventoryReadsMachineAndInteractiveUserRegistrationsInBothViews()
    {
        var registry = new Mock<IWindowsRegistryService>(MockBehavior.Strict);
        foreach (var root in WindowsInstalledProgramSource.Roots)
        {
            registry.Setup(source => source.GetSubKeyNames(root)).Returns(["Tool"]);
            var path = root + @"\Tool";
            registry.Setup(source => source.GetValue(path, It.IsAny<string>())).Returns((string _, string value) => value switch
            {
                "DisplayName" => "Installed tool",
                "UninstallString" => "uninstall.exe",
                "DisplayIcon" => "\"C:\\Fixture\\app.exe\",-7",
                _ => null,
            });
        }
        var programs = await new WindowsInstalledProgramSource(registry.Object).ReadAsync();
        Assert.Equal(WindowsInstalledProgramSource.Roots.Select(root => root + @"\Tool"), programs.Select(program => program.KeyPath));
        Assert.Equal(4, programs.Select(program => program.ToDefinition().Id).Distinct().Count());
        Assert.All(programs, program => Assert.True(program.IsVisible));
        Assert.All(programs, program => Assert.Equal("\"C:\\Fixture\\app.exe\",-7", program.DisplayIcon));
    }

    [Fact]
    public async Task CommandLookupRereadsOnlyTheExactRegistration()
    {
        var registry = new Mock<IWindowsRegistryService>(MockBehavior.Strict);
        var path = WindowsInstalledProgramSource.Roots[3] + @"\Poedit_is1";
        registry.SetupSequence(source => source.GetValue(path, "UninstallString"))
            .Returns("original.exe").Returns("updated.exe").Returns(() => null);
        var source = new WindowsInstalledProgramSource(registry.Object);
        var commands = new WindowsUninstallCommandSource(Mock.Of<IInteractiveUserService>(), Mock.Of<ILogService>(), source);
        var item = new InstalledProgram(path, "Poedit", "unused-old-command.exe").ToDefinition();
        Assert.Equal((true, "original.exe"), await commands.GetAsync(item));
        Assert.Equal((true, "updated.exe"), await commands.GetAsync(item));
        Assert.Equal((false, ""), await commands.GetAsync(item));
        registry.Verify(service => service.GetValue(path, "UninstallString"), Times.Exactly(3));
        registry.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Elsewhere\Program")]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Program\Child")]
    public async Task CommandLookupRejectsPathsOutsideIndividualUninstallRegistrations(string path)
    {
        var registry = new Mock<IWindowsRegistryService>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ArgumentException>(() => new WindowsInstalledProgramSource(registry.Object).GetUninstallCommandAsync(path));
        registry.VerifyNoOtherCalls();
    }

    [Fact]
    public void DiscoveredIdentityIsCaseInsensitiveAndDoesNotExposeUnsafeWebsiteSchemes()
    {
        var program = new InstalledProgram(WindowsInstalledProgramSource.Roots[0] + @"\Tool", "Tool", "remove.exe", WebsiteUrl: "file:///C:/tool.exe");
        Assert.Equal(program.ToDefinition().Id, (program with { KeyPath = program.KeyPath.ToLowerInvariant() }).ToDefinition().Id);
        Assert.Null(program.ToDefinition().WebsiteUrl);
    }
}
