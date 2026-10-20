using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Verifies password presentation, read-only modes and the credential page's lifetime.</summary>
public sealed class SavedWifiViewModelTests
{
    private readonly Mock<ISavedWifiService> _service = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationModeService> _mode = new();

    public SavedWifiViewModelTests() => _localization.Setup(value => value.GetString(It.IsAny<string>()))
        .Returns((string key) => key);

    private SavedWifiViewModel Create() => new(_service.Object, _localization.Object, _mode.Object,
        new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());

    [Fact]
    public async Task PageReadsOnEntryAndDisplaysPasswordsAndDistinctUnavailableStates()
    {
        _service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SavedWifiSnapshot(
            [new("Personal", "Adapter 1", "WPA2PSK", "AES", "fake-password", WifiPasswordState.Available),
             new("Open", "Adapter 2", "open", "none", null, WifiPasswordState.OpenNetwork),
             new("Enterprise", "Adapter 1", "WPA2", "AES", null, WifiPasswordState.NotStored),
             new("Protected", "Adapter 1", "WPA3SAE", "AES", null, WifiPasswordState.Unavailable)], ""));
        using var vm = Create();
        _service.VerifyNoOtherCalls();
        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal("fake-password", vm.Profiles[0].Password);
        Assert.Contains("Adapter 1", vm.Profiles[0].Details);
        Assert.Equal("Connection_WifiOpen", vm.Profiles[1].Password);
        Assert.Equal("Connection_WifiNotStored", vm.Profiles[2].Password);
        Assert.Equal("Connection_WifiUnavailable", vm.Profiles[3].Password);
        Assert.False(vm.ShowEmpty); Assert.False(vm.HasStatus);
        vm.Clear();
        Assert.Empty(vm.Profiles); Assert.False(vm.ShowEmpty);
    }

    [Fact]
    public async Task EmptyInventoryAndFailedDiscoveryHaveDifferentPresentation()
    {
        _service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SavedWifiSnapshot([], ""));
        using var vm = Create(); await vm.LoadAsync(CancellationToken.None);
        Assert.True(vm.ShowEmpty);
        _service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SavedWifiSnapshot([], "Adapter unavailable"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.ShowEmpty); Assert.True(vm.HasDiagnostic);
        _service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new UtilityOperationException("Wi-Fi service unavailable"));
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.ShowEmpty); Assert.True(vm.IsError); Assert.Contains("unavailable", vm.Status);
    }

    [Fact]
    public async Task LeavingPageRejectsLateResultsEvenWhenTheNativeReadCompletes()
    {
        var pending = new TaskCompletionSource<SavedWifiSnapshot>();
        _service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var session = new CancellationTokenSource();
        using var vm = Create(); var load = vm.LoadAsync(session.Token);
        Assert.True(vm.IsBusy); Assert.False(vm.RefreshCommand.CanExecute(null));
        session.Cancel(); vm.Clear();
        pending.SetResult(new([new("Example", "Wi-Fi", "WPA2PSK", "AES", "fake-password", WifiPasswordState.Available)], ""));
        await load;
        Assert.Empty(vm.Profiles); Assert.False(vm.HasStatus); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CancellingPageDiscoveryDoesNotShowAnError()
    {
        using var session = new CancellationTokenSource();
        session.Cancel();
        _service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException(session.Token));
        using var vm = Create(); await vm.LoadAsync(session.Token);
        Assert.Empty(vm.Profiles); Assert.False(vm.HasStatus); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task PasswordInventoryIsReadOnlyAndAvailableInAllApplicationModes()
    {
        _service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SavedWifiSnapshot([], ""));
        foreach (var mode in new[] { WinOptimizerMode.Normal, WinOptimizerMode.Builder, WinOptimizerMode.ConfigReview })
        {
            _mode.SetupGet(value => value.CurrentMode).Returns(mode);
            using var vm = Create(); await vm.LoadAsync(CancellationToken.None);
            Assert.True(vm.RefreshCommand.CanExecute(null));
        }
        _mode.Verify(value => value.BeginSystemOperation(), Times.Never);
    }
}
