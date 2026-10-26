using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Checks native command ownership with a fake machine/account registration inventory.</summary>
public sealed class WindowsUninstallCommandSourceTests
{
    private readonly Mock<IInstalledProgramSource> _programs = new(MockBehavior.Strict);
    private WindowsUninstallCommandSource CreateSource() => new(Mock.Of<ILogService>(), _programs.Object);

    [Theory]
    [InlineData("Git", "GitHub Desktop")]
    [InlineData("Notepad++", "Notepad")]
    [InlineData("Microsoft Visual Studio Code", "Microsoft Visual Studio")]
    public async Task PartialNamesCannotSupplyAnotherProductsCommand(string selected, string installed)
    {
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync([
            new InstalledProgram(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Other", installed, "other.exe")]);

        Assert.Equal((false, ""), await CreateSource().GetAsync(new() { Id = "selected", Name = selected, Description = "Test" }));

        _programs.Verify(source => source.ReadAsync(), Times.Once);
        _programs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExactNameSelectsItsRegistrationAndRereadsTheCommand()
    {
        var path = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Git_is1";
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync([
            new InstalledProgram(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\GitHub", "GitHub Desktop", "github.exe"),
            new InstalledProgram(path, "git", "old-command.exe")]);
        _programs.Setup(source => source.GetUninstallCommandAsync(path)).ReturnsAsync("fresh-command.exe");

        Assert.Equal((true, "fresh-command.exe"), await CreateSource().GetAsync(new() { Id = "git", Name = "Git", Description = "Test" }));

        _programs.Verify(source => source.GetUninstallCommandAsync(path), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogueFingerprintsMatchRegisteredNamesOrKeys(bool matchKey)
    {
        var path = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Product-1.2";
        var item = new ItemDefinition
        {
            Id = "product", Name = "Catalogue product", Description = "Test",
            RegistrySubKeyName = matchKey ? "Product-{version}" : null,
            RegistryDisplayName = matchKey ? null : "Product {version} ({arch})"
        };
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync([new InstalledProgram(path, "Product 1.2 (x64)", "cached.exe")]);
        _programs.Setup(source => source.GetUninstallCommandAsync(path)).ReturnsAsync("remove.exe");

        Assert.Equal((true, "remove.exe"), await CreateSource().GetAsync(item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleMatchingRegistrationsCannotSelectAnArbitraryCommand(bool secondHasCommand)
    {
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync([
            new InstalledProgram(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Product", "Product", "machine.exe"),
            new InstalledProgram(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Product", "Product", secondHasCommand ? "user.exe" : null)]);

        Assert.Equal((false, ""), await CreateSource().GetAsync(new() { Id = "product", Name = "Product", Description = "Test" }));

        _programs.Verify(source => source.ReadAsync(), Times.Once);
        _programs.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PythonCatalogueTargetsOnlyItsDeclaredVersion(bool selectedVersionInstalled)
    {
        var item = ExternalAppDefinitions.DevelopmentApps.GetDevelopmentApps().Items.Single(app => app.Id == "external-app-python313");
        var path = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Python313";
        List<InstalledProgram> registrations = [new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Python312", "Python 3.12.8 (64-bit)", "remove312.exe")];
        if (selectedVersionInstalled)
        {
            registrations.Add(new(path, "Python 3.13.1 (64-bit)", "old313.exe"));
            _programs.Setup(source => source.GetUninstallCommandAsync(path)).ReturnsAsync("remove313.exe");
        }
        _programs.Setup(source => source.ReadAsync()).ReturnsAsync(registrations);

        Assert.Equal(selectedVersionInstalled ? (true, "remove313.exe") : (false, ""), await CreateSource().GetAsync(item));
    }

    [Fact]
    public async Task UnreadableInventoryDoesNotAuthorizeNativeRemoval()
    {
        _programs.Setup(source => source.ReadAsync()).ThrowsAsync(new IOException("Registration access denied"));

        await Assert.ThrowsAsync<IOException>(() => CreateSource().GetAsync(new() { Id = "product", Name = "Product", Description = "Test" }));
    }
}
