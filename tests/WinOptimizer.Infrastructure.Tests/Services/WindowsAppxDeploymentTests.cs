using System.Text;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Checks account and process boundaries without querying or removing Windows packages.</summary>
public sealed class WindowsAppxDeploymentTests
{
    private readonly Mock<IInteractiveUserService> _user = new(MockBehavior.Strict);
    private readonly Mock<IProcessExecutor> _processes = new(MockBehavior.Strict);
    private WindowsAppxDeployment CreateSource() => new(_user.Object, _processes.Object, Mock.Of<ILogService>());

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task AlternateAccountInstallationUsesItsTokenAndChecksTheExit(int exitCode)
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        _user.SetupGet(user => user.InteractiveUserSid).Returns("S-1-5-21-1-2-3-1001");
        _user.SetupGet(user => user.HasInteractiveUserToken).Returns(true);
        string? script = null;
        _user.Setup(user => user.RunProcessAsInteractiveUserAsync(It.IsAny<string>(), It.IsAny<string>(),
            null, null, CancellationToken.None, Timeout.Infinite, null))
            .Callback<string, string, Action<string>?, Action<string>?, CancellationToken, int, Action<string>?>((executable, arguments, _, _, _, _, _) =>
            {
                Assert.Equal(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), executable);
                script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments.Split(' ')[^1]));
            }).ReturnsAsync(new InteractiveProcessResult(exitCode, "", "fixture error"));
        var install = () => CreateSource().InstallPackageAsync(@"C:\Fixture's packages\App.msix", [@"C:\Fixture's packages\Framework.appx"], CancellationToken.None);

        if (exitCode == 0) await install();
        else Assert.Contains("fixture error", (await Assert.ThrowsAsync<IOException>(install)).Message);

        Assert.Equal("Add-AppxPackage -Path 'C:\\Fixture''s packages\\App.msix' -ErrorAction Stop -DependencyPath @('C:\\Fixture''s packages\\Framework.appx')", script);
        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AlternateAccountInstallationWithoutATokenFailsBeforeAnyDeployment()
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        _user.SetupGet(user => user.InteractiveUserSid).Returns("S-1-5-21-1-2-3-1001");
        _user.SetupGet(user => user.HasInteractiveUserToken).Returns(false);

        await Assert.ThrowsAsync<IOException>(() => CreateSource().InstallPackageAsync(@"C:\Fixture\App.msix", [], CancellationToken.None));

        _processes.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, null, "")]
    [InlineData(true, "S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001")]
    public void InteractiveAccountUsesTheDetectedIdentity(bool alternate, string? sid, string expected)
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(alternate);
        _user.SetupGet(user => user.InteractiveUserSid).Returns(sid);

        Assert.Equal(expected, CreateSource().InteractiveAccountSid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingAlternateAccountFailsBeforeAnyOsOperation(string? sid)
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        _user.SetupGet(user => user.InteractiveUserSid).Returns(sid);

        Assert.Throws<IOException>(() => CreateSource().InteractiveAccountSid);

        _processes.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExplicitRemovalTargetsOnlyOneSidAndChecksTheProcessExit(int exitCode)
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        string? script = null;
        _processes.Setup(processes => processes.ExecuteAsync(It.Is<string>(path => path.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe")),
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, arguments, _) =>
                script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments.Split(' ')[^1])))
            .ReturnsAsync(new ProcessExecutionResult { ExitCode = exitCode, StandardError = exitCode == 0 ? "" : "Access denied" });
        var operation = () => CreateSource().RemovePackageAsync("Test.App_1_x64__publisher", sid, _ => { }, CancellationToken.None);

        if (exitCode == 0) await operation();
        else Assert.Contains("Access denied", (await Assert.ThrowsAsync<IOException>(operation)).Message);

        Assert.Equal($"Remove-AppxPackage -Package 'Test.App_1_x64__publisher' -User '{sid}' -ErrorAction Stop", script);
        _user.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RemovalQuotesPackageIdentityAsOneLiteral()
    {
        string? script = null;
        _processes.Setup(processes => processes.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, arguments, _) =>
                script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments.Split(' ')[^1])))
            .ReturnsAsync(new ProcessExecutionResult());

        await CreateSource().RemovePackageAsync("App'; other-command", "S-1-5-21-1-2-3-1001", _ => { }, CancellationToken.None);

        Assert.StartsWith("Remove-AppxPackage -Package 'App''; other-command' -User ", script);
    }

    [Fact]
    public async Task CancellationBeforeEnumerationOrRemovalDoesNotQueryWindows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = CreateSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetPackagesAsync("", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.RemovePackageAsync("Test.App", "", _ => { }, cancellation.Token));

        _processes.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21-1-2-3-1001\\Software")]
    public async Task InvalidAccountCannotReachEnumerationOrRemoval(string sid)
    {
        var source = CreateSource();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => source.GetPackagesAsync(sid, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => source.RemovePackageAsync("Test.App", sid, _ => { }, CancellationToken.None));

        _processes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CancellationAfterTheWorkerReturnsIsNotSuccessfulRemoval()
    {
        using var cancellation = new CancellationTokenSource();
        _processes.Setup(processes => processes.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel()).ReturnsAsync(new ProcessExecutionResult());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateSource().RemovePackageAsync(
            "Test.App", "S-1-5-21-1-2-3-1001", _ => { }, cancellation.Token));
    }
}
