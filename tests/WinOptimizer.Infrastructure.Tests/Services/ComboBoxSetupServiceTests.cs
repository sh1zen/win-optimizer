using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class ComboBoxSetupServiceTests
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IComboBoxResolver> _mockResolver = new();
    private readonly Mock<IPowerPlanComboBoxService> _mockPowerPlan = new();
    private readonly Mock<ISystemSettingsDiscoveryService> _mockDiscovery = new();
    private readonly ComboBoxSetupService _sut;

    public ComboBoxSetupServiceTests()
    {
        _sut = new ComboBoxSetupService(
            _mockLog.Object,
            _mockResolver.Object,
            _mockPowerPlan.Object,
            _mockDiscovery.Object);
    }

    private static SettingDefinition CreateSelectionSetting(
        string id,
        string[]? displayNames = null,
        Dictionary<int, Dictionary<string, object?>>? valueMappings = null)
    {
        ComboBoxMetadata? comboBox = null;
        if (displayNames != null || valueMappings != null)
        {
            var names = displayNames ?? Array.Empty<string>();
            var options = new List<WinOptimizer.Core.Features.Common.Models.ComboBoxOption>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                options.Add(new WinOptimizer.Core.Features.Common.Models.ComboBoxOption
                {
                    DisplayName = names[i],
                    ValueMappings = valueMappings != null && valueMappings.TryGetValue(i, out var vm) ? vm : null,
                });
            }
            comboBox = new ComboBoxMetadata { Options = options };
        }

        return new SettingDefinition
        {
            Id = id,
            Name = $"Setting {id}",
            Description = $"Description for {id}",
            InputType = InputType.Selection,
            ComboBox = comboBox,
        };
    }

    private static SettingDefinition CreateToggleSetting(string id) => new()
    {
        Id = id,
        Name = $"Setting {id}",
        Description = $"Description for {id}",
        InputType = InputType.Toggle,
    };

    // ── SetupComboBoxOptionsAsync ──

    [Fact]
    public async Task SetupComboBoxOptionsAsync_StandardSelection_ReturnsOptionsWithCorrectIndex()
    {
        // Arrange
        var displayNames = new[] { "Low", "Medium", "High" };
        var valueMappings = new Dictionary<int, Dictionary<string, object?>>
        {
            { 0, new Dictionary<string, object?> { { "Quality", 1 } } },
            { 1, new Dictionary<string, object?> { { "Quality", 2 } } },
            { 2, new Dictionary<string, object?> { { "Quality", 3 } } },
        };
        var setting = CreateSelectionSetting("quality-level", displayNames, valueMappings);

        // currentValue is an int index, so resolver/discovery are not called
        int currentValue = 1;

        // Act
        var result = await _sut.SetupComboBoxOptionsAsync(setting, currentValue);

        // Assert
        result.Success.Should().BeTrue();
        result.Options.Should().HaveCount(3);
        result.Options[0].DisplayText.Should().Be("Low");
        result.Options[1].DisplayText.Should().Be("Medium");
        result.Options[2].DisplayText.Should().Be("High");
        result.SelectedValue.Should().Be(1);
    }

    [Fact]
    public async Task SetupComboBoxOptionsAsync_NoOptions_ReturnsErrorResult()
    {
        // Arrange -- Selection type but no custom properties (empty dictionary)
        var setting = new SettingDefinition
        {
            Id = "empty-combo",
            Name = "Empty ComboBox",
            Description = "No options",
            InputType = InputType.Selection,
        };

        // Act
        var result = await _sut.SetupComboBoxOptionsAsync(setting, 0);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("empty-combo");
        result.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task SetupComboBoxOptionsAsync_NonSelectionInputType_ReturnsError()
    {
        // Arrange
        var setting = CreateToggleSetting("not-a-combo");

        // Act
        var result = await _sut.SetupComboBoxOptionsAsync(setting, null);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not a ComboBox");
    }

    [Fact]
    public async Task ResolveIndexFromRawValuesAsync_ResolverThrows_ReturnsZeroAndLogsWarning()
    {
        // Arrange
        var setting = CreateSelectionSetting("throw-test");
        var rawValues = new Dictionary<string, object?>();
        _mockResolver
            .Setup(r => r.ResolveRawValuesToIndex(setting, rawValues))
            .Throws(new InvalidOperationException("Bad data"));

        // Act
        var index = await _sut.ResolveIndexFromRawValuesAsync(setting, rawValues);

        // Assert
        index.Should().Be(0);
        _mockLog.Verify(
            l => l.Log(LogLevel.Warning,
                It.Is<string>(s => s.Contains("throw-test") && s.Contains("Bad data")),
                It.IsAny<Exception?>()),
            Times.Once);
    }
}
