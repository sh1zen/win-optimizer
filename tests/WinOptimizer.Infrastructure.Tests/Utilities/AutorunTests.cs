using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Exercises snapshots, write authorization and transport parsing without touching Windows startup.</summary>
public sealed class AutorunTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "WinOptimizerStorageTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private readonly Backend _backend = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly Mock<IScheduledTaskService> _scheduledTasks = new(MockBehavior.Strict);
    internal static readonly AutorunEntry Entry = new("registry:1", "Example", "Logon", "HKCU\\Run", "example.exe --start", "example.exe", "Example Ltd", "", "User", true, true, "Original entry");

    private AutorunService Create(Func<string>? startupExecutable = null)
    {
        _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        return startupExecutable is null
            ? new(_backend, _mode.Object, _user.Object, _scheduledTasks.Object)
            : new(_backend, _mode.Object, _user.Object, _scheduledTasks.Object, startupExecutable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplicationStartupUsesTheSignedInAccountsTaskAndNormalShellLaunch(bool enable)
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        const string executable = @"C:\Program Files\win-optimizer\win-optimizer.exe";
        _user.SetupGet(user => user.InteractiveUserSid).Returns(sid);
        _scheduledTasks.Setup(tasks => tasks.ConfigureUserLogonApplicationAsync($"WinOptimizer-{sid}",
                enable ? executable : "", "", sid, enable)).ReturnsAsync(OperationResult.Succeeded());
        _scheduledTasks.Setup(tasks => tasks.IsUserLogonApplicationEnabledAsync($"WinOptimizer-{sid}")).ReturnsAsync(enable);
        var service = Create(() => enable ? executable : throw new InvalidOperationException("Disabling only needs the task name."));

        await service.SetApplicationStartupAsync(enable);

        Assert.Equal(enable, await service.ReadApplicationStartupAsync());
        _scheduledTasks.VerifyAll();
    }

    [Fact]
    public async Task AlternateAccountCannotConfigureAnElevatedLogonTask()
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        var service = Create();

        Assert.Null(await service.ReadApplicationStartupAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetApplicationStartupAsync(true));
        _scheduledTasks.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationStartupRequiresAnAdministratorControlledPayload(bool allowUserWrites)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new FileSecurity();
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users,
            allowUserWrites ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, AccessControlType.Allow));

        if (allowUserWrites)
            Assert.Throws<UnauthorizedAccessException>(() => ApplicationStartupExecutable.ValidatePermissions(security, "win-optimizer.dll"));
        else ApplicationStartupExecutable.ValidatePermissions(security, "win-optimizer.dll");
    }

    [Fact]
    public void ConstructionPerformsNoDiscovery() { _ = Create(); Assert.Equal(0, _backend.Reads); }

    [Fact]
    public async Task AdvancedScanRequiresExplicitLicenseConsent()
    {
        await Assert.ThrowsAsync<UtilityOperationException>(() => Create().ReadAsync(AutorunView.Advanced, false, default));
        Assert.Equal(0, _backend.Reads);
    }

    [Fact]
    public async Task AdvancedInventoryCannotAuthorizeWritesEvenWhenAnEntryClaimsItCan()
    {
        var service = Create(); await service.ReadAsync(AutorunView.Advanced, true, default);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetEnabledAsync(Entry.Id, false, default));
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task ChangeUsesTheExactScannedEntryAndRequiresARefreshForTheNextChange()
    {
        var service = Create(); await service.ReadAsync(AutorunView.Startup, false, default);
        await service.SetEnabledAsync(Entry.Id, false, default);
        Assert.Same(Entry, Assert.Single(_backend.Writes).Entry);
        Assert.False(_backend.Writes[0].Enabled);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetEnabledAsync(Entry.Id, true, default));
    }

    [Fact]
    public async Task UnscannedIdentityCannotChooseAnArbitraryWindowsTarget()
    {
        var service = Create(); await service.ReadAsync(AutorunView.Startup, false, default);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetEnabledAsync("forged:target", false, default));
        Assert.Empty(_backend.Writes);
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, null)]
    public async Task ReadOnlyOrUnknownStateCannotBeChanged(bool canToggle, bool? enabled)
    {
        _backend.Entries = [Entry with { CanToggle = canToggle, Enabled = enabled }];
        var service = Create(); await service.ReadAsync(AutorunView.Startup, false, default);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetEnabledAsync(Entry.Id, false, default));
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)] [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task NonNormalModesAllowInventoryButRejectWrites(WinOptimizerMode mode)
    {
        _mode.SetupGet(value => value.CurrentMode).Returns(mode);
        var service = Create(); await service.ReadAsync(AutorunView.Startup, false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(Entry.Id, false, default));
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task ElevationUnderAnotherAccountDoesNotSilentlyEditTheWrongProfile()
    {
        _user.SetupGet(value => value.IsOtsElevation).Returns(true);
        await Assert.ThrowsAsync<UtilityOperationException>(() => Create().ReadAsync(AutorunView.Startup, false, default));
        var inventory = await Create().ReadAsync(AutorunView.Advanced, true, default);
        Assert.Equal(AutorunView.Advanced, inventory.View);
    }

    [Fact]
    public async Task AFailedRefreshRevokesThePreviousWriteSnapshot()
    {
        var service = Create(); await service.ReadAsync(AutorunView.Startup, false, default);
        _backend.FailRead = true;
        await Assert.ThrowsAsync<IOException>(() => service.ReadAsync(AutorunView.Startup, false, default));
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetEnabledAsync(Entry.Id, false, default));
    }

    [Fact]
    public async Task DuplicateIdentitiesAreRejected()
    {
        _backend.Entries = [Entry, Entry];
        await Assert.ThrowsAsync<UtilityOperationException>(() => Create().ReadAsync(AutorunView.Startup, false, default));
    }

    [Fact]
    public async Task AnAlreadyRequestedStateDoesNotWrite()
    {
        var service = Create(); await service.ReadAsync(AutorunView.Startup, false, default);
        await service.SetEnabledAsync(Entry.Id, true, default); Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task ACancelledWriteDoesNotReachTheOperatingSystem()
    {
        var service = Create(); await service.ReadAsync(AutorunView.Startup, false, default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetEnabledAsync(Entry.Id, false, cancellation.Token));
        Assert.Empty(_backend.Writes);
    }

    [Fact]
    public async Task TransportUsesAnEmbeddedScriptAndEncodedDataThenDeletesTheScript()
    {
        var processes = new Mock<IProcessExecutor>(); string? scriptPath = null;
        var supplied = Entry with { Id = "quote\"; $(not executable)\nvalue" };
        processes.Setup(value => value.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync((string executable, string arguments, CancellationToken _) =>
            {
                Assert.EndsWith("WindowsPowerShell\\v1.0\\powershell.exe", executable, StringComparison.OrdinalIgnoreCase);
                var encoded = arguments[(arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + "-EncodedCommand ".Length)..];
                var loader = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
                var pathMatch = Regex.Match(loader, "FromBase64String\\('([A-Za-z0-9+/=]+)'\\)");
                scriptPath = Encoding.UTF8.GetString(Convert.FromBase64String(pathMatch.Groups[1].Value));
                var match = Regex.Match(loader, "-RequestBase64 '([A-Za-z0-9+/=]+)'$");
                Assert.True(match.Success);
                Assert.True(File.Exists(scriptPath)); Assert.Contains("function Write-Registry", File.ReadAllText(scriptPath));
                Assert.Contains("ComputeHash($b)", loader); Assert.Contains("scriptblock]::Create", loader);
                using var data = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value)));
                Assert.Equal(supplied.Id, data.RootElement.GetProperty("Entry").GetProperty("Id").GetString());
                Assert.False(data.RootElement.GetProperty("Enabled").GetBoolean());
                return new ProcessExecutionResult { StandardOutput = "{\"Applied\":true}" };
            });
        await new AutorunPowerShellBackend(processes.Object, _temporaryDirectory).SetEnabledAsync(supplied, false);
        Assert.NotNull(scriptPath); Assert.False(File.Exists(scriptPath));
    }

    [Theory]
    [InlineData("{}")] [InlineData("null")] [InlineData("{\"Entries\":[],\"Warnings\":null,\"Coverage\":\"x\"}")]
    public async Task IncompleteTransportOutputIsNotAnEmptySuccessfulScan(string output)
    {
        var processes = Processes(output);
        await Assert.ThrowsAsync<UtilityOperationException>(() => new AutorunPowerShellBackend(processes.Object, _temporaryDirectory).ReadAsync(AutorunView.Startup, false, default));
    }

    [Fact]
    public async Task TransportRetainsAccessWarningsAndUnknownStates()
    {
        var entry = Entry with { Enabled = null, CanToggle = false };
        var processes = Processes(JsonSerializer.Serialize(new { Entries = new[] { entry }, Warnings = new[] { "Access denied: source" }, Coverage = "Current account" }));
        var inventory = await new AutorunPowerShellBackend(processes.Object, _temporaryDirectory).ReadAsync(AutorunView.Startup, false, default);
        Assert.Equal("Access denied: source", Assert.Single(inventory.Warnings));
        Assert.Null(Assert.Single(inventory.Entries).Enabled);
    }

    [Fact]
    public async Task NativeFailurePropagatesTheActualDiagnostic()
    {
        var processes = Processes("", 5, "Access denied");
        var error = await Assert.ThrowsAsync<UtilityOperationException>(() => new AutorunPowerShellBackend(processes.Object, _temporaryDirectory).SetEnabledAsync(Entry, false));
        Assert.Contains("Access denied", error.Message);
    }

    private static Mock<IProcessExecutor> Processes(string output, int exitCode = 0, string error = "")
    {
        var processes = new Mock<IProcessExecutor>();
        processes.Setup(value => value.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(new ProcessExecutionResult { ExitCode = exitCode, StandardOutput = output, StandardError = error });
        return processes;
    }

    private sealed class Backend : IAutorunBackend
    {
        public AutorunEntry[] Entries = [Entry]; public int Reads; public bool FailRead;
        public List<(AutorunEntry Entry, bool Enabled)> Writes { get; } = [];
        public Task<AutorunInventory> ReadAsync(AutorunView view, bool acceptLicense, CancellationToken cancellationToken)
        { Reads++; if (FailRead) throw new IOException("Source unavailable"); return Task.FromResult(new AutorunInventory(view, DateTimeOffset.Now, Entries, [], "Fake boundary")); }
        public Task SetEnabledAsync(AutorunEntry entry, bool enabled) { Writes.Add((entry, enabled)); return Task.CompletedTask; }
        public Task OpenStartupSettingsAsync() => Task.CompletedTask;
    }
}
