using System.ComponentModel;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Models;
using WinOptimizer.UI.Features.Optimize.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class SettingsGroupTests
{
    private readonly Mock<ISettingApplicationService> _mockSettingApplicationService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IDispatcherService> _mockDispatcherService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IEventBus> _mockEventBus = new();

    public SettingsGroupTests()
    {
        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());

        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Returns<Func<Task>>(f => f());

        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);
    }

    private SettingItemViewModel CreateSettingItem(
        string settingId = "test-setting",
        string name = "Test Setting",
        string description = "Description",
        string groupName = "Group",
        bool isVisible = true)
    {
        var settingDef = new SettingDefinition
        {
            Id = settingId,
            Name = name,
            Description = description,
            InputType = InputType.Toggle,
        };

        var config = new SettingItemViewModelConfig
        {
            SettingDefinition = settingDef,
            SettingId = settingId,
            Name = name,
            Description = description,
            GroupName = groupName,
            InputType = InputType.Toggle,
            IsSelected = false,
            Icon = "Icon",
            IconPack = "Custom",
        };

        var item = new SettingItemViewModel(
            config,
            _mockSettingApplicationService.Object,
            _mockLogService.Object,
            _mockDispatcherService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockEventBus.Object);

        item.IsVisible = isVisible;
        return item;
    }

    [Fact]
    public void HasVisibleItems_WhenValueDoesNotChange_DoesNotRaisePropertyChanged()
    {
        // Arrange
        var item1 = CreateSettingItem("s1", "Item 1", isVisible: true);
        var item2 = CreateSettingItem("s2", "Item 2", isVisible: true);
        var group = new SettingsGroup("Group", new[] { item1, item2 });
        var raisedCount = 0;
        ((INotifyPropertyChanged)group).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsGroup.HasVisibleItems))
                raisedCount++;
        };

        // Act - making one invisible still keeps HasVisibleItems true
        item1.IsVisible = false;

        // Assert - HasVisibleItems is still true, so it should not have raised
        group.HasVisibleItems.Should().BeTrue();
        raisedCount.Should().Be(0);
    }

    [Fact]
    public void Add_NewItem_SubscribesToPropertyChanged()
    {
        // Arrange
        var group = new SettingsGroup("Group", Enumerable.Empty<SettingItemViewModel>());
        var newItem = CreateSettingItem("s1", "New Item", isVisible: false);
        group.Add(newItem);
        group.HasVisibleItems.Should().BeFalse();

        // Act - changing visibility should be tracked
        newItem.IsVisible = true;

        // Assert
        group.HasVisibleItems.Should().BeTrue();
    }

    [Fact]
    public void Remove_Item_UnsubscribesFromPropertyChanged()
    {
        // Arrange
        var item = CreateSettingItem("s1", "Item 1", isVisible: true);
        var group = new SettingsGroup("Group", new[] { item });

        // Remove the item
        group.Remove(item);

        // Act - changing visibility on removed item
        var raisedProperties = new List<string>();
        ((INotifyPropertyChanged)group).PropertyChanged += (_, e) =>
            raisedProperties.Add(e.PropertyName!);
        item.IsVisible = false;

        // Assert - group should not react to changes on removed items
        // HasVisibleItems is already false from removal, no change expected
        raisedProperties.Should().NotContain(nameof(SettingsGroup.HasVisibleItems));
    }

    [Fact]
    public void Clear_RemovesAllItemsAndUpdatesVisibility()
    {
        // Arrange
        var items = new[]
        {
            CreateSettingItem("s1", "Item 1", isVisible: true),
            CreateSettingItem("s2", "Item 2", isVisible: true),
        };
        var group = new SettingsGroup("Group", items);
        group.HasVisibleItems.Should().BeTrue();

        // Act
        group.Clear();

        // Assert
        group.Should().BeEmpty();
        group.HasVisibleItems.Should().BeFalse();
    }

    [Fact]
    public void HasVisibleItems_MultipleVisibilityToggles_TracksCorrectly()
    {
        // Arrange
        var item = CreateSettingItem("s1", "Item 1", isVisible: true);
        var group = new SettingsGroup("Group", new[] { item });

        // Act & Assert - toggle multiple times
        item.IsVisible = false;
        group.HasVisibleItems.Should().BeFalse();

        item.IsVisible = true;
        group.HasVisibleItems.Should().BeTrue();

        item.IsVisible = false;
        group.HasVisibleItems.Should().BeFalse();
    }
}
