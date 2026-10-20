using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks the ordinary and advanced UI flows using fake inventory, dialogs and Windows settings.</summary>
public sealed class AutorunsViewModelTests : IDisposable
{
    private readonly Mock<IAutorunService> _service = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly List<CancellationTokenSource> _sources = [];
    private CancellationTokenSource? _current;
    private AutorunEntry[] _entries = [new("run:1", "Example startup", "Logon", "HKCU\\Run", "C:\\Tools\\Example.exe --startup", "C:\\Tools\\Example.exe", "Example Ltd", "", "Current user", true, true, "Original value preserved")];

    public AutorunsViewModelTests()
    {
        _localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key == "Autoruns_Count" ? "{0} / {1}" : key);
        _tasks.SetupGet(source => source.CurrentTaskCancellationSource).Returns(() => _current);
        _tasks.Setup(source => source.StartTask(It.IsAny<string>(), true, true, false)).Returns(() => { _current = new(); _sources.Add(_current); return _current; });
        _tasks.Setup(source => source.CompleteTask()).Callback(() => _current = null);
        _dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _service.Setup(source => source.ReadAsync(It.IsAny<AutorunView>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AutorunView view, bool _, CancellationToken _) => new AutorunInventory(view, new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), _entries, [], "Fake current account"));
        _service.Setup(source => source.SetEnabledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((string id, bool enabled, CancellationToken _) => _entries = _entries.Select(entry => entry.Id == id ? entry with { Enabled = enabled } : entry).ToArray())
            .Returns(Task.CompletedTask);
    }

    private AutorunsViewModel Create() => new(_service.Object, _dialogs.Object, _localization.Object, _mode.Object, new ExtensionTestDispatcher(), _tasks.Object);

    [Theory]
    [InlineData("CurrentUser", "HKCU\\Run", AutorunScope.User)]
    [InlineData("Current user", "", AutorunScope.User)]
    [InlineData("EXAMPLE\\TestUser", "", AutorunScope.User)]
    [InlineData("S-1-5-21-1-2-3-1001", "\\ExampleTask", AutorunScope.User)]
    [InlineData("LocalMachine", "HKLM\\Run", AutorunScope.AllUsers)]
    [InlineData("All users", "", AutorunScope.AllUsers)]
    [InlineData("System", "", AutorunScope.System)]
    [InlineData(" NT AUTHORITY\\SYSTEM ", "", AutorunScope.System)]
    [InlineData("LocalSystem", "", AutorunScope.System)]
    [InlineData("NT AUTHORITY\\LOCAL SERVICE", "", AutorunScope.System)]
    [InlineData("NetworkService", "", AutorunScope.System)]
    [InlineData("S-1-5-18", "\\ExampleTask", AutorunScope.System)]
    [InlineData("S-1-5-19", "", AutorunScope.System)]
    [InlineData("S-1-5-20", "", AutorunScope.System)]
    [InlineData("", "HKEY_LOCAL_MACHINE\\Software\\Example", AutorunScope.AllUsers)]
    [InlineData("", "HKLM\\Software\\Example", AutorunScope.AllUsers)]
    [InlineData("", "HKEY_CURRENT_USER\\Software\\Example", AutorunScope.User)]
    [InlineData("", "HKCU\\Software\\Example", AutorunScope.User)]
    [InlineData("", "\\ExampleTask", AutorunScope.Unspecified)]
    [InlineData(" ", "", AutorunScope.Unspecified)]
    public void ScopeUsesReportedProfilesAndExplicitRegistryHives(string profile, string location, AutorunScope expected)
    {
        var row = new AutorunRow(_entries[0] with { Profile = profile, Location = location });
        Assert.Equal(expected, row.Scope);
    }

    [Fact]
    public async Task FilteredGroupsRetainRowsOrderingAndSelectionWithoutRescanning()
    {
        _entries = [
            _entries[0] with { Id = "user-z", Name = "Zulu", Profile = "CurrentUser" },
            _entries[0] with { Id = "system", Profile = "S-1-5-18" },
            _entries[0] with { Id = "user-a", Name = "Alpha", Profile = "EXAMPLE\\TestUser" },
            _entries[0] with { Id = "machine", Profile = "LocalMachine" },
            _entries[0] with { Id = "unknown", Profile = "", Location = "\\ExampleTask" }
        ];
        using var vm = Create(); await vm.LoadAsync();
        Assert.Equal(new[] { AutorunScope.User, AutorunScope.AllUsers, AutorunScope.System, AutorunScope.Unspecified }, vm.Groups.Select(group => group.Scope));
        Assert.Equal(new[] { "user-a", "user-z" }, vm.Groups[0].Rows.Select(row => row.Entry.Id));
        Assert.All(vm.Groups.SelectMany(group => group.Rows), row => Assert.Same(vm.Rows.Single(item => item.Entry.Id == row.Entry.Id), row));
        var selected = vm.Groups[0].Rows[0]; vm.SelectedRow = selected;
        vm.SearchText = "Alpha";
        Assert.Same(selected, Assert.Single(vm.Rows)); Assert.Same(selected, vm.SelectedRow);
        Assert.Same(selected, Assert.Single(Assert.Single(vm.Groups).Rows));
        Assert.Equal("Autoruns_Scope_User (1)", vm.Groups[0].Header);
        vm.SearchText = "not a registration";
        Assert.Empty(vm.Groups); Assert.Null(vm.SelectedRow);
        vm.ClearFiltersCommand.Execute(null); Assert.Equal(4, vm.Groups.Count);
        _service.Verify(source => source.ReadAsync(AutorunView.Startup, false, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task LanguageChangesRefreshHeadersWithoutReplacingRowsOrSelection()
    {
        using var vm = Create(); await vm.LoadAsync();
        var group = Assert.Single(vm.Groups); var row = Assert.Single(vm.Rows); vm.SelectedRow = row;
        var changed = false;
        group.PropertyChanged += (_, args) => changed |= args.PropertyName == nameof(AutorunGroup.Header);
        _localization.Setup(source => source.GetString("Autoruns_Scope_User")).Returns("Account utente");
        _localization.Raise(source => source.LanguageChanged += null, EventArgs.Empty);
        Assert.True(changed); Assert.Equal("Account utente (1)", group.Header);
        Assert.Same(group, Assert.Single(vm.Groups)); Assert.Same(row, vm.SelectedRow);
    }

    [Fact]
    public void ConstructionDoesNotScanDownloadOrAcceptTheLicense()
    {
        using var vm = Create(); Assert.False(vm.Advanced); Assert.False(vm.LicenseAccepted);
        Assert.False(vm.HideVerifiedMicrosoft); Assert.Empty(vm.Rows); _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FirstEntryLoadsOnlyOrdinarySourcesAndReentryKeepsTheSnapshot()
    {
        using var vm = Create(); await vm.LoadAsync(); await vm.LoadAsync();
        _service.Verify(source => source.ReadAsync(AutorunView.Startup, false, CancellationToken.None), Times.Once);
        Assert.Same(_entries[0], Assert.Single(vm.Rows).Entry);
    }

    [Fact]
    public async Task AdvancedModeWaitsForConsentThenScansAutomatically()
    {
        using var vm = Create(); vm.Advanced = true;
        Assert.False(vm.RefreshCommand.CanExecute(null)); await vm.LoadAsync(); await vm.RefreshCommand.ExecuteAsync(null);
        _service.VerifyNoOtherCalls();
        Assert.Equal("Autoruns_ConsentRequired", vm.EmptyLabel);
        vm.LicenseAccepted = true;
        await vm.RefreshCommand.ExecutionTask!;
        Assert.Single(vm.Rows); Assert.True(vm.RefreshCommand.CanExecute(null));
        _service.Verify(source => source.ReadAsync(AutorunView.Advanced, true, CancellationToken.None), Times.Once);
    }

    [Fact]
    public void ConsentInOrdinaryModeDoesNotStartAnAdvancedScan()
    {
        using var vm = Create(); vm.LicenseAccepted = true;
        Assert.Empty(vm.Rows); _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SwitchingModesAutomaticallyLoadsTheSelectedSourcesWithExistingConsent()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0];
        vm.LicenseAccepted = true; vm.Advanced = true;
        await vm.RefreshCommand.ExecutionTask!;
        Assert.Null(vm.SelectedRow); Assert.Single(vm.Rows);
        vm.SelectedRow = vm.Rows[0]; Assert.False(vm.CanDisable);
        vm.Advanced = false; await vm.RefreshCommand.ExecutionTask!;
        Assert.Null(vm.SelectedRow); Assert.Single(vm.Rows);
        vm.SelectedRow = vm.Rows[0]; Assert.True(vm.CanDisable);
        vm.Advanced = true; await vm.RefreshCommand.ExecutionTask!;
        _service.Verify(source => source.ReadAsync(AutorunView.Startup, false, CancellationToken.None), Times.Once);
        _service.Verify(source => source.ReadAsync(AutorunView.Startup, true, CancellationToken.None), Times.Once);
        _service.Verify(source => source.ReadAsync(AutorunView.Advanced, true, CancellationToken.None), Times.Exactly(2));
    }

    [Fact]
    public async Task AutomaticScanUsesTheBusyStateAndPublishesTheCompletedInventory()
    {
        var pending = new TaskCompletionSource<AutorunInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(source => source.ReadAsync(AutorunView.Advanced, true, CancellationToken.None)).Returns(pending.Task);
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0];
        vm.LicenseAccepted = true; vm.Advanced = true;
        Assert.True(vm.IsBusy); Assert.Empty(vm.Rows); Assert.Null(vm.SelectedRow);
        Assert.False(vm.RefreshCommand.CanExecute(null)); Assert.False(vm.CanDisable);
        pending.SetResult(new(AutorunView.Advanced, new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), _entries, [], "Synthetic account"));
        await vm.RefreshCommand.ExecutionTask!;
        Assert.True(vm.IsIdle); Assert.Single(vm.Rows); Assert.False(vm.IsError);
        await vm.LoadAsync();
        _service.Verify(source => source.ReadAsync(AutorunView.Advanced, true, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task FailedAutomaticScanExposesTheErrorAndAllowsRefreshToRetry()
    {
        _service.SetupSequence(source => source.ReadAsync(AutorunView.Advanced, true, CancellationToken.None))
            .ThrowsAsync(new IOException("Advanced scan failed"))
            .ReturnsAsync(new AutorunInventory(AutorunView.Advanced, new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), _entries, [], "Synthetic account"));
        using var vm = Create(); await vm.LoadAsync(); vm.LicenseAccepted = true; vm.Advanced = true;
        await vm.RefreshCommand.ExecutionTask!;
        Assert.True(vm.IsError); Assert.Equal("Advanced scan failed", vm.Status); Assert.Empty(vm.Rows);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.IsError); Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task ChangingViewsClearsTheOtherViewsSelectionAndControls()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0]; Assert.True(vm.CanDisable);
        vm.Advanced = true; Assert.Empty(vm.Rows); Assert.Null(vm.SelectedRow); Assert.False(vm.CanDisable);
    }

    [Fact]
    public async Task SearchMatchesFullCommandAndProfileWithoutRescanning()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.SearchText = "TOOLS\\example"; Assert.Single(vm.Rows);
        vm.SearchText = "current USER"; Assert.Single(vm.Rows);
        vm.SearchText = "unmatched"; Assert.Empty(vm.Rows); Assert.True(vm.IsEmpty);
        vm.ClearFiltersCommand.Execute(null); Assert.Single(vm.Rows);
        _service.Verify(source => source.ReadAsync(AutorunView.Startup, false, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task CategoryAndStateFiltersDistinguishUnknownFromDisabled()
    {
        _entries = [_entries[0], _entries[0] with { Id = "task:1", Category = "Scheduled tasks", Enabled = false },
            _entries[0] with { Id = "app:1", Category = "Windows apps", Enabled = null, CanToggle = false }];
        using var vm = Create(); await vm.LoadAsync();
        vm.StateFilter = 3; Assert.Null(Assert.Single(vm.Rows).Entry.Enabled);
        vm.StateFilter = 2; Assert.False(Assert.Single(vm.Rows).Entry.Enabled);
        vm.StateFilter = 0; vm.SelectedCategory = "Windows apps"; Assert.Single(vm.Rows);
        Assert.Equal("1 / 3", vm.CountSummary);
    }

    [Fact]
    public async Task MicrosoftFilterUsesVerifiedSignerInsteadOfAnUntrustedCompanyName()
    {
        _entries = [_entries[0] with { Id = "signed", Signer = "(Verified) Microsoft Windows" },
            _entries[0] with { Id = "unsigned", Publisher = "Microsoft Corporation" },
            _entries[0] with { Id = "third-party", Signer = "(Verified) Example Microsoft integrations" }];
        using var vm = Create(); vm.Advanced = true; vm.LicenseAccepted = true; await vm.RefreshCommand.ExecutionTask!;
        Assert.Equal(3, vm.Rows.Count); vm.HideVerifiedMicrosoft = true;
        Assert.Equal(new[] { "unsigned", "third-party" }, vm.Rows.Select(row => row.Entry.Id));
    }

    [Fact]
    public async Task DisablingConfirmsTheExactEntryAndRefreshesTheActualState()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0]; await vm.DisableCommand.ExecuteAsync(null);
        _dialogs.Verify(source => source.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request => request.Items != null && request.Items.Contains(_entries[0].Command))), Times.Once);
        _service.Verify(source => source.SetEnabledAsync("run:1", false, CancellationToken.None), Times.Once);
        Assert.False(Assert.Single(vm.Rows).Entry.Enabled); Assert.Equal("Autoruns_Changed", vm.Status);
        vm.SelectedRow = vm.Rows[0]; Assert.True(vm.CanEnable);
    }

    [Fact]
    public async Task CancelledConfirmationDoesNotWriteOrRefresh()
    {
        _dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0]; await vm.DisableCommand.ExecuteAsync(null);
        _service.Verify(source => source.SetEnabledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _service.Verify(source => source.ReadAsync(AutorunView.Startup, false, CancellationToken.None), Times.Once);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)] [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ModeGuardsKeepReadsAvailableAndChangesDisabled(WinOptimizerMode mode)
    {
        _mode.SetupGet(source => source.CurrentMode).Returns(mode);
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0]; Assert.False(vm.CanDisable);
        await vm.DisableCommand.ExecuteAsync(null);
        _service.Verify(source => source.SetEnabledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AdvancedRowsAreInspectionOnlyEvenIfTheFakeEntryIsEditable()
    {
        using var vm = Create(); vm.Advanced = true; vm.LicenseAccepted = true; await vm.RefreshCommand.ExecutionTask!;
        vm.SelectedRow = vm.Rows[0]; Assert.False(vm.CanDisable); await vm.DisableCommand.ExecuteAsync(null);
        _service.Verify(source => source.SetEnabledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReadFailuresClearStaleControlsAndExposeTheError()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0];
        _service.Setup(source => source.ReadAsync(AutorunView.Startup, false, CancellationToken.None)).ThrowsAsync(new IOException("Source access denied"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows); Assert.Empty(vm.Groups); Assert.False(vm.CanDisable); Assert.True(vm.IsError); Assert.Equal("Source access denied", vm.Status);
    }

    [Fact]
    public async Task ShutdownWaitsForTheCompleteNativeWrite()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(source => source.SetEnabledAsync("run:1", false, CancellationToken.None)).Returns(pending.Task);
        using var vm = Create(); await vm.LoadAsync(); vm.SelectedRow = vm.Rows[0];
        var action = vm.DisableCommand.ExecuteAsync(null);
        Assert.False(vm.WaitForWriteAsync().IsCompleted); Assert.True(vm.IsBusy); Assert.False(vm.RefreshCommand.CanExecute(null));
        pending.SetResult(); await action; await vm.WaitForWriteAsync(); Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task WindowsSettingsUsesTheDedicatedServiceBoundary()
    {
        using var vm = Create(); await vm.OpenWindowsSettingsCommand.ExecuteAsync(null);
        _service.Verify(source => source.OpenStartupSettingsAsync(), Times.Once);
    }

    public void Dispose() { foreach (var source in _sources) source.Dispose(); }
}
