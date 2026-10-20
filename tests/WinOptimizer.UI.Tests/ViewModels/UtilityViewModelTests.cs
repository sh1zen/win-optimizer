using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class UtilityViewModelTests
{
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<IDialogService> _dialogs = new();
    public UtilityViewModelTests()
    {
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        CancellationTokenSource? running = null;
        _tasks.Setup(service => service.StartTask(It.IsAny<string>(), true, true, false))
            .Returns(() => running = new CancellationTokenSource());
        _tasks.SetupGet(service => service.CurrentTaskCancellationSource).Returns(() => running);
        _tasks.Setup(service => service.CompleteTask()).Callback(() => { running?.Dispose(); running = null; });
    }

    [Fact]
    public void ExtensionHeadingRefreshesWithLanguage()
    {
        using var vm = new UtilitiesCatalogViewModel(_localization.Object, new ExtensionTestDispatcher());
        Assert.Equal("Nav_Functionalities", vm.Heading);
        Assert.Equal("Utilities_Description", vm.Description);
        var changed = false;
        vm.PropertyChanged += (_, _) => changed = true;
        _localization.Raise(service => service.LanguageChanged += null, EventArgs.Empty);
        Assert.True(changed);
    }

    [Fact]
    public async Task EnvironmentEditsRawValuesAndDoesNotWriteOnSelectionOrLoad()
    {
        var service = new Mock<IEnvironmentEditorService>();
        var original = new EnvironmentVariableEntry("PATH", "%USERPROFILE%\\bin", true);
        var current = original;
        service.Setup(s => s.ReadAsync(EnvironmentScope.User)).Returns(() => Task.FromResult<IReadOnlyList<EnvironmentVariableEntry>>([current]));
        service.Setup(s => s.SaveAsync(EnvironmentScope.User, original, It.IsAny<EnvironmentVariableEntry>()))
            .Callback<EnvironmentScope, EnvironmentVariableEntry?, EnvironmentVariableEntry>((_, _, value) => current = value)
            .ReturnsAsync(new EnvironmentEditResult(true));
        using var vm = new EnvironmentEditorViewModel(service.Object, _dialogs.Object, _localization.Object, _mode.Object, new ExtensionTestDispatcher(), _tasks.Object);
        Assert.Equal("Nav_Management", vm.SectionTitle);
        await vm.LoadAsync(); vm.SelectedVariable = vm.Variables[0];
        Assert.Equal(original.Value, vm.VariableValue); Assert.False(vm.CanChangeName);
        service.Verify(s => s.SaveAsync(It.IsAny<EnvironmentScope>(), It.IsAny<EnvironmentVariableEntry>(), It.IsAny<EnvironmentVariableEntry>()), Times.Never);
        vm.VariableValue += ";C:\\Tools";
        await vm.SaveCommand.ExecuteAsync(null);
        service.Verify(s => s.SaveAsync(EnvironmentScope.User, original,
            It.Is<EnvironmentVariableEntry>(entry => entry.Value == "%USERPROFILE%\\bin;C:\\Tools" && entry.Expandable)), Times.Once);
        Assert.False(vm.IsError);
        Assert.Equal(current.Value, vm.VariableValue);
    }

    [Fact]
    public async Task EnvironmentDeletionRequiresTheEditorsConfirmation()
    {
        var service = new Mock<IEnvironmentEditorService>();
        var original = new EnvironmentVariableEntry("TEST", "value", false);
        service.Setup(s => s.ReadAsync(EnvironmentScope.User)).ReturnsAsync([original]);
        _dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        using var vm = new EnvironmentEditorViewModel(service.Object, _dialogs.Object, _localization.Object, _mode.Object, new ExtensionTestDispatcher(), _tasks.Object);
        await vm.LoadAsync(); vm.SelectedVariable = original;
        await vm.DeleteCommand.ExecuteAsync(null);
        service.Verify(s => s.DeleteAsync(It.IsAny<EnvironmentScope>(), It.IsAny<EnvironmentVariableEntry>()), Times.Never);
    }

    [Fact]
    public async Task HostsRetainsDraftAcrossReentryAndUndoUsesTheLatestRevision()
    {
        _localization.Setup(service => service.GetString("Utilities_HostsSaved")).Returns("Hosts saved. Backup: {0}");
        var service = new Mock<IHostsEditorService>();
        var document = new HostsDocument("127.0.0.1 localhost\n", "revision-1", "C:\\test\\hosts");
        service.Setup(s => s.ReadAsync()).ReturnsAsync(document);
        service.Setup(s => s.SaveAsync(It.IsAny<HostsDocument>(), It.IsAny<string>()))
            .ReturnsAsync((HostsDocument original, string content) => new HostsSaveResult(original with { Content = content, Revision = "revision-2" }, "C:\\test\\backup.bak"));
        using var vm = new HostsEditorViewModel(service.Object, _dialogs.Object, _localization.Object, _mode.Object, new ExtensionTestDispatcher(), _tasks.Object);
        Assert.Equal("Nav_Management", vm.SectionTitle);
        await vm.LoadAsync(); vm.Content += "0.0.0.0 blocked.test\n";
        await vm.LoadAsync(); Assert.Contains("blocked.test", vm.Content);
        service.Verify(s => s.ReadAsync(), Times.Once);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("backup.bak", vm.Status);
        await vm.UndoCommand.ExecuteAsync(null);
        service.Verify(s => s.SaveAsync(It.Is<HostsDocument>(current => current.Revision == "revision-2"), document.Content), Times.Once);
        Assert.Equal(document.Content, vm.Content);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    public async Task BatteryDefaultsToEightyAndAppliesOnlyTheSelectedSupportedLimit(int percentage)
    {
        var service = new Mock<IBatteryChargeService>();
        var state = BatteryState([60, 80, 100], 100);
        service.Setup(s => s.ReadAsync()).ReturnsAsync(state);
        service.Setup(s => s.SetLimitAsync(percentage)).ReturnsAsync(state with { ActiveLimit = percentage });
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync();
        Assert.Equal("Nav_Management", vm.SectionTitle);
        Assert.Equal(80, vm.Limits[vm.SelectedIndex].Percentage);
        Assert.Equal("100%", vm.ActiveLimit);
        service.Verify(s => s.SetLimitAsync(It.IsAny<int>()), Times.Never);
        vm.SelectedIndex = vm.Limits.ToList().FindIndex(limit => limit.Percentage == percentage);
        Assert.True(vm.SelectedIndex >= 0); Assert.True(vm.ApplyCommand.CanExecute(null));
        await vm.ApplyCommand.ExecuteAsync(null);
        service.Verify(s => s.SetLimitAsync(percentage), Times.Once);
        Assert.Equal(percentage + "%", vm.ActiveLimit); Assert.False(vm.IsError);
    }

    [Fact]
    public async Task UnsupportedBatteryAndRejectedChangesNeverDisplaySuccess()
    {
        var service = new Mock<IBatteryChargeService>();
        service.Setup(s => s.ReadAsync()).ReturnsAsync(BatteryState([], null));
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync(); Assert.False(vm.CanSelectLimit); Assert.False(vm.ApplyCommand.CanExecute(null));
        service.Setup(s => s.ReadAsync()).ReturnsAsync(BatteryState([80, 100], 100));
        await vm.RefreshCommand.ExecuteAsync(null);
        service.Setup(s => s.SetLimitAsync(80)).ThrowsAsync(new UtilityOperationException("firmware did not confirm"));
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Equal("firmware did not confirm", vm.Status); Assert.Equal("100%", vm.ActiveLimit);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task BatteryWritesFollowApplicationModeChanges(WinOptimizerMode mode)
    {
        var service = new Mock<IBatteryChargeService>();
        service.Setup(s => s.ReadAsync()).ReturnsAsync(BatteryState([80, 100], 100));
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync(); Assert.True(vm.ApplyCommand.CanExecute(null));
        _mode.SetupGet(m => m.CurrentMode).Returns(mode);
        _mode.Raise(m => m.ModeChanged += null, EventArgs.Empty);
        Assert.False(vm.ApplyCommand.CanExecute(null)); Assert.True(vm.ShowModeNotice);
        await vm.ApplyCommand.ExecuteAsync(null);
        service.Verify(s => s.SetLimitAsync(It.IsAny<int>()), Times.Never);
    }

    private BatteryChargeViewModel BatteryVm(IBatteryChargeService service) =>
        new(service, _localization.Object, _mode.Object, new ExtensionTestDispatcher(), _tasks.Object);

    [Theory]
    [InlineData(BatteryControlIssue.InterfaceUnavailable, true, true)]
    [InlineData(BatteryControlIssue.NoProvider, true, true)]
    [InlineData(BatteryControlIssue.FirmwareUnsupported, true, false)]
    [InlineData(BatteryControlIssue.FirmwareLocked, false, false)]
    [InlineData(BatteryControlIssue.AccessDenied, false, false)]
    [InlineData(BatteryControlIssue.DetectionFailed, false, false)]
    public async Task BatteryHelperSuggestsInstallationOnlyForApplicableIssues(BatteryControlIssue issue, bool action, bool optionalDrivers)
    {
        var service = new Mock<IBatteryChargeService>();
        service.Setup(value => value.ReadAsync()).ReturnsAsync(BatteryState([], null) with
        {
            Issue = issue,
            Setup = new("OEM driver", new("https://example.test/install"), new("https://example.test/help"), BatterySupportSetupKind.DriverAndUtility)
        });
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync();
        Assert.True(vm.ShowSupportHelper);
        Assert.Equal(action, vm.ShowSetupAction);
        Assert.Equal(optionalDrivers, vm.ShowOptionalDrivers);
        Assert.Equal(action, vm.CanOpenSetup);
        Assert.Equal("Utilities_BatteryIssue_" + issue, vm.SupportHelperMessage);
        Assert.Equal(issue == BatteryControlIssue.FirmwareUnsupported ? "Utilities_BatteryCheckModel" : "Utilities_BatteryInstallSupport", vm.SetupActionLabel);
    }

    [Fact]
    public async Task BatteryRecheckHidesTheHelperAfterSupportBecomesAvailable()
    {
        var service = new Mock<IBatteryChargeService>();
        service.SetupSequence(value => value.ReadAsync())
            .ReturnsAsync(BatteryState([], null) with { Issue = BatteryControlIssue.InterfaceUnavailable })
            .ReturnsAsync(BatteryState([80, 100], 100));
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync();
        Assert.True(vm.ShowSupportHelper);
        Assert.False(vm.ShowSetupAction);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.ShowSupportHelper);
        Assert.True(vm.CanSelectLimit);
        service.Verify(value => value.SetLimitAsync(It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task BatteryInstallerLinksFollowTheModeGuard(WinOptimizerMode mode)
    {
        var service = new Mock<IBatteryChargeService>();
        service.Setup(value => value.ReadAsync()).ReturnsAsync(BatteryState([], null) with
        {
            Issue = BatteryControlIssue.NoProvider,
            Setup = new("OEM utility", new("https://example.test/install"), new("https://example.test/help"), BatterySupportSetupKind.OemUtility)
        });
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync();
        Assert.True(vm.CanOpenSetup);
        _mode.SetupGet(value => value.CurrentMode).Returns(mode);
        _mode.Raise(value => value.ModeChanged += null, EventArgs.Empty);
        Assert.False(vm.CanOpenSetup);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoBatteryHasNoInstallationHelper()
    {
        var service = new Mock<IBatteryChargeService>();
        service.Setup(value => value.ReadAsync()).ReturnsAsync(BatteryState([], null) with { HasBattery = false });
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync();
        Assert.False(vm.ShowSupportHelper);
        Assert.False(vm.CanOpenSetup);
    }

    [Fact]
    public async Task ShutdownWaitsForAnInFlightFirmwareWrite()
    {
        var service = new Mock<IBatteryChargeService>();
        var state = BatteryState([80, 100], 100);
        service.Setup(s => s.ReadAsync()).ReturnsAsync(state);
        var pending = new TaskCompletionSource<BatteryChargeState>();
        service.Setup(s => s.SetLimitAsync(80)).Returns(pending.Task);
        using var vm = BatteryVm(service.Object);
        await vm.LoadAsync();
        var saving = vm.ApplyCommand.ExecuteAsync(null);
        var shutdown = vm.WaitForWriteAsync();
        Assert.False(shutdown.IsCompleted);
        pending.SetResult(state with { ActiveLimit = 80 });
        await Task.WhenAll(saving, shutdown);
        Assert.True(vm.WaitForWriteAsync().IsCompleted); Assert.False(vm.IsBusy);
    }
    private static BatteryChargeState BatteryState(int[] limits, int? active) =>
        new("Test", "Laptop", true, 72, "Fake firmware", limits, active, "", new("https://example.test/help"));
}
