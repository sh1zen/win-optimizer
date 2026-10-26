using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class WindowsAppsViewModelTests
{
    private readonly Mock<IWindowsAppsService> _windowsAppsService = new();
    private readonly Mock<IAppUninstallWorkflow> _uninstallWorkflow = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<ILocalizationService> _localizationService = new();
    private readonly Mock<IDispatcherService> _dispatcherService = new();
    private readonly Mock<IThemeService> _themeService = new();
    private readonly Mock<IAppIconResolver> _iconResolver = new();

    public WindowsAppsViewModelTests()
    {
        _dispatcherService.Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _dispatcherService.Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Returns<Func<Task>>(f => f());
        _dispatcherService.Setup(d => d.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Returns<Func<Task>>(f => f());
        _iconResolver.Setup(r => r.ResolveBatchAsync(
                It.IsAny<IEnumerable<ItemDefinition>>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _localizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);
    }

    private WindowsAppsViewModel CreateSut() => new(
        _windowsAppsService.Object,
        _uninstallWorkflow.Object,
        _logService.Object,
        _dialogService.Object,
        _localizationService.Object,
        _dispatcherService.Object,
        _themeService.Object,
        _iconResolver.Object);

    private ItemDefinition CreateTestItem(string id, string name = "Test App",
        string[]? appxPackageName = null, bool isInstalled = false) => new()
    {
        Id = id,
        Name = name,
        Description = $"Description for {name}",
        AppxPackageName = appxPackageName ?? new[] { "Microsoft.Test" },
        IsInstalled = isInstalled
    };

    [Fact]
    public async Task RemovalUpdatesCachedRowsBeforeReviewWithoutDiscovery()
    {
        var app = CreateTestItem("chosen", isInstalled: true);
        var installed = true;
        _windowsAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync([app]);
        _windowsAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(() => new Dictionary<string, bool> { [app.Id] = installed });
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.Items[0].IsSelected = true;
        _uninstallWorkflow.Setup(workflow => workflow.UninstallAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), AppUninstallKind.Windows, true,
            It.IsAny<CancellationToken>(), It.IsAny<Func<AppUninstallResult, Task>>()))
            .Returns(async (IReadOnlyList<ItemDefinition> apps, AppUninstallKind kind, bool scripts, CancellationToken ct, Func<AppUninstallResult, Task>? refresh) =>
            {
                installed = false;
                var result = new AppUninstallResult([new(app.Id, app.Name, UninstallVerificationState.Removed, "Verified")], []);
                await refresh!(result);
                Assert.False(sut.Items[0].IsInstalled);
                Assert.Empty(sut.ItemsView);
                Assert.True(sut.IsTaskRunning);
                return result;
            });

        await sut.RemoveApps(skipConfirmation: true);

        Assert.Empty(sut.ItemsView);
        Assert.False(sut.IsTaskRunning);
        _windowsAppsService.Verify(service => service.InvalidateStatusCache(), Times.Once);
        _windowsAppsService.Verify(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()), Times.Once);
        _windowsAppsService.Verify(service => service.GetDevicePackageNamesAsync(), Times.Never);
        _iconResolver.Verify(service => service.ResolveBatchAsync(It.IsAny<IEnumerable<ItemDefinition>>(), true, It.IsAny<CancellationToken>()), Times.Once);

        await sut.RefreshInstallationStatusAsync();
        Assert.False(sut.Items[0].IsInstalled);
        _windowsAppsService.Verify(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(UninstallVerificationState.Removed)]
    [InlineData(UninstallVerificationState.StillInstalled)]
    [InlineData(UninstallVerificationState.Unknown)]
    [InlineData(UninstallVerificationState.Deferred)]
    public async Task CachedRemovalPreservesUnverifiedAndDeviceRetainedState(UninstallVerificationState state)
    {
        var chosen = CreateTestItem("chosen", isInstalled: true);
        var other = CreateTestItem("other", isInstalled: true);
        _windowsAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync([chosen, other]);
        _windowsAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { [chosen.Id] = true, [other.Id] = true });
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        var original = sut.Items[0];
        original.IsSelected = true;
        original.IsRetainedOnDevice = true;
        var metadata = new AppMetadata(null, 123, null);
        chosen.Metadata = metadata;
        other.Metadata = metadata;
        _uninstallWorkflow.Setup(workflow => workflow.UninstallAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), AppUninstallKind.Windows, true,
            It.IsAny<CancellationToken>(), It.IsAny<Func<AppUninstallResult, Task>>()))
            .Returns(async (IReadOnlyList<ItemDefinition> apps, AppUninstallKind kind, bool scripts, CancellationToken ct, Func<AppUninstallResult, Task>? refresh) =>
            {
                var result = new AppUninstallResult([new(chosen.Id, chosen.Name, state, "Verified")], []);
                await refresh!(result);
                return result;
            });

        await sut.RemoveApps(skipConfirmation: true);

        Assert.Same(original, sut.Items[0]);
        Assert.Equal(state != UninstallVerificationState.Removed, original.IsInstalled);
        Assert.True(original.IsRetainedOnDevice);
        Assert.Same(state == UninstallVerificationState.Removed ? null : metadata, chosen.Metadata);
        Assert.True(sut.Items[1].IsInstalled);
        Assert.Same(metadata, other.Metadata);
        Assert.All(sut.Items, item => Assert.False(item.IsSelected));
        _windowsAppsService.Verify(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()), Times.Once);
        _windowsAppsService.Verify(service => service.GetDevicePackageNamesAsync(), Times.Never);
    }

    [Fact]
    public async Task StatusBatchUpdatesCardsOnceAndClearsUnavailableSelections()
    {
        var apps = Enumerable.Range(0, 100).Select(index => CreateTestItem($"app{index}")).ToArray();
        var installed = true;
        _windowsAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync(apps);
        _windowsAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(() => apps.ToDictionary(app => app.Id, _ => installed));
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        foreach (var item in sut.Items) item.IsSelected = true;
        var changes = 0;
        sut.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(sut.WindowsAppsFiltered)) changes++; };
        installed = false;

        await sut.CheckInstallationStatusAsync();

        Assert.Equal(1, changes);
        Assert.All(sut.Items, item => { Assert.False(item.IsInstalled); Assert.False(item.IsSelected); });
        Assert.Empty(sut.ItemsView);
    }

    [Fact]
    public void InactiveStatusTracksInstallationAndDeviceRetentionChanges()
    {
        using var item = new AppItemViewModel(CreateTestItem("status"),
            _localizationService.Object, _dispatcherService.Object, _themeService.Object);
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        item.IsInactive.Should().BeFalse();

        item.IsRetainedOnDevice = true;
        item.IsInactive.Should().BeTrue();
        changes.Should().Contain(nameof(AppItemViewModel.IsInactive));

        changes.Clear();
        item.IsInstalled = true;
        item.IsInactive.Should().BeFalse();
        changes.Should().Contain(nameof(AppItemViewModel.IsInactive));

        changes.Clear();
        item.IsInstalled = false;
        item.IsInactive.Should().BeTrue();
        changes.Should().Contain(nameof(AppItemViewModel.IsInactive));

        changes.Clear();
        item.IsRetainedOnDevice = false;
        item.IsInactive.Should().BeFalse();
        changes.Should().Contain(nameof(AppItemViewModel.IsInactive));
    }

    [Fact]
    public async Task HiddenInstalledApps_ToggleIncludesOnlyDeviceMatchesAndPreservesAccountStatus()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync()).ReturnsAsync(new[]
        {
            CreateTestItem("active", "Matching Active", ["Test.Active"]),
            CreateTestItem("hidden", "Matching Hidden", ["Test.Hidden"]),
            CreateTestItem("absent", "Matching Absent", ["Test.Absent"]),
            CreateTestItem("other", "Other App", ["Test.Other"])
        });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["active"] = true, ["hidden"] = false, ["absent"] = false, ["other"] = true });
        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test.hidden", "Test.Unknown" };
        _windowsAppsService.Setup(s => s.GetDevicePackageNamesAsync()).ReturnsAsync(() => deviceNames);
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.ShowHiddenInstalledApps.Should().BeFalse();
        _windowsAppsService.Verify(s => s.GetDevicePackageNamesAsync(), Times.Never);
        sut.SearchText = "Matching";
        sut.ItemsView.Cast<AppItemViewModel>().Select(item => item.Id).Should().Equal("active");

        sut.ShowHiddenInstalledApps = true;
        await sut.CheckInstallationStatusAsync();
        sut.WindowsAppsFiltered.Select(item => item.Id).Should().BeEquivalentTo("active", "hidden");
        sut.ItemsView.Cast<AppItemViewModel>().Select(item => item.Id).Should().BeEquivalentTo("active", "hidden");
        var hidden = sut.Items.Single(item => item.Id == "hidden");
        hidden.IsInstalled.Should().BeFalse();
        hidden.Definition.IsInstalled.Should().BeFalse();
        hidden.IsInstalledOnDevice.Should().BeTrue();
        hidden.InstalledStatusText.Should().Be("WindowsApps_Status_Inactive");
        hidden.InstalledStatusTooltip.Should().Be("WindowsApps_Status_Inactive_Tooltip");
        sut.ToggleSelectAllCommand.Execute(null);
        sut.Items.Where(item => item.IsSelected).Select(item => item.Id).Should().BeEquivalentTo("active", "hidden");

        sut.ShowHiddenInstalledApps = false;
        hidden.IsSelected.Should().BeFalse();
        sut.WindowsAppsFiltered.Select(item => item.Id).Should().Equal("active");
        sut.ItemsView.Cast<AppItemViewModel>().Select(item => item.Id).Should().Equal("active");
        sut.ShowHiddenInstalledApps = true;
        await sut.CheckInstallationStatusAsync();
        hidden.IsSelected = true;
        deviceNames.Clear();
        await sut.RefreshInstallationStatusAsync();
        hidden.IsRetainedOnDevice.Should().BeFalse();
        hidden.IsSelected.Should().BeFalse();
        hidden.IsInstalledOnDevice.Should().BeFalse();
        sut.WindowsAppsFiltered.Select(item => item.Id).Should().Equal("active");
    }

    [Fact]
    public async Task HiddenInstalledApps_WhenInventoryFails_ShowsErrorAndPreservesCurrentAccountResults()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync()).ReturnsAsync(new[] { CreateTestItem("active") });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["active"] = true });
        _windowsAppsService.Setup(s => s.GetDevicePackageNamesAsync()).ThrowsAsync(new IOException("Device inventory unavailable"));
        using var sut = CreateSut();
        sut.ShowHiddenInstalledApps = true;
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.Items.Single().IsInstalled.Should().BeTrue();
        sut.Items.Single().IsRetainedOnDevice.Should().BeFalse();
        _dialogService.Verify(s => s.ShowWarningAsync("Device inventory unavailable", "WindowsApps_ShowHiddenInstalled"), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadAppsAndCheckInstallationStatusAsync_OverlapsStatusAndIconsAndWaitsForBoth(
        bool iconsCompleteFirst)
    {
        var definition = CreateTestItem("app1");
        var statusCompletion = new TaskCompletionSource<Dictionary<string, bool>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var iconCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _windowsAppsService.Setup(s => s.GetAppsAsync()).ReturnsAsync(new[] { definition });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .Returns(statusCompletion.Task);
        _iconResolver.Setup(r => r.ResolveBatchAsync(
                It.IsAny<IEnumerable<ItemDefinition>>(), true, It.IsAny<CancellationToken>()))
            .Returns(iconCompletion.Task);

        using var sut = CreateSut();
        var load = sut.LoadAppsAndCheckInstallationStatusAsync();
        try
        {
            sut.LoadAppsAndCheckInstallationStatusAsync().Should().BeSameAs(load);
            _windowsAppsService.Verify(s => s.CheckBatchInstalledAsync(
                It.Is<IEnumerable<ItemDefinition>>(items => items.Single() == definition)), Times.Once);
            _iconResolver.Verify(r => r.ResolveBatchAsync(
                It.Is<IEnumerable<ItemDefinition>>(items => items.Single() == definition),
                true, It.IsAny<CancellationToken>()), Times.Once);

            if (iconsCompleteFirst)
                iconCompletion.SetResult();
            else
                statusCompletion.SetResult(new Dictionary<string, bool> { [definition.Id] = true });

            load.IsCompleted.Should().BeFalse();
            sut.IsLoading.Should().BeTrue();
            sut.IsInitialized.Should().BeFalse();
        }
        finally
        {
            statusCompletion.TrySetResult(new Dictionary<string, bool> { [definition.Id] = true });
            iconCompletion.TrySetResult();
            await load.WaitAsync(TimeSpan.FromSeconds(5));
        }

        sut.IsInitialized.Should().BeTrue();
        sut.IsLoading.Should().BeFalse();
        sut.Items.Single().IsInstalled.Should().BeTrue();
        _windowsAppsService.Verify(s => s.GetAppsAsync(), Times.Once);
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_WhenIconsFail_StillPublishesInstalledStatus()
    {
        var definition = CreateTestItem("app1");
        _windowsAppsService.Setup(s => s.GetAppsAsync()).ReturnsAsync(new[] { definition });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { [definition.Id] = true });
        _iconResolver.Setup(r => r.ResolveBatchAsync(
                It.IsAny<IEnumerable<ItemDefinition>>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Icon source unavailable"));

        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.IsInitialized.Should().BeTrue();
        sut.IsLoading.Should().BeFalse();
        sut.Items.Single().IsInstalled.Should().BeTrue();
        _logService.Verify(l => l.LogWarning(It.Is<string>(message =>
            message.Contains("Icon resolution failed") && message.Contains("Icon source unavailable"))), Times.Once);
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_WhenStatusFails_StillResolvesIcons()
    {
        var definition = CreateTestItem("app1");
        _windowsAppsService.Setup(s => s.GetAppsAsync()).ReturnsAsync(new[] { definition });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ThrowsAsync(new InvalidOperationException("Status unavailable"));

        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.IsInitialized.Should().BeTrue();
        sut.IsLoading.Should().BeFalse();
        _iconResolver.Verify(r => r.ResolveBatchAsync(
            It.Is<IEnumerable<ItemDefinition>>(items => items.Single() == definition),
            true, It.IsAny<CancellationToken>()), Times.Once);
        _logService.Verify(l => l.LogError(It.Is<string>(message =>
            message.Contains("Error checking installation status")), It.IsAny<Exception>()), Times.Once);
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_LoadsItems()
    {
        var items = new List<ItemDefinition>
        {
            CreateTestItem("app1", "App One"),
            CreateTestItem("app2", "App Two")
        };
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(items);
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>
            {
                ["app1"] = true,
                ["app2"] = false
            });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.Items.Should().HaveCount(2);
        sut.ItemsView.Cast<AppItemViewModel>().Select(a => a.Id).Should().Equal("app1");
        sut.WindowsAppsFiltered.Select(a => a.Id).Should().Equal("app1");
        sut.IsInitialized.Should().BeTrue();
        sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_WhenAlreadyInitialized_SkipsLoading()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();

        await sut.LoadAppsAndCheckInstallationStatusAsync();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        _windowsAppsService.Verify(s => s.GetAppsAsync(), Times.Once);
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_OnError_LogsErrorAndCompletes()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ThrowsAsync(new InvalidOperationException("Service error"));

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        // The error is logged; finalization still sets "Loaded 0 items"
        _logService.Verify(l => l.LogError(
            It.Is<string>(s => s.Contains("Error loading app definitions")),
            It.IsAny<Exception>()), Times.Once);
        sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_SetsIsLoadingDuringOperation()
    {
        var sut = CreateSut();
        bool wasLoadingDuringGet = false;

        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .Callback(() => wasLoadingDuringGet = sut.IsLoading)
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        await sut.LoadAppsAndCheckInstallationStatusAsync();

        wasLoadingDuringGet.Should().BeTrue();
        // After completion, IsLoading should be false
        sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshInstallationStatusAsync_WhenNotInitialized_SetsWaitMessage()
    {
        var sut = CreateSut();

        await sut.RefreshInstallationStatusAsync();

        sut.StatusText.Should().Be("Progress_WaitForInitialLoad");
    }

    [Fact]
    public async Task RefreshInstallationStatusAsync_WhenInitialized_InvalidatesCacheAndRefreshes()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        await sut.RefreshInstallationStatusAsync();

        _windowsAppsService.Verify(s => s.InvalidateStatusCache(), Times.Once);
        sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAppsAsync_WhenNoItemsSelected_ShowsWarning()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[] { CreateTestItem("app1") });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        await sut.RemoveAppsAsync();

        _dialogService.Verify(d => d.ShowWarningAsync(
            It.Is<string>(s => s.Contains("select at least one")),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ClearSelections_DeselectsAllItems()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", "App1"),
                CreateTestItem("app2", "App2")
            });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true, ["app2"] = false });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        foreach (var item in sut.Items) item.IsSelected = true;
        sut.HasSelectedItems.Should().BeTrue();

        sut.ClearSelections();

        sut.Items.Should().OnlyContain(i => !i.IsSelected);
        sut.HasSelectedItems.Should().BeFalse();
    }

    [Fact]
    public async Task SelectedItemsChanged_RaisedWhenItemSelectionChanges()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[] { CreateTestItem("app1") });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = false });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        bool eventRaised = false;
        sut.SelectedItemsChanged += (_, _) => eventRaised = true;

        sut.Items[0].IsSelected = true;

        eventRaised.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleSelectAll_SelectsAllItems()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1"),
                CreateTestItem("app2")
            });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true, ["app2"] = true });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.ToggleSelectAllCommand.Execute(null);

        sut.Items.Should().OnlyContain(i => i.IsSelected);
        sut.IsAllSelected.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleSelectAll_WhenAllSelected_DeselectsAll()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1"),
                CreateTestItem("app2")
            });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true, ["app2"] = true });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        // Select all first
        sut.ToggleSelectAllCommand.Execute(null);
        sut.IsAllSelected.Should().BeTrue();

        // Toggle again to deselect
        sut.ToggleSelectAllCommand.Execute(null);

        sut.Items.Should().OnlyContain(i => !i.IsSelected);
    }

    [Fact]
    public async Task ToggleSelectAllInstalled_SelectsOnlyInstalledItems()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", isInstalled: true),
                CreateTestItem("app2", isInstalled: false)
            });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true, ["app2"] = false });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.ToggleSelectAllInstalledCommand.Execute(null);

        var installedItem = sut.Items.First(i => i.IsInstalled);
        var notInstalledItem = sut.Items.First(i => !i.IsInstalled);

        installedItem.IsSelected.Should().BeTrue();
        notInstalledItem.IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task ToggleSelectAllNotInstalled_SelectsOnlyNotInstalledItems()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", isInstalled: true),
                CreateTestItem("app2", isInstalled: false)
            });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true, ["app2"] = false });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.ToggleSelectAllNotInstalledCommand.Execute(null);

        var installedItem = sut.Items.First(i => i.IsInstalled);
        var notInstalledItem = sut.Items.First(i => !i.IsInstalled);

        installedItem.IsSelected.Should().BeFalse();
        notInstalledItem.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_CleansUpEventSubscriptions()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[] { CreateTestItem("app1") });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = false });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.Dispose();

        // After dispose, items should still be accessible but the ViewModel should be disposed
        sut.Items.Should().NotBeNull();
    }

    [Fact]
    public async Task VisibleApps_RefreshWhenInstallationStatusChanges()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[] { CreateTestItem("app1"), CreateTestItem("app2") });
        var status = new Dictionary<string, bool> { ["app1"] = true, ["app2"] = false };
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(() => status);
        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.ItemsView.Cast<AppItemViewModel>().Select(a => a.Id).Should().Equal("app1");

        sut.Items[0].IsSelected = true;
        status["app1"] = false;
        status["app2"] = true;
        await sut.RefreshInstallationStatusAsync();

        sut.ItemsView.Cast<AppItemViewModel>().Select(a => a.Id).Should().Equal("app2");
        sut.HasSelectedItems.Should().BeFalse();
        sut.WindowsAppsFiltered.Select(a => a.Id).Should().Equal("app2");
    }

    [Fact]
    public async Task SelectAll_WithSearch_SelectsOnlyMatchingInstalledApps()
    {
        _windowsAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", "Matching Installed"),
                CreateTestItem("app2", "Matching Available"),
                CreateTestItem("app3", "Other Installed")
            });
        _windowsAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>
            {
                ["app1"] = true, ["app2"] = false, ["app3"] = true
            });
        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.SearchText = "Matching";

        sut.ToggleSelectAllCommand.Execute(null);

        sut.Items.Where(a => a.IsSelected).Select(a => a.Id).Should().Equal("app1");
        sut.IsAllSelected.Should().BeTrue();
        sut.ToggleSelectAllCommand.Execute(null);
        sut.Items.Should().OnlyContain(a => !a.IsSelected);
        sut.SearchText = "Available";
        sut.ItemsView.Should().BeEmpty();
        sut.IsAllSelected.Should().BeFalse();
    }
}
