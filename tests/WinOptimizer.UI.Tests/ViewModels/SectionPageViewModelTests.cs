using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.ViewModels;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>
/// Concrete ISectionInfo implementation for testing.
/// </summary>
public class TestSectionInfo : ISectionInfo
{
    public string Key { get; }
    public string IconGlyphKey { get; }
    public string DisplayName { get; }
    public string ModuleId { get; }

    public TestSectionInfo(string key, string iconGlyphKey, string displayName, string moduleId)
    {
        Key = key;
        IconGlyphKey = iconGlyphKey;
        DisplayName = displayName;
        ModuleId = moduleId;
    }
}

/// <summary>
/// Concrete subclass of SectionPageViewModel for testing.
/// </summary>
public class TestableSectionPageViewModel : SectionPageViewModel<TestSectionInfo>
{
    public static readonly IReadOnlyList<TestSectionInfo> TestSections = new List<TestSectionInfo>
    {
        new("SectionA", "IconA", "Section A", "ModuleA"),
        new("SectionB", "IconB", "Section B", "ModuleB"),
        new("SectionC", "IconC", "Section C", "ModuleC"),
    };

    protected override string PageTitleKey => "Test_Page_Title";
    protected override string PageDescriptionKey => "Test_Page_Description";
    protected override string LogPrefix => "TestPageViewModel";
    protected override IReadOnlyList<TestSectionInfo> SectionDefinitions => TestSections;

    public TestableSectionPageViewModel(
        ILogService logService,
        ILocalizationService localizationService,
        IEnumerable<ISettingsFeatureViewModel> featureViewModels)
        : base(logService, localizationService, featureViewModels)
    {
        InitializeSectionMappings();
    }
}

public class SectionPageViewModelTests : IDisposable
{
    private readonly Mock<ILogService> _mockLogService;
    private readonly Mock<ILocalizationService> _mockLocalizationService;
    private readonly List<Mock<ISettingsFeatureViewModel>> _mockFeatureVms;

    public SectionPageViewModelTests()
    {
        _mockLogService = new Mock<ILogService>();
        _mockLocalizationService = new Mock<ILocalizationService>();

        // Default localization: return the key itself
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockFeatureVms = new List<Mock<ISettingsFeatureViewModel>>();
        foreach (var section in TestableSectionPageViewModel.TestSections)
        {
            var mock = new Mock<ISettingsFeatureViewModel>();
            mock.Setup(vm => vm.ModuleId).Returns(section.ModuleId);
            mock.Setup(vm => vm.DisplayName).Returns(section.DisplayName);
            mock.Setup(vm => vm.SettingsCount).Returns(0);
            mock.Setup(vm => vm.Settings).Returns(new ObservableCollection<SettingItemViewModel>());
            mock.Setup(vm => vm.HasVisibleSettings).Returns(false);
            _mockFeatureVms.Add(mock);
        }
    }

    public void Dispose()
    {
        // Intentionally empty.
    }

    private TestableSectionPageViewModel CreateViewModel()
    {
        return new TestableSectionPageViewModel(
            _mockLogService.Object,
            _mockLocalizationService.Object,
            _mockFeatureVms.Select(m => m.Object));
    }

    private TestableSectionPageViewModel CreateViewModel(
        IEnumerable<ISettingsFeatureViewModel> featureViewModels)
    {
        return new TestableSectionPageViewModel(
            _mockLogService.Object,
            _mockLocalizationService.Object,
            featureViewModels);
    }

    [Fact]
    public async Task InitializeAsync_LoadsFeaturesAndClearsLoadingState()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.InitializeAsync();

        foreach (var mockVm in _mockFeatureVms)
        {
            mockVm.Verify(v => v.LoadSettingsAsync(), Times.Once);
        }
        vm.IsLoading.Should().BeFalse();
        vm.IsNotLoading.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_OnSecondCall_DoesNotReload()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        await vm.InitializeAsync();
        await vm.InitializeAsync();

