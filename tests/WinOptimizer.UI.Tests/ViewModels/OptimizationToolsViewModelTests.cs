using System.Xml.Linq;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Verifies the real Optimize tool view models with fake Windows, dialog and progress boundaries.</summary>
public sealed class OptimizationToolsViewModelTests
{
    [Fact]
    public void FeatureCardsPlaceStatefulAccessibleTogglesInTheRightColumn()
    {
        using var stream = typeof(OptimizationToolsViewModelTests).Assembly.GetManifestResourceStream("FeaturesOptimizePage.xaml");
        var document = System.Xml.Linq.XDocument.Load(stream!);
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var toggle = Assert.Single(document.Descendants(ui + "ToggleSwitch"));
        Assert.Equal("1", (string?)toggle.Attribute("Grid.Column"));
        Assert.Equal("Right", (string?)toggle.Attribute("HorizontalAlignment"));
        Assert.Equal("{x:Bind IsOn, Mode=OneWay}", (string?)toggle.Attribute("IsOn"));
        Assert.Equal("{x:Bind IsAvailable, Mode=OneWay}", (string?)toggle.Attribute("IsEnabled"));
        Assert.Equal("{x:Bind Title, Mode=OneWay}", (string?)toggle.Attribute("AutomationProperties.Name"));
        Assert.DoesNotContain(document.Descendants(ui + "Button"), button => (string?)button.Attribute("Click") == "Apply_Click");
    }
    private readonly Mock<IOptimizationToolsService> _service = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<IWindowsVersionService> _windows = new();
    private readonly Mock<ILocalizationService> _localization = new();
    public OptimizationToolsViewModelTests()
    {
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _service.Setup(service => service.ReadFeatureAsync(It.IsAny<WindowsFeatureOptimization>())).ReturnsAsync(false);
        _service.Setup(service => service.ReadNetworkTuningsAsync()).ReturnsAsync(NetworkStates());
    }
    private NetworkOptimizationViewModel Network() => new(_service.Object, _dialogs.Object, _localization.Object,
        _mode.Object, new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());
    private FeatureOptimizationViewModel Features() => new(_service.Object, _dialogs.Object, _windows.Object,
        _localization.Object, _mode.Object, new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());

    private static NetworkTuningState[] NetworkStates(string autoTuning = "Normal", bool rss = true) => NetworkTuningCatalog.All
        .Select(item => new NetworkTuningState(item.Id, item.Id == "tcp-auto-tuning" ? autoTuning : item.Id == "offload-rss" ? rss ? "Enabled" : "Disabled"
            : item.Input == NetworkTuningInput.Toggle ? "Disabled" : item.Owner == NetworkTuningOwner.NameResolution ? null : item.Minimum.ToString(), true)).ToArray();

