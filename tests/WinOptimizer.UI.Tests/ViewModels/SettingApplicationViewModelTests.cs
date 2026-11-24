using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class SettingApplicationViewModelTests
{
    private readonly Mock<ISettingApplicationService> _mockSettingApplicationService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();

    private SettingItemViewModel CreateSut(SettingDefinition definition) => new(new()
    {
        SettingDefinition = definition,
        SettingId = definition.Id,
        Name = definition.Name,
        Description = definition.Description,
        InputType = definition.InputType
    }, _mockSettingApplicationService.Object, _mockLogService.Object,
        Mock.Of<IDispatcherService>(), _mockDialogService.Object, Mock.Of<ILocalizationService>())
    {
        HasBattery = true
    };

    private static SettingDefinition CreateDefinition(InputType inputType) => new()
    {
        Id = "fixture",
        Name = "Fixture",
        Description = "Synthetic setting",
        InputType = inputType,
        PowerCfgSettings = inputType == InputType.Action ? [] : [new()
        {
            PowerModeSupport = PowerModeSupport.Separate,
            RecommendedValueAC = 50,
            RecommendedValueDC = 25,
            DefaultValueAC = 0,
            DefaultValueDC = 0
        }]
    };

    [Fact]
    public void RejectedPowerSelectionRebindsTheControlToItsCommittedValue()
    {
        var definition = CreateDefinition(InputType.Selection);
        using var sut = CreateSut(definition);
        _mockSettingApplicationService.Setup(x => x.ApplySettingAsync(It.IsAny<ApplySettingRequest>()))
            .ReturnsAsync(OperationResult.Failed("AC write rejected"));
        var combo = new Microsoft.UI.Xaml.Controls.ComboBox { ItemsSource = new[] { "Original", "Requested" } };
        combo.SelectedIndex = 1;
        sut.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(sut.AcValue))
                combo.SelectedIndex = sut.AcValue;
        };

        sut.OnACComboBoxDropDownClosed(combo, EventArgs.Empty);

        sut.AcValue.Should().Be(0);
        combo.SelectedIndex.Should().Be(0);
        sut.LastApplyFailed.Should().BeTrue();
        _mockSettingApplicationService.Verify(x => x.ApplySettingAsync(It.IsAny<ApplySettingRequest>()), Times.Once);
    }

    [Fact]
    public async Task FailedPowerEditDoesNotLeakIntoQueuedEditForTheOtherPowerSource()
    {
        var definition = CreateDefinition(InputType.NumericRange);
        using var sut = CreateSut(definition);
        sut.AcNumericValue = 10;
        sut.DcNumericValue = 20;
        var first = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ApplySettingRequest> requests = [];
        _mockSettingApplicationService.Setup(x => x.ApplySettingAsync(It.IsAny<ApplySettingRequest>()))
            .Returns((ApplySettingRequest request) =>
            {
                requests.Add(request);
                return requests.Count == 1 ? first.Task : Task.FromResult(OperationResult.Succeeded());
            });
        sut.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(sut.DcNumericValue) && sut.DcNumericValue == 25)
                second.TrySetResult();
        };

        sut.SetAcNumericToRecommendedCommand.Execute(null);
        sut.SetDcNumericToRecommendedCommand.Execute(null);
        sut.AcNumericValue.Should().Be(10);
        sut.DcNumericValue.Should().Be(20);
        first.SetResult(OperationResult.Failed("AC write rejected"));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));

        requests.Should().HaveCount(2);
        requests[1].Value.Should().BeEquivalentTo(new Dictionary<string, object?> { ["ACValue"] = 10, ["DCValue"] = 25 });
        sut.AcNumericValue.Should().Be(10);
        sut.DcNumericValue.Should().Be(25);
        sut.LastApplyFailed.Should().BeFalse();
    }

    [Fact]
    public async Task ActionFailureIsShownAndNeverLoggedAsSuccessful()
    {
        using var sut = CreateSut(CreateDefinition(InputType.Action));
        _mockSettingApplicationService.Setup(x => x.ApplySettingAsync(It.IsAny<ApplySettingRequest>()))
            .ReturnsAsync(OperationResult.Failed("Action rejected by Windows"));

        await sut.RunActionCommand.ExecuteAsync(null);

        sut.LastApplyFailed.Should().BeTrue();
        _mockDialogService.Verify(x => x.ShowErrorAsync("Action rejected by Windows", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockLogService.Verify(x => x.Log(LogLevel.Info, It.Is<string>(s => s.Contains("Successfully executed")), null), Times.Never);
    }

}
