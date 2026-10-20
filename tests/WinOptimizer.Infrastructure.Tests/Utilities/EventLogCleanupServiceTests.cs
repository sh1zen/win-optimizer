using Moq;
using System.Security.AccessControl;
using System.Security.Principal;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Exercises production orchestration without reading or clearing Windows event logs.</summary>
public sealed class EventLogCleanupServiceTests
{
    private readonly Store _store = new();
    private readonly Mock<IApplicationModeService> _mode = new();

    public EventLogCleanupServiceTests() => _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
    private EventLogCleanupService Create() => new(_store, _mode.Object);

    [Fact]
    public async Task DiscoverySortsRowsAndKeepsUnavailableMetadataWithoutWriting()
    {
        _store.Rows = [new("System", 20, 4096, ""), new("Application", null, null, "Access denied")];
        var rows = await Create().ReadAsync(CancellationToken.None);
        Assert.Equal(new[] { "Application", "System" }, rows.Select(row => row.Name));
        Assert.Equal("Access denied", rows[0].Diagnostic);
        Assert.Empty(_store.Cleared); Assert.Equal(0, _store.BackupDirectories);
        _mode.Verify(mode => mode.BeginSystemOperation(), Times.Never);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task NonNormalModesNeverReachTheWindowsWriteBoundary(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ClearAsync(["System"], null, CancellationToken.None));
        Assert.Empty(_store.Cleared); Assert.Equal(0, _store.BackupDirectories); Assert.Equal(0, _store.PermissionChecks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("../../Security")]
    public async Task InvalidOrUnregisteredNamesRejectTheWholeBatchBeforeCreatingArchives(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create().ClearAsync(["System", name], null, CancellationToken.None));
        Assert.Empty(_store.Cleared); Assert.Equal(0, _store.BackupDirectories);
    }

    [Fact]
    public async Task EmptyAndDuplicateRequestsAreRejected()
    {
        var service = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ClearAsync([], null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ClearAsync(["System", "system"], null, CancellationToken.None));
        Assert.Empty(_store.Cleared);
    }

    [Fact]
    public async Task MissingElevationCannotCreateAnArchiveOrClearLogs()
    {
        _store.Deny = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Create().ClearAsync(["System"], null, CancellationToken.None));
        Assert.Equal(0, _store.BackupDirectories); Assert.Empty(_store.Cleared);
    }

    [Fact]
    public async Task BackupPreparationFailureStopsTheBatchBeforeAnyClear()
    {
        _store.BackupFailure = true;
        await Assert.ThrowsAsync<IOException>(() => Create().ClearAsync(["System"], null, CancellationToken.None));
        Assert.Empty(_store.Cleared);
    }

    [Fact]
    public async Task PartialFailuresKeepSuccessfulReceiptsAndContinueToTheNextLog()
    {
        _store.Rows = [new("System", 20, 4096, ""), new("Security", 30, 4096, ""), new("Application", 5, 4096, "")];
        _store.Failing = "Security";
        var progress = new ProgressRecorder();
        var result = await Create().ClearAsync(["System", "Security", "Application"], progress, CancellationToken.None);
        Assert.False(result.Cancelled); Assert.Equal(3, result.Outcomes.Count);
        Assert.True(result.Outcomes[0].Cleared); Assert.Equal("archive/System.evtx", result.Outcomes[0].BackupPath);
        Assert.False(result.Outcomes[1].Cleared); Assert.Equal("Access denied", result.Outcomes[1].Error);
        Assert.True(result.Outcomes[2].Cleared);
        Assert.Equal(new[] { 1, 2, 3 }, progress.Values.Select(value => value.Completed));
        Assert.All(progress.Values, value => Assert.Equal(3, value.Total));
        Assert.Equal(1, _store.BackupDirectories);
    }

    [Fact]
    public async Task CancellationAfterOneClearReturnsItsArchiveAndDoesNotStartTheNextLog()
    {
        using var cancellation = new CancellationTokenSource();
        _store.AfterClear = cancellation.Cancel;
        var result = await Create().ClearAsync(["System", "Application"], null, cancellation.Token);
        Assert.True(result.Cancelled); Assert.True(Assert.Single(result.Outcomes).Cleared);
        Assert.Equal(new[] { "System" }, _store.Cleared);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotWrite()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().ClearAsync(["System"], null, cancellation.Token));
        Assert.Empty(_store.Cleared); Assert.Equal(0, _store.BackupDirectories);
    }

    [Theory]
    [InlineData(false, FileSystemRights.Read)]
    [InlineData(true, FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(true, FileSystemRights.ChangePermissions)]
    public void UntrustedArchiveAccessOrReplaceableParentIsRejected(bool container, FileSystemRights rights)
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), rights, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsEventLogStore.ValidateStorageAcl(security, container));
    }

    [Fact]
    public void ArchiveStorageRequiresATrustedOwner()
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        Assert.Throws<UnauthorizedAccessException>(() => WindowsEventLogStore.ValidateStorageAcl(security, false));
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        WindowsEventLogStore.ValidateStorageAcl(security, false);
    }

    private sealed class ProgressRecorder : IProgress<EventLogCleanupProgress>
    {
        public List<EventLogCleanupProgress> Values { get; } = [];
        public void Report(EventLogCleanupProgress value) => Values.Add(value);
    }

    private sealed class Store : IEventLogStore
    {
        public EventLogChannel[] Rows = [new("System", 20, 4096, ""), new("Application", 5, 4096, "")];
        public List<string> Cleared { get; } = [];
        public int PermissionChecks, BackupDirectories;
        public bool Deny, BackupFailure;
        public string Failing = "";
        public Action? AfterClear;
        public IReadOnlyList<string> GetLogNames() => Rows.Select(row => row.Name).ToArray();
        public EventLogChannel Read(string name) => Rows.Single(row => row.Name == name);
        public void EnsureCanClear() { PermissionChecks++; if (Deny) throw new UnauthorizedAccessException("Elevation required"); }
        public string CreateBackupDirectory() { BackupDirectories++; if (BackupFailure) throw new IOException("Disk full"); return "archive"; }
        public EventLogCleanupOutcome ClearWithBackup(string name, string directory)
        {
            Cleared.Add(name); AfterClear?.Invoke();
            return name == Failing ? new(name, false, "", "Access denied") : new(name, true, directory + "/" + name + ".evtx", "", Read(name));
        }
    }
}