    [Fact]
    public async Task NetworkLoadsOnDemandAndDisplaysOnlyVerifiedEnhancementResults()
    {
        var original = new NetworkPerformanceState("Disabled", false);
        var updated = new NetworkPerformanceState("Normal", true);
        _service.Setup(service => service.ReadNetworkTuningsAsync()).ReturnsAsync(NetworkStates("Disabled", false));
        _service.Setup(service => service.EnhanceNetworkAsync())
            .Callback(() => _service.Setup(service => service.ReadNetworkTuningsAsync()).ReturnsAsync(NetworkStates())).ReturnsAsync(updated);
        using var vm = Network();
        Assert.False(vm.CanEnhance); _service.VerifyNoOtherCalls();
        await vm.RefreshAsync();
        Assert.Equal(original, vm.State); Assert.True(vm.CanEnhance);
        await vm.EnhanceCommand.ExecuteAsync(null);
        Assert.Equal(updated, vm.State); Assert.False(vm.IsError);
        _dialogs.Verify(dialogs => dialogs.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request =>
            request.Title == vm.EnhanceLabel && request.Message == vm.EnhanceDescription)), Times.Once);
    }

    [Fact]
    public async Task NetworkCancellationAndFailuresDoNotFabricateAnAppliedState()
    {
        var original = new NetworkPerformanceState("Restricted", false);
        _service.Setup(service => service.ReadNetworkTuningsAsync()).ReturnsAsync(NetworkStates("Restricted", false));
        using var vm = Network();
        await vm.RefreshAsync();
        var baseline = vm.State;
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        await vm.EnhanceCommand.ExecuteAsync(null);
        _service.Verify(service => service.EnhanceNetworkAsync(), Times.Never);
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _service.Setup(service => service.EnhanceNetworkAsync()).ThrowsAsync(new InvalidOperationException("Readback failed"));
        await vm.EnhanceCommand.ExecuteAsync(null);
        Assert.Equal(original, vm.State); Assert.Same(baseline, vm.State); Assert.True(vm.IsError); Assert.Equal("Readback failed", vm.Status);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ToolWritesAreDisabledInTestAndReviewModes(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        using var network = Network(); using var features = Features();
        await network.RefreshAsync();
        Assert.False(network.CanEnhance); Assert.True(network.ShowModeNotice);
        await features.RefreshAsync();
        await features.SetAsync(features.Choices[0], true);
        _service.Verify(service => service.SetFeatureAsync(It.IsAny<WindowsFeatureOptimization>(), It.IsAny<bool>()), Times.Never);
        _dialogs.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListsOnlyActionsSupportedByTheWindowsVersion(bool windows11)
    {
        _windows.Setup(windows => windows.IsWindows11()).Returns(windows11);
        using var vm = Features();
        Assert.Equal(windows11, vm.Choices.Any(choice => choice.Feature == WindowsFeatureOptimization.Copilot));
        Assert.Equal(!windows11, vm.Choices.Any(choice => choice.Feature == WindowsFeatureOptimization.ActivityHistory));
        Assert.Contains(vm.Choices, choice => choice.Feature == WindowsFeatureOptimization.OptionalServices);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FeatureConfirmationAndErrorsUseTheSharedTaskLifecycleAndShutdownDrain()
    {
        using var vm = Features();
        await vm.RefreshAsync();
        var choice = vm.Choices.First(item => item.Feature == WindowsFeatureOptimization.Telemetry);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(service => service.SetFeatureAsync(choice.Feature, true)).Returns(pending.Task);
        var operation = vm.SetAsync(choice, true);
        Assert.True(vm.IsBusy); Assert.False(vm.CanWrite); Assert.False(vm.WaitForWriteAsync().IsCompleted);
        _dialogs.Verify(dialogs => dialogs.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request => request.Message == choice.Description)), Times.Once);
        pending.SetException(new InvalidOperationException("Service access denied"));
        await operation;
        Assert.False(vm.IsBusy); Assert.True(vm.IsError); Assert.Equal("Service access denied", vm.Status);
        Assert.True(vm.WaitForWriteAsync().IsCompleted);
    }

    [Fact]
    public void NetworkCardsExposeAllThreeInputKindsInTheRightColumn()
    {
        using var stream = typeof(OptimizationToolsViewModelTests).Assembly.GetManifestResourceStream("NetworkOptimizePage.xaml");
        var document = XDocument.Load(stream!);
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (var type in new[] { "ToggleSwitch", "ComboBox", "NumberBox" })
        {
            var control = Assert.Single(document.Descendants(ui + type));
            Assert.Contains(control.Ancestors(), parent => (string?)parent.Attribute("Grid.Column") == "1" && (string?)parent.Attribute("HorizontalAlignment") == "Right");
            Assert.Equal("{x:Bind Title}", (string?)control.Attribute("AutomationProperties.Name"));
        }
    }

    [Fact]
    public async Task NetworkLoadsTwentyFiveStableChoicesWithoutApplyingAnything()
    {
        using var vm = Network();
        Assert.Equal(25, vm.Choices.Count); Assert.Equal(5, vm.Groups.Count);
        Assert.All(vm.Choices, choice => Assert.False(choice.IsAvailable));
        _service.VerifyNoOtherCalls();
        var choice = vm.Choices[0];
        await vm.RefreshAsync();
        Assert.Same(choice, vm.Choices[0]); Assert.All(vm.Choices, item => Assert.True(item.IsAvailable));
        _service.Verify(service => service.SetNetworkTuningAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    public async Task NetworkToggleUsesTheDisplayedBaselineAndOnlyPublishesVerifiedReadback(string value)
    {
        var states = NetworkStates();
        var index = Array.FindIndex(states, state => state.Id == "tcp-ecn");
        states[index] = states[index] with { Value = value == "Enabled" ? "Disabled" : "Enabled" };
        _service.Setup(service => service.ReadNetworkTuningsAsync()).ReturnsAsync(states);
        using var vm = Network();
        await vm.RefreshAsync();
        var choice = vm.Choices.Single(item => item.Definition.Id == "tcp-ecn");
        var baseline = choice.State.Value;
        _service.Setup(service => service.SetNetworkTuningAsync(choice.Definition.Id, value, baseline))
            .ReturnsAsync(() => states.Select(state => state.Id == choice.Definition.Id ? state with { Value = value } : state).ToArray());
        await vm.SetAsync(choice, value);
        Assert.Equal(value, choice.State.Value); Assert.False(vm.IsError);
        _service.Verify(service => service.SetNetworkTuningAsync("tcp-ecn", value, baseline), Times.Once);
    }

    [Fact]
    public async Task CancelledAndUnavailableNetworkEditsRetainTheNativeBaseline()
    {
        using var vm = Network();
        await vm.RefreshAsync();
        var choice = vm.Choices.Single(item => item.Definition.Id == "priority-local");
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        await vm.SetAsync(choice, "4");
        Assert.Null(choice.State.Value);
        choice.Update(choice.State with { IsAvailable = false });
        await vm.SetAsync(choice, "4");
        _service.Verify(service => service.SetNetworkTuningAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task FailedNetworkEditRequiresRefreshAndRetainsTheLastKnownValue()
    {
        using var vm = Network();
        await vm.RefreshAsync();
        var choice = vm.Choices.Single(item => item.Definition.Id == "priority-dns");
        _service.Setup(service => service.SetNetworkTuningAsync("priority-dns", "6", null)).ThrowsAsync(new InvalidOperationException("Native write failed"));
        await vm.SetAsync(choice, "6");
        Assert.Null(choice.State.Value); Assert.False(choice.IsAvailable); Assert.True(vm.IsError); Assert.Equal("Native write failed", vm.Status);
        await vm.RefreshAsync();
        Assert.True(choice.IsAvailable); Assert.False(vm.IsError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TogglesBothDirectionsAndUsesFreshReadback(bool isOn)
    {
        using var vm = Features();
        var choice = vm.Choices[0];
        _service.Setup(service => service.ReadFeatureAsync(choice.Feature)).ReturnsAsync(!isOn);
        Assert.False(choice.IsAvailable);
        await vm.RefreshAsync();
        Assert.Same(choice, vm.Choices[0]);
        Assert.Equal(!isOn, choice.IsOn);
        _service.Setup(service => service.SetFeatureAsync(choice.Feature, isOn))
            .Callback(() => _service.Setup(service => service.ReadFeatureAsync(choice.Feature)).ReturnsAsync(isOn))
            .Returns(Task.CompletedTask);
        await vm.SetAsync(choice, isOn);
        Assert.Equal(isOn, choice.IsOn); Assert.True(choice.IsAvailable); Assert.False(vm.IsError);
        _service.Verify(service => service.SetFeatureAsync(choice.Feature, isOn), Times.Once);
    }

    [Fact]
    public async Task CancelledUnavailableAndUnconfirmedTogglesCannotShowFabricatedSuccess()
    {
        using var vm = Features();
        var choice = vm.Choices[0];
        await vm.SetAsync(choice, true);
        _dialogs.VerifyNoOtherCalls();
        await vm.RefreshAsync();
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse());
        await vm.SetAsync(choice, true);
        Assert.False(choice.IsOn);
        _service.Verify(service => service.SetFeatureAsync(It.IsAny<WindowsFeatureOptimization>(), It.IsAny<bool>()), Times.Never);
        _dialogs.Setup(dialogs => dialogs.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        await vm.SetAsync(choice, true);
        Assert.False(choice.IsOn); Assert.True(vm.IsError); Assert.Equal("OptimizeFeatures_Unconfirmed", vm.Status);
        _service.Setup(service => service.ReadFeatureAsync(choice.Feature)).ReturnsAsync((bool?)null);
        await vm.RefreshAsync();
        Assert.False(choice.IsAvailable);
    }

    [Fact]
    public async Task FailedWritesReloadPartialStateAndFailedReadsDisableTheToggle()
    {
        using var vm = Features();
        await vm.RefreshAsync();
        var choice = vm.Choices[0];
        _service.Setup(service => service.SetFeatureAsync(choice.Feature, true))
            .Callback(() => _service.Setup(service => service.ReadFeatureAsync(choice.Feature)).ReturnsAsync(true))
            .ThrowsAsync(new InvalidOperationException("Partial change"));
        await vm.SetAsync(choice, true);
        Assert.True(choice.IsOn); Assert.True(vm.IsError); Assert.Equal("Partial change", vm.Status);
        _service.Setup(service => service.ReadFeatureAsync(choice.Feature)).ThrowsAsync(new InvalidOperationException("Read denied"));
        await vm.RefreshAsync();
        Assert.False(choice.IsAvailable); Assert.True(vm.IsError); Assert.Equal("Read denied", vm.Status);
    }

    [Fact]
    public async Task DisappearingTargetsCannotConfirmAnOffChange()
    {
        _service.Setup(service => service.ReadFeatureAsync(It.IsAny<WindowsFeatureOptimization>())).ReturnsAsync(true);
        using var vm = Features();
        await vm.RefreshAsync();
        var choice = vm.Choices[0];
        _service.Setup(service => service.SetFeatureAsync(choice.Feature, false))
            .Callback(() => _service.Setup(service => service.ReadFeatureAsync(choice.Feature)).ReturnsAsync((bool?)null))
            .Returns(Task.CompletedTask);
        await vm.SetAsync(choice, false);
        Assert.False(choice.IsAvailable); Assert.True(vm.IsError); Assert.Equal("OptimizeFeatures_Unconfirmed", vm.Status);
    }
}
