using Moq;
using WinOptimizer.Application.Configuration;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

/// <summary>Protects discovery order, state identity and publication boundaries shared by export formats.</summary>
public class FeatureConfigurationSnapshotTests
{
    [Fact]
    public async Task Populate_DiscoversRecognizedFeaturesOnceAndPreservesProjectionOrderAndIdentity()
    {
        var first = Setting("first");
        var missing = Setting("missing");
        var last = Setting("last");
        var registry = new Mock<ICompatibleSettingsRegistry>(MockBehavior.Strict);
        registry.Setup(source => source.GetAllFilteredSettings()).Returns(new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            [FeatureIds.Power] = [first, missing], ["unknown"] = [Setting("ignored")],
            [FeatureIds.Sound] = [], [FeatureIds.WindowsTheme] = [last]
        });
        var state = new SettingStateResult { Success = false, CurrentValue = 17 };
        var discovery = new Mock<ISystemSettingsDiscoveryService>(MockBehavior.Strict);
        var reads = new List<string[]>();
        discovery.Setup(source => source.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .Returns((IEnumerable<SettingDefinition> settings) =>
            {
                reads.Add(settings.Select(setting => setting.Id).ToArray());
                return Task.FromResult(new Dictionary<string, SettingStateResult> { [first.Id] = state });
            });
        var projected = new List<SettingDefinition>();
        var items = new Dictionary<string, ConfigurationItem>();
        var config = new UnifiedConfigurationFile();

        await FeatureConfigurationSnapshot.PopulateAsync(config, registry.Object, discovery.Object, Mock.Of<ILogService>(),
            (setting, observed) =>
            {
                Assert.Same(setting == first ? state : null, observed);
                projected.Add(setting);
                return items[setting.Id] = new ConfigurationItem { Id = setting.Id };
            });

        Assert.Equal(new[] { first, missing, last }, projected);
        Assert.Equal(new[] { "first", "missing" }, reads[0]);
        Assert.Equal(new[] { "last" }, reads[1]);
        Assert.Equal(2, reads.Count);
        Assert.True(config.Optimize.IsIncluded && config.Customize.IsIncluded);
        Assert.Equal(new[] { FeatureIds.Power }, config.Optimize.Features.Keys);
        Assert.Equal(new[] { FeatureIds.WindowsTheme }, config.Customize.Features.Keys);
        Assert.True(config.Optimize.Features[FeatureIds.Power].IsIncluded);
        Assert.Same(items[first.Id], config.Optimize.Features[FeatureIds.Power].Items[0]);
        Assert.Same(items[missing.Id], config.Optimize.Features[FeatureIds.Power].Items[1]);
        Assert.Same(items[last.Id], config.Customize.Features[FeatureIds.WindowsTheme].Items[0]);
        registry.Verify(source => source.GetAllFilteredSettings(), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Populate_FailurePropagatesWithoutPublishingPartialFeatureDictionaries(bool failProjection)
    {
        var first = Setting("first");
        var failed = Setting("failed");
        var registry = new Mock<ICompatibleSettingsRegistry>();
        registry.Setup(source => source.GetAllFilteredSettings()).Returns(new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            [FeatureIds.Power] = [first], [FeatureIds.WindowsTheme] = [failed], [FeatureIds.Sound] = [Setting("later")]
        });
        var failure = new InvalidOperationException("fixture failure");
        var reads = new List<string>();
        var discovery = new Mock<ISystemSettingsDiscoveryService>();
        discovery.Setup(source => source.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .Returns((IEnumerable<SettingDefinition> settings) =>
            {
                var id = settings.Single().Id;
                reads.Add(id);
                return id == failed.Id && !failProjection
                    ? Task.FromException<Dictionary<string, SettingStateResult>>(failure)
                    : Task.FromResult(new Dictionary<string, SettingStateResult>());
            });
        var config = new UnifiedConfigurationFile();
        var originalOptimize = config.Optimize.Features;
        var originalCustomize = config.Customize.Features;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => FeatureConfigurationSnapshot.PopulateAsync(
            config, registry.Object, discovery.Object, Mock.Of<ILogService>(),
            (setting, _) => setting.Id == failed.Id ? throw failure : new ConfigurationItem { Id = setting.Id }));

        Assert.Same(failure, actual);
        Assert.Equal(new[] { "first", "failed" }, reads);
        Assert.Same(originalOptimize, config.Optimize.Features);
        Assert.Same(originalCustomize, config.Customize.Features);
        Assert.True(config.Optimize.IsIncluded);
        Assert.False(config.Customize.IsIncluded);
    }

    private static SettingDefinition Setting(string id) => new() { Id = id, Name = id, Description = "" };
}