        // Assert - LoadSettingsAsync should only be called once per VM
        foreach (var mockVm in _mockFeatureVms)
        {
            mockVm.Verify(v => v.LoadSettingsAsync(), Times.Once);
        }
    }

    [Fact]
    public async Task InitializeAsync_WhenFeatureVMThrows_ContinuesLoadingOthers()
    {
        // Arrange
        _mockFeatureVms[0]
            .Setup(v => v.LoadSettingsAsync())
            .ThrowsAsync(new InvalidOperationException("VM failed"));

        var vm = CreateViewModel();

        // Act
        await vm.InitializeAsync();

        // Assert - other VMs should still be loaded
        _mockFeatureVms[1].Verify(v => v.LoadSettingsAsync(), Times.Once);
        _mockFeatureVms[2].Verify(v => v.LoadSettingsAsync(), Times.Once);
        vm.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_SetsIsLoadingTrueDuringLoad()
    {
        // Arrange
        var vm = CreateViewModel();
        var wasLoadingDuringInit = false;

        _mockFeatureVms[0]
            .Setup(v => v.LoadSettingsAsync())
            .Returns(() =>
            {
                wasLoadingDuringInit = vm.IsLoading;
                return Task.CompletedTask;
            });

        // Act
        await vm.InitializeAsync();

        // Assert
        wasLoadingDuringInit.Should().BeTrue();
    }

    [Fact]
    public void OnNavigatedFrom_ClearsSearchText()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SearchText = "test search";

        // Act
        vm.OnNavigatedFrom();

        // Assert
        vm.SearchText.Should().BeEmpty();
    }

    [Fact]
    public void CurrentSectionKey_WhenChangedWithActiveSearch_ClearsSearchText()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.CurrentSectionKey = "SectionA";
        vm.SearchText = "some search";

        // Act
        vm.CurrentSectionKey = "SectionB";

        // Assert
        vm.SearchText.Should().BeEmpty();
    }

    [Fact]
    public void SearchText_WhenSetInDetailPage_AppliesFilterToCurrentSectionVM()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.CurrentSectionKey = "SectionA";

        // Act
        vm.SearchText = "test query";

        // Assert
        _mockFeatureVms[0].Verify(v => v.ApplySearchFilter("test query"), Times.AtLeastOnce);
    }

    [Fact]
    public void SearchText_WhenSetInOverview_AppliesFilterToAllFeatureVMs()
    {
        // Arrange
        var vm = CreateViewModel();
        // CurrentSectionKey defaults to "Overview", which has no mapped VM

        // Act
        vm.SearchText = "global search";

        // Assert
        foreach (var mockVm in _mockFeatureVms)
        {
            mockVm.Verify(v => v.ApplySearchFilter("global search"), Times.Once);
        }
    }

    [Fact]
    public void SearchText_WhenCleared_AppliesEmptyFilterToVMs()
    {
        // Arrange
        var vm = CreateViewModel();
        vm.SearchText = "something";

        // Act
        vm.SearchText = string.Empty;

        // Assert
        foreach (var mockVm in _mockFeatureVms)
        {
            mockVm.Verify(v => v.ApplySearchFilter(string.Empty), Times.AtLeastOnce);
        }
    }

    [Fact]
    public void HasNoSearchResults_WhenSearchTextSetAndNoVisibleSettings_ReturnsTrue()
    {
        // Arrange
        var vm = CreateViewModel();
        // All mock VMs have HasVisibleSettings = false by default

        // Act
        vm.SearchText = "xyz no matches";

        // Assert
        vm.HasNoSearchResults.Should().BeTrue();
    }

    [Fact]
    public void HasNoSearchResults_WhenSearchTextSetAndHasVisibleSettings_ReturnsFalse()
    {
        // Arrange
        _mockFeatureVms[1].Setup(v => v.HasVisibleSettings).Returns(true);
        var vm = CreateViewModel();

        // Act
        vm.SearchText = "something";

        // Assert
        vm.HasNoSearchResults.Should().BeFalse();
    }

    [Fact]
    public void HasNoSearchResults_InDetailPage_ChecksCurrentSectionOnly()
    {
        // Arrange
        _mockFeatureVms[0].Setup(v => v.HasVisibleSettings).Returns(false);
        _mockFeatureVms[1].Setup(v => v.HasVisibleSettings).Returns(true);
        var vm = CreateViewModel();
        vm.CurrentSectionKey = "SectionA";

        // Act
        vm.SearchText = "query";

        // Assert - SectionA has no visible settings, even though SectionB does
        vm.HasNoSearchResults.Should().BeTrue();
    }

    [Fact]
    public void LanguageChanged_RaisesPropertyChangedForLocalizedProperties()
    {
        // Arrange
        var vm = CreateViewModel();
        var raisedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName!);

        // Act - simulate language change
        _mockLocalizationService.Raise(l => l.LanguageChanged += null, EventArgs.Empty);

        // Assert
        raisedProperties.Should().Contain(nameof(vm.PageTitle));
        raisedProperties.Should().Contain(nameof(vm.PageDescription));
        raisedProperties.Should().Contain(nameof(vm.SearchPlaceholder));
    }

    [Fact]
    public void Dispose_UnsubscribesFromLanguageChanged()
    {
        // Arrange
        var vm = CreateViewModel();
        var raisedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName!);

        // Act
        vm.Dispose();
        raisedProperties.Clear();

        // Raise after dispose - should not fire property changed
        _mockLocalizationService.Raise(l => l.LanguageChanged += null, EventArgs.Empty);

        // Assert - no property changes should be raised after dispose
        raisedProperties.Should().NotContain(nameof(vm.PageTitle));
    }

    [Fact]
    public void CurrentSectionKey_WhenChangedWithNoSearchText_AppliesEmptyFilterToTargetSection()
    {
        // Arrange
        var vm = CreateViewModel();

        // Act
        vm.CurrentSectionKey = "SectionB";

        // Assert - should apply empty filter to the target section VM
        _mockFeatureVms[1].Verify(v => v.ApplySearchFilter(string.Empty), Times.AtLeastOnce);
    }
}
