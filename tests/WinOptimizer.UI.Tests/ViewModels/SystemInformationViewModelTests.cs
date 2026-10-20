using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Interfaces;
using WinOptimizer.Core.Features.SystemInformation.Models;
using WinOptimizer.UI.Features.SystemInformation.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public sealed class SystemInformationViewModelTests : IDisposable
{
    private readonly Mock<ISystemInformationService> _service = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly SystemInformationViewModel _vm;

    private static SystemInformationItem Item(string name) => new(name,
        [new("SystemInfo_Field_Model", new SystemInformationText("Example model")),
         new("SystemInfo_Field_Capacity", new SystemInformationNumber(17179869184UL, SystemInformationUnit.Bytes))]);

    private static SystemInformationSnapshot Snapshot() => new(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero),
        [new(SystemInformationCategory.Machine, [Item("Example machine")], null),
         new(SystemInformationCategory.Windows, [Item("Example Windows")], null),
         new(SystemInformationCategory.Drivers, [Item("Example GPU driver"), Item("Example audio driver")], null),
         new(SystemInformationCategory.Battery, [], null)]);

    public SystemInformationViewModelTests()
    {
        _localization.Setup(x => x.GetString(It.IsAny<string>())).Returns((string key) => key switch
        {
            "SystemInfo_EntryCount" => "{0} entries",
            "SystemInfo_EntrySingular" => "{0} entry",
            "SystemInfo_CollectedAt" => "Snapshot: {0}",
            _ => key
        });
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        _vm = new(_service.Object, _localization.Object);
    }

    private SystemInformationCategoryViewModel Category(SystemInformationCategory id) =>
        Assert.Single(_vm.Categories, category => category.Section?.Category == id);

    [Fact]
    public void Construction_DoesNotQueryTheMachine()
    {
        _service.VerifyNoOtherCalls();
        Assert.Empty(_vm.Categories);
        Assert.Null(_vm.SelectedCategory);
    }

    [Fact]
    public async Task OpeningPage_LoadsOnce_AndExplicitRefreshRecollects()
    {
        await _vm.InitializeAsync();
        await _vm.InitializeAsync();
        _service.Verify(x => x.ReadAsync(It.IsAny<CancellationToken>()), Times.Once);
        await _vm.RefreshCommand.ExecuteAsync(null);
        _service.Verify(x => x.ReadAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task ReopeningPage_ReusesSessionSnapshotAfterDisposal_AndRefreshStillRecollects()
    {
        SystemInformationSnapshot? cached = null;
        _service.SetupGet(x => x.CachedSnapshot).Returns(() => cached);
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => cached = Snapshot());
        await _vm.InitializeAsync();
        var first = cached!;
        var collectedAt = _vm.CollectedAt;
        _vm.Dispose();
        using var reopened = new SystemInformationViewModel(_service.Object, _localization.Object);
        await reopened.InitializeAsync();
        Assert.Equal(collectedAt, reopened.CollectedAt);
        Assert.Same(first.Sections[0].Items[0], reopened.Overview[0].Items[0].Item);
        Assert.False(reopened.IsBusy);
        _service.Verify(x => x.ReadAsync(It.IsAny<CancellationToken>()), Times.Once);
        await reopened.RefreshCommand.ExecuteAsync(null);
        Assert.NotSame(first, cached);
        _service.Verify(x => x.ReadAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CachedPartialSnapshot_PreservesAvailabilityAndLocalizesForTheNewPage()
    {
        var cached = new SystemInformationSnapshot(Snapshot().CollectedAt,
            [new(SystemInformationCategory.Machine, [Item("Example machine")], null),
             new(SystemInformationCategory.Firmware, [], "access denied")]);
        _service.SetupGet(x => x.CachedSnapshot).Returns(cached);
        await _vm.InitializeAsync();
        Assert.Equal("SystemInfo_Partial", _vm.Status);
        Assert.Contains("access denied", _vm.Notices);
        Assert.Same(cached.Sections[0].Items[0], _vm.Overview[0].Items[0].Item);
        _localization.Raise(x => x.LanguageChanged += null, EventArgs.Empty);
        _service.Verify(x => x.ReadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Snapshot_OpensOverview_WithSameCategoriesDevicesAndTypedProperties()
    {
        var snapshot = Snapshot();
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        await _vm.InitializeAsync();
        Assert.Equal(5, _vm.Categories.Count);
        Assert.True(_vm.IsOverview);
        Assert.Same(_vm.Categories[0], _vm.SelectedCategory);
        Assert.Equal(4, _vm.Overview.Count);
        var drivers = Category(SystemInformationCategory.Drivers);
        Assert.Same(drivers, _vm.Overview[2]);
        Assert.Equal("Example GPU driver", drivers.OverviewValue);
        Assert.Equal("2 entries", drivers.CountText);
        var device = drivers.Items[0];
        Assert.Same(snapshot.Sections[2].Items[0], device.Item);
        Assert.Same(device.Item.Properties[0], device.Properties[0].Property);
        Assert.Equal("SystemInfo_Field_Model", device.Properties[0].Label);
        Assert.Equal("Example model", device.Properties[0].Value);
        Assert.Contains("16", device.Properties[1].Value);
        _vm.SelectedCategory = drivers;
        Assert.False(_vm.IsOverview);
        Assert.True(_vm.IsDetail);
        Assert.Same(drivers.Items, _vm.SelectedItems);
    }

    [Fact]
    public async Task Overview_ShowsRealKeySpecifications_AndExplicitEmptyState()
    {
        await _vm.InitializeAsync();
        var machine = Category(SystemInformationCategory.Machine);
        Assert.Equal("1 entry", machine.CountText);
        Assert.Contains("SystemInfo_Field_Model: Example model", machine.OverviewDetail);
        var battery = Category(SystemInformationCategory.Battery);
        Assert.Equal("0 entries", battery.CountText);
        Assert.Equal("SystemInfo_NoDevices", battery.OverviewValue);
        _vm.SelectedCategory = battery;
        Assert.True(_vm.HasEmptyCategory);
        Assert.Empty(_vm.SelectedItems);
        Assert.Equal("SystemInfo_NoDevices", _vm.EmptyMessage);
    }

    [Fact]
    public async Task SuccessfulRefresh_PreservesCategoryAndPublishesNewDevices()
    {
        await _vm.InitializeAsync();
        _vm.SelectedCategory = Category(SystemInformationCategory.Drivers);
        var previous = _vm.SelectedItems[0];
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(SystemInformationCategory.Drivers, _vm.SelectedCategory!.Section!.Category);
        Assert.NotSame(previous, _vm.SelectedItems[0]);
        Assert.Equal(2, _vm.SelectedItems.Count);
    }

    [Fact]
    public async Task FailedCategory_HasAnErrorAndZeroItems_WhileOtherInformationRemainsVisible()
    {
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SystemInformationSnapshot(DateTimeOffset.UtcNow,
            [new(SystemInformationCategory.Machine, [Item("Example machine")], null), new(SystemInformationCategory.Firmware, [], "access denied")]));
        await _vm.InitializeAsync();
        Assert.Equal("SystemInfo_Partial", _vm.Status);
        Assert.True(_vm.HasNotices);
        Assert.Contains("access denied", _vm.Notices);
        Assert.Single(Category(SystemInformationCategory.Machine).Items);
        _vm.SelectedCategory = Category(SystemInformationCategory.Firmware);
        Assert.Empty(_vm.SelectedItems);
        Assert.Equal("SystemInfo_Unavailable", _vm.EmptyMessage);
    }

    [Fact]
    public async Task FailedOrCancelledRefresh_PreservesTheDatedPreviousSnapshot()
    {
        await _vm.InitializeAsync();
        _vm.SelectedCategory = Category(SystemInformationCategory.Drivers);
        var previous = _vm.SelectedCategory;
        var collectedAt = _vm.CollectedAt;
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new SystemInformationException("WMI unavailable"));
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Same(previous, _vm.SelectedCategory);
        Assert.Equal("WMI unavailable", _vm.Status);
        Assert.Equal(collectedAt, _vm.CollectedAt);
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await _vm.RefreshCommand.ExecuteAsync(null);
        Assert.Same(previous, _vm.SelectedCategory);
        Assert.Equal("SystemInfo_Cancelled", _vm.Status);
        Assert.Equal(collectedAt, _vm.CollectedAt);
    }

    [Fact]
    public async Task AllQueriesDenied_IsReportedAsUnavailable_WithoutInventedInventoryRows()
    {
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SystemInformationSnapshot(DateTimeOffset.UtcNow,
            Enum.GetValues<SystemInformationCategory>().Select(category => new SystemInformationSection(category, [], "access denied")).ToArray()));
        await _vm.InitializeAsync();
        Assert.Equal("SystemInfo_UnavailableAll", _vm.Status);
        Assert.Equal(14, _vm.Categories.Count);
        Assert.Equal(13, _vm.Overview.Count);
        Assert.All(_vm.Overview, category =>
        {
            Assert.Empty(category.Items);
            Assert.Equal("0 entries", category.CountText);
            Assert.Equal("SystemInfo_Unavailable", category.EmptyMessage);
        });
    }

    [Fact]
    public async Task NavigationAway_CancelsAndRejectsLateResults()
    {
        var completion = new TaskCompletionSource<SystemInformationSnapshot>();
        CancellationToken token = default;
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) => { token = ct; return completion.Task; });
        var operation = _vm.InitializeAsync();
        _vm.Dispose();
        Assert.True(token.IsCancellationRequested);
        completion.SetResult(Snapshot());
        await operation;
        Assert.Empty(_vm.Categories);
        Assert.Empty(_vm.Overview);
    }

    [Fact]
    public async Task NavigationAway_ReleasesTheCollectedSnapshot()
    {
        await _vm.InitializeAsync();
        Assert.NotEmpty(_vm.CollectedAt);
        _vm.Dispose();
        Assert.Empty(_vm.CollectedAt);
        Assert.Empty(_vm.Categories);
        Assert.Empty(_vm.SelectedItems);
        Assert.Empty(_vm.Overview);
        Assert.Empty(_vm.Notices);
        Assert.False(_vm.HasSelection);
    }

    [Fact]
    public async Task Cancel_StopsCollectionAndDoesNotPublishALateSnapshot()
    {
        var completion = new TaskCompletionSource<SystemInformationSnapshot>();
        CancellationToken token = default;
        _service.Setup(x => x.ReadAsync(It.IsAny<CancellationToken>())).Returns((CancellationToken ct) => { token = ct; return completion.Task; });
        var operation = _vm.InitializeAsync();
        _vm.CancelCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal("SystemInfo_Cancelling", _vm.Status);
        completion.SetResult(Snapshot());
        await operation;
        Assert.Empty(_vm.Categories);
        Assert.Equal("SystemInfo_Cancelled", _vm.Status);
        Assert.True(_vm.IsIdle);
    }

    [Fact]
    public async Task LanguageChanges_RefreshStructuredLabelsAndPreserveSelectionWithoutCollectingAgain()
    {
        await _vm.InitializeAsync();
        var drivers = Category(SystemInformationCategory.Drivers);
        _vm.SelectedCategory = drivers;
        var row = drivers.Items[0].Properties[0];
        _localization.Setup(x => x.GetString(It.IsAny<string>())).Returns((string key) => "translated " + key);
        _localization.Raise(x => x.LanguageChanged += null, EventArgs.Empty);
        Assert.StartsWith("translated ", drivers.Title);
        Assert.Equal("translated SystemInfo_Field_Model", row.Label);
        Assert.Equal("Example model", row.Value);
        Assert.Same(drivers, _vm.SelectedCategory);
        _service.Verify(x => x.ReadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Formatting_PreservesUnknownZeroAndFalse_AndUses64BitByteValues()
    {
        string Format(SystemInformationValue value) => SystemInformationPropertyViewModel.FormatValue(value, _localization.Object);
        Assert.Equal("SystemInfo_Unknown", Format(new SystemInformationText(null)));
        Assert.Equal("SystemInfo_Unknown", Format(new SystemInformationNumber(null, SystemInformationUnit.Bytes)));
        Assert.Equal("SystemInfo_No", Format(new SystemInformationBoolean(false)));
        Assert.Equal("SystemInfo_Unknown", Format(new SystemInformationBoolean(null)));
        Assert.Equal("0 B", Format(new SystemInformationNumber(0, SystemInformationUnit.Bytes)));
        Assert.Contains("32", Format(new SystemInformationNumber(34359738368UL, SystemInformationUnit.Bytes)));
        Assert.EndsWith(" GiB", Format(new SystemInformationNumber(34359738368UL, SystemInformationUnit.Bytes)));
    }

    [Fact]
    public void SharedList_StillShowsCleanerAndDriverScanRowsInOneLine()
    {
        var cleaner = new WinOptimizer.UI.Features.Cleaner.ViewModels.CleanerResultItem(new(Guid.NewGuid(),
            WinOptimizer.Core.Features.Cleaner.Models.CleanerArea.Privacy, "Cleaner_Privacy", "Evidence", 0, false,
            new WinOptimizer.Core.Features.Cleaner.Models.CleanerDiskTarget("C:\\")), _localization.Object);
        Assert.Equal(1, cleaner.DetailMaxLines);
        Assert.Equal(1, new WinOptimizer.UI.Features.Drivers.ViewModels.DriverResultItem("Driver scan evidence", "", null,
            () => "Driver scan evidence", null, _localization.Object, Mock.Of<IDialogService>()).DetailMaxLines);
        using var group = new WinOptimizer.UI.Features.Common.Models.SelectableResultGroup("Example", [cleaner], "",
            FluentIcons.Common.Icon.Info, _localization.Object);
        Assert.True(group.ShowSelection);
        Assert.False(group.HasEmptyMessage);
    }

    [Fact]
    public void Categories_UseSpecificHardwareIconsFromTheSharedIconFamily()
    {
        var icons = Enum.GetValues<SystemInformationCategory>().Select(category =>
            new SystemInformationCategoryViewModel(new(category, [], null), _localization.Object).Icon).ToArray();
        Assert.Equal(13, icons.Distinct().Count());
        Assert.Contains(FluentIcons.Common.Icon.Ram, icons);
        Assert.Contains(FluentIcons.Common.Icon.HardDrive, icons);
        Assert.Contains(FluentIcons.Common.Icon.NetworkCheck, icons);
        Assert.Equal(FluentIcons.Common.Icon.Layer, new SystemInformationCategoryViewModel(null, _localization.Object).Icon);
    }

    public void Dispose() => _vm.Dispose();
}
