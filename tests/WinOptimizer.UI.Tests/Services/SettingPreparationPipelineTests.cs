using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class SettingPreparationPipelineTests
{
    private readonly Mock<ICompatibleSettingsRegistry> _mockCompatibleSettingsRegistry = new();
    private readonly Mock<ISettingLocalizationService> _mockSettingLocalizationService = new();

    private SettingPreparationPipeline CreateService()
    {
        return new SettingPreparationPipeline(
            _mockCompatibleSettingsRegistry.Object,
            _mockSettingLocalizationService.Object);
    }

    [Fact]
    public void PrepareSettings_FiltersSettingsByModuleIdAndLocalizesEach()
    {
        var settingA = new SettingDefinition
        {
            Id = "setting-a",
            Name = "Setting A",
            Description = "Desc A"
        };
        var settingB = new SettingDefinition
        {
            Id = "setting-b",
            Name = "Setting B",
            Description = "Desc B"
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetFilteredSettings("Privacy"))
            .Returns(new[] { settingA, settingB });

        var localizedA = settingA with { Name = "Localized A" };
        var localizedB = settingB with { Name = "Localized B" };

        _mockSettingLocalizationService
            .Setup(l => l.LocalizeSetting(settingA))
            .Returns(localizedA);
        _mockSettingLocalizationService
            .Setup(l => l.LocalizeSetting(settingB))
            .Returns(localizedB);

        var service = CreateService();
        var result = service.PrepareSettings("Privacy");

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Localized A");
        result[1].Name.Should().Be("Localized B");
    }

    [Fact]
    public void PrepareSettings_WhenModuleHasNoSettings_ReturnsEmptyList()
    {
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetFilteredSettings("EmptyModule"))
            .Returns(Enumerable.Empty<SettingDefinition>());

        var service = CreateService();
        var result = service.PrepareSettings("EmptyModule");

        result.Should().BeEmpty();
    }

    [Fact]
    public void PrepareSettings_DifferentModuleIds_ReturnDifferentResults()
    {
        var privacySetting = new SettingDefinition
        {
            Id = "privacy-1",
            Name = "Privacy Setting",
            Description = "Desc"
        };
        var gamingSetting = new SettingDefinition
        {
            Id = "gaming-1",
            Name = "Gaming Setting",
            Description = "Desc"
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetFilteredSettings("Privacy"))
            .Returns(new[] { privacySetting });
        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetFilteredSettings("Gaming"))
            .Returns(new[] { gamingSetting });

        _mockSettingLocalizationService
            .Setup(l => l.LocalizeSetting(It.IsAny<SettingDefinition>()))
            .Returns((SettingDefinition s) => s);

        var service = CreateService();

        var privacyResult = service.PrepareSettings("Privacy");
        var gamingResult = service.PrepareSettings("Gaming");

        privacyResult.Should().ContainSingle().Which.Id.Should().Be("privacy-1");
        gamingResult.Should().ContainSingle().Which.Id.Should().Be("gaming-1");
    }

    [Fact]
    public void PrepareSettings_PreservesOrderFromRegistry()
    {
        var settings = Enumerable.Range(1, 5).Select(i => new SettingDefinition
        {
            Id = $"setting-{i}",
            Name = $"Setting {i}",
            Description = "Desc"
        }).ToArray();

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetFilteredSettings("Module"))
            .Returns(settings);

        _mockSettingLocalizationService
            .Setup(l => l.LocalizeSetting(It.IsAny<SettingDefinition>()))
            .Returns((SettingDefinition s) => s);

        var service = CreateService();
        var result = service.PrepareSettings("Module");

        result.Select(s => s.Id).Should().ContainInOrder(
            "setting-1", "setting-2", "setting-3", "setting-4", "setting-5");
    }
}
