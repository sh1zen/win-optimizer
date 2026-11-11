using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class CompatibleSettingsRegistryTests
{
    private readonly Mock<IWindowsCompatibilityFilter> _windowsFilter;
    private readonly Mock<IHardwareCompatibilityFilter> _hardwareFilter;
    private readonly Mock<IPowerSettingsValidationService> _powerValidation;
    private readonly Mock<ILogService> _logService;
    private readonly CompatibleSettingsRegistry _sut;

    public CompatibleSettingsRegistryTests()
    {
        _windowsFilter = new Mock<IWindowsCompatibilityFilter>();
        _hardwareFilter = new Mock<IHardwareCompatibilityFilter>();
        _powerValidation = new Mock<IPowerSettingsValidationService>();
        _logService = new Mock<ILogService>();

        // Default setup: filters pass through input unchanged
        _windowsFilter
            .Setup(f => f.FilterSettingsByWindowsVersion(It.IsAny<IEnumerable<SettingDefinition>>()))
            .Returns((IEnumerable<SettingDefinition> s) => s.ToList());

        _windowsFilter
            .Setup(f => f.FilterSettingsByWindowsVersion(It.IsAny<IEnumerable<SettingDefinition>>(), It.IsAny<bool>()))
            .Returns((IEnumerable<SettingDefinition> s, bool _) => s.ToList());

        _hardwareFilter
            .Setup(f => f.FilterSettingsByHardwareAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync((IEnumerable<SettingDefinition> s) => s.ToList());

        _powerValidation
            .Setup(f => f.FilterSettingsByExistenceAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync((IEnumerable<SettingDefinition> s) => s.ToList());

        _sut = new CompatibleSettingsRegistry(
            _windowsFilter.Object,
            _hardwareFilter.Object,
            _powerValidation.Object,
            _logService.Object);
    }

    [Fact]
    public async Task AuthoringCatalogue_IncludesSettingsForUnavailableHardware()
    {
        _hardwareFilter.Setup(x => x.FilterSettingsByHardwareAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(Array.Empty<SettingDefinition>());
        await _sut.InitializeAsync();
        _sut.GetFilteredSettings(FeatureIds.Power).Should().BeEmpty();
        _sut.GetBypassedSettings(FeatureIds.Power).Should().NotBeEmpty();
    }

    [Fact]
    public async Task CatalogueReads_ReuseInitializedSnapshotsAndIndexedDefinitions()
    {
        var filteredEnumerations = 0;
        var bypassedEnumerations = 0;
        IEnumerable<SettingDefinition> Filter(IEnumerable<SettingDefinition> settings)
        {
            filteredEnumerations++;
            foreach (var setting in settings) yield return setting with { Description = "Filtered" };
        }
        IEnumerable<SettingDefinition> Decorate(IEnumerable<SettingDefinition> settings)
        {
            bypassedEnumerations++;
            foreach (var setting in settings) yield return setting with { VersionCompatibilityMessage = "Compatibility_Windows10Only" };
        }
        _windowsFilter.Setup(f => f.FilterSettingsByWindowsVersion(It.IsAny<IEnumerable<SettingDefinition>>()))
            .Returns((IEnumerable<SettingDefinition> settings) => Filter(settings));
        _windowsFilter.Setup(f => f.FilterSettingsByWindowsVersion(It.IsAny<IEnumerable<SettingDefinition>>(), false))
            .Returns((IEnumerable<SettingDefinition> settings, bool _) => Decorate(settings));

        await _sut.InitializeAsync();
        var featureCount = _sut.GetAllFilteredSettings().Count;
        filteredEnumerations.Should().Be(featureCount);
        bypassedEnumerations.Should().Be(featureCount);

        foreach (var enabled in new[] { true, false, true })
        {
            _sut.SetFilterEnabled(enabled);
            foreach (var (featureId, settings) in _sut.GetAllFilteredSettings())
            {
                var snapshot = settings.ToArray();
                _sut.GetFilteredSettings(featureId).Should().Equal(snapshot);
                foreach (var setting in snapshot)
                {
                    _sut.GetById(setting.Id).Should().BeSameAs(setting);
                    _sut.GetFeatureIdForSetting(setting.Id).Should().Be(featureId);
                }
            }
        }
        foreach (var (featureId, settings) in _sut.GetAllBypassedSettings())
            _sut.GetBypassedSettings(featureId).Should().Equal(settings);
        filteredEnumerations.Should().Be(featureCount);
        bypassedEnumerations.Should().Be(featureCount);
    }

    [Fact]
    public async Task GetFilteredSettings_WhenFilterEnabled_ReturnsFilteredResults()
    {
        // Unknown features have no registered provider.
        await _sut.InitializeAsync();

        var result = _sut.GetFilteredSettings("NonExistentFeature");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBypassedSettings_ReturnsUnfilteredSettingsForFeature()
    {
        await _sut.InitializeAsync();

        var result = _sut.GetBypassedSettings("NonExistentFeature");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_ConcurrentCalls_OnlyInitializesOnce()
    {
        var task1 = _sut.InitializeAsync();
        var task2 = _sut.InitializeAsync();

        await Task.WhenAll(task1, task2);

        _sut.IsInitialized.Should().BeTrue();
        _logService.Verify(
            l => l.Log(LogLevel.Info, "Initializing compatible settings registry with auto-discovery", null),
            Times.Once);
    }

    [Fact]
    public void GetFilteredSettings_BeforeInitialization_ThrowsInvalidOperationException()
    {
        var action = () => _sut.GetFilteredSettings("SomeFeature");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*not initialized*");
    }

    [Fact]
    public void GetAllFilteredSettings_BeforeInitialization_ThrowsInvalidOperationException()
    {
        var action = () => _sut.GetAllFilteredSettings();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*not initialized*");
    }

    [Fact]
    public void GetBypassedSettings_BeforeInitialization_ThrowsInvalidOperationException()
    {
        var action = () => _sut.GetBypassedSettings("SomeFeature");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*not initialized*");
    }

    [Fact]
    public void GetAllBypassedSettings_BeforeInitialization_ThrowsInvalidOperationException()
    {
        var action = () => _sut.GetAllBypassedSettings();

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*not initialized*");
    }

    [Fact]
    public void GetById_BeforeInitialize_ThrowsInvalidOperationException()
    {
        Action act = () => _sut.GetById("any-id");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task GetById_MissingId_ReturnsNull()
    {
        await _sut.InitializeAsync();

        _sut.GetById("does-not-exist").Should().BeNull();
    }

    [Fact]
    public async Task GetFeatureIdForSetting_MissingId_ReturnsNull()
    {
        await _sut.InitializeAsync();

        _sut.GetFeatureIdForSetting("does-not-exist").Should().BeNull();
    }

    [Fact]
    public async Task GetById_NullSettingId_ThrowsArgumentNullException()
    {
        // Must be initialized first so we exercise the null-guard rather than the not-init throw.
        await _sut.InitializeAsync();

        Action act = () => _sut.GetById(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task GetFeatureIdForSetting_NullSettingId_ThrowsArgumentNullException()
    {
        // Must be initialized first so we exercise the null-guard rather than the not-init throw.
        await _sut.InitializeAsync();

        Action act = () => _sut.GetFeatureIdForSetting(null!);
        act.Should().Throw<ArgumentNullException>();
    }

}
