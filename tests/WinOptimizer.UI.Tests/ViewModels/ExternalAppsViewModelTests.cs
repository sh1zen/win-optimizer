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

public class ExternalAppsViewModelTests
{
    private readonly Mock<IExternalAppsService> _externalAppsService = new();
    private readonly Mock<IAppUninstallWorkflow> _uninstallWorkflow = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IDialogService> _dialogService = new();
    private readonly Mock<ILocalizationService> _localizationService = new();
    private readonly Mock<IDispatcherService> _dispatcherService = new();
    private readonly Mock<IThemeService> _themeService = new();
    private readonly Mock<IAppIconResolver> _icons = new();

    public ExternalAppsViewModelTests()
    {
        _dispatcherService.Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _dispatcherService.Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);
        _dispatcherService.Setup(d => d.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);

        _localizationService.Setup(l => l.GetString(It.IsAny<string>()))
            .Returns<string>(k => k);
    }

    private ExternalAppsViewModel CreateSut() => new(
        _externalAppsService.Object,
        _uninstallWorkflow.Object,
        _logService.Object,
        _dialogService.Object,
        _localizationService.Object,
        _dispatcherService.Object,
        _themeService.Object,
        iconResolver: _icons.Object);

    [Fact]
    public async Task RefreshResolvesRegisteredIconsForNewDesktopRowsWithoutLosingSelection()
    {
        var first = CreateTestItem("first", isInstalled: true);
        var added = CreateTestItem("added", isInstalled: true);
        _externalAppsService.SetupSequence(source => source.GetAppsAsync()).ReturnsAsync([first]).ReturnsAsync([first, added]);
        _externalAppsService.Setup(source => source.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { [first.Id] = true, [added.Id] = true });
        _icons.Setup(source => source.ResolveBatchAsync(It.IsAny<IEnumerable<ItemDefinition>>(), false, It.IsAny<CancellationToken>()))
            .Callback((IEnumerable<ItemDefinition> definitions, bool _, CancellationToken _) =>
            {
                foreach (var definition in definitions) definition.IconPath = Path.Combine(Path.GetTempPath(), definition.Id + ".png");
            }).Returns(Task.CompletedTask);
        var viewModel = CreateSut();
        await viewModel.LoadAppsAndCheckInstallationStatusAsync();
        var original = Assert.Single(viewModel.Items); original.IsSelected = true;
        await viewModel.RefreshInstallationStatusAsync();
        Assert.Same(original, viewModel.Items.Single(item => item.Definition.Id == first.Id));
        Assert.True(original.IsSelected);
        Assert.True(viewModel.Items.Single(item => item.Definition.Id == added.Id).HasIcon);
        _icons.Verify(source => source.ResolveBatchAsync(It.Is<IEnumerable<ItemDefinition>>(items => items.Any(item => item.Id == added.Id)),
            false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UninstallApps_WhenNothingSelected_DoesNotRunWorkflow()
    {
        var sut = CreateSut();
        await sut.UninstallAppsAsync();
        _dialogService.Verify(dialog => dialog.ShowWarningAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _uninstallWorkflow.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StatusBatchRebuildsCategoriesOnceAndClearsUnavailableSelections()
    {
        var apps = Enumerable.Range(0, 100).Select(index => CreateTestItem($"app{index}")).ToArray();
        var installed = true;
        _externalAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync(apps);
        _externalAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(() => apps.ToDictionary(app => app.Id, _ => installed));
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        foreach (var item in sut.Items) item.IsSelected = true;
        var changes = 0;
        sut.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(sut.Categories)) changes++; };
        installed = false;

        await sut.CheckInstallationStatusAsync();

        Assert.Equal(1, changes);
        Assert.All(sut.Items, item => { Assert.False(item.IsInstalled); Assert.False(item.IsSelected); });
        Assert.Empty(sut.Categories);
    }

    [Fact]
    public async Task UninstallUpdatesCachedInventoryBeforeReviewWithoutDiscovery()
    {
        var app = CreateTestItem("chosen", isInstalled: true) with { RegistryUninstallKeyPath = @"HKCU\Fixture\Chosen" };
        var installed = true;
        _externalAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync(() => installed ? [app] : Array.Empty<ItemDefinition>());
        _externalAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(() => new Dictionary<string, bool> { [app.Id] = installed });
        _dialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.Items[0].IsSelected = true;
        _uninstallWorkflow.Setup(workflow => workflow.UninstallAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), AppUninstallKind.External, true,
            It.IsAny<CancellationToken>(), It.IsAny<Func<AppUninstallResult, Task>>()))
            .Returns(async (IReadOnlyList<ItemDefinition> apps, AppUninstallKind kind, bool scripts, CancellationToken ct, Func<AppUninstallResult, Task>? refresh) =>
            {
                installed = false;
                var result = new AppUninstallResult([new(app.Id, app.Name, UninstallVerificationState.Removed, "Verified")], []);
                await refresh!(result);
                Assert.Empty(sut.Items);
                Assert.Empty(sut.ItemsView);
                Assert.True(sut.IsTaskRunning);
                return result;
            });

        await sut.UninstallAppsAsync();

        Assert.Empty(sut.Items);
        Assert.False(sut.IsTaskRunning);
        _externalAppsService.Verify(service => service.InvalidateStatusCache(), Times.Once);
        _externalAppsService.Verify(service => service.GetAppsAsync(), Times.Once);
        _externalAppsService.Verify(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()), Times.Once);
        _icons.Verify(service => service.ResolveBatchAsync(It.IsAny<IEnumerable<ItemDefinition>>(), false, It.IsAny<CancellationToken>()), Times.Once);

        await sut.RefreshInstallationStatusAsync();
        Assert.Empty(sut.Items);
        _externalAppsService.Verify(service => service.GetAppsAsync(), Times.Exactly(2));
        _externalAppsService.Verify(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnverifiedCancellationOrFailurePreservesCacheWithoutDiscovery(bool cancelled)
    {
        var app = CreateTestItem("chosen", isInstalled: true);
        var installed = true;
        _externalAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync(() => installed ? [app] : Array.Empty<ItemDefinition>());
        _externalAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(() => new Dictionary<string, bool> { [app.Id] = installed });
        _dialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _uninstallWorkflow.Setup(workflow => workflow.UninstallAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), AppUninstallKind.External, true,
            It.IsAny<CancellationToken>(), It.IsAny<Func<AppUninstallResult, Task>>()))
            .Callback(() => installed = false)
            .ThrowsAsync(cancelled ? new OperationCanceledException() : new IOException("Native launch failed"));
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.Items[0].IsSelected = true;

        await sut.UninstallAppsAsync();

        Assert.True(Assert.Single(sut.Items).IsInstalled);
        Assert.False(sut.Items[0].IsSelected);
        Assert.False(sut.IsTaskRunning);
        _externalAppsService.Verify(service => service.InvalidateStatusCache(), Times.Once);
        _externalAppsService.Verify(service => service.GetAppsAsync(), Times.Once);
        _externalAppsService.Verify(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()), Times.Once);
    }

    [Theory]
    [InlineData(UninstallVerificationState.Removed)]
    [InlineData(UninstallVerificationState.StillInstalled)]
    [InlineData(UninstallVerificationState.Unknown)]
    [InlineData(UninstallVerificationState.Deferred)]
    public async Task CachedUninstallUsesOnlyVerifiedRemovalAndRetainsUnrelatedRows(UninstallVerificationState state)
    {
        var chosen = CreateTestItem("chosen", isInstalled: true);
        var other = CreateTestItem("other", isInstalled: true);
        _externalAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync([chosen, other]);
        _externalAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { [chosen.Id] = true, [other.Id] = true });
        _dialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>()))
            .ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        var original = sut.Items[0];
        original.IsSelected = true;
        var metadata = new AppMetadata(null, 123, null);
        chosen.Metadata = metadata;
        other.Metadata = metadata;
        _uninstallWorkflow.Setup(workflow => workflow.UninstallAsync(It.IsAny<IReadOnlyList<ItemDefinition>>(), AppUninstallKind.External, true,
            It.IsAny<CancellationToken>(), It.IsAny<Func<AppUninstallResult, Task>>()))
            .Returns(async (IReadOnlyList<ItemDefinition> apps, AppUninstallKind kind, bool scripts, CancellationToken ct, Func<AppUninstallResult, Task>? refresh) =>
            {
                var result = new AppUninstallResult([new(chosen.Id, chosen.Name, state, "Verified")], []);
                await refresh!(result);
                return result;
            });

        await sut.UninstallAppsAsync();

        Assert.Same(original, sut.Items[0]);
        Assert.Equal(state != UninstallVerificationState.Removed, original.IsInstalled);
        Assert.Same(state == UninstallVerificationState.Removed ? null : metadata, chosen.Metadata);
        Assert.True(sut.Items[1].IsInstalled);
        Assert.Same(metadata, other.Metadata);
        Assert.All(sut.Items, item => Assert.False(item.IsSelected));
        _externalAppsService.Verify(service => service.GetAppsAsync(), Times.Once);
        _externalAppsService.Verify(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()), Times.Once);
    }

    [Fact]
    public async Task UninstallApps_RequiresConfirmationBeforeCallingWorkflow()
    {
        _externalAppsService.Setup(service => service.GetAppsAsync()).ReturnsAsync([CreateTestItem("chosen", isInstalled: true)]);
        _externalAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>())).ReturnsAsync(new Dictionary<string, bool> { ["chosen"] = true });
        _dialogService.Setup(dialog => dialog.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        sut.Items[0].IsSelected = true;

        await sut.UninstallAppsAsync();

        _dialogService.Verify(dialog => dialog.ShowConfirmationAsync(It.Is<ConfirmationRequest>(request =>
            request.Title == "Dialog_ConfirmRemoval" && request.Items!.Count == 1)), Times.Once);
        _uninstallWorkflow.VerifyNoOtherCalls();
        Assert.True(sut.Items[0].IsInstalled);
    }

    private ItemDefinition CreateTestItem(string id, string name = "Test App",
        string group = "Browsers", bool isInstalled = false) => new()
    {
        Id = id,
        Name = name,
        Description = $"Description for {name}",
        GroupName = group,
        WinGetPackageId = new[] { $"Publisher.{name.Replace(" ", "")}" },
        IsInstalled = isInstalled
    };

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_LoadsItems()
    {
        var items = new List<ItemDefinition>
        {
            CreateTestItem("app1", "Firefox", "Browsers"),
            CreateTestItem("app2", "7-Zip", "Compression")
        };
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(items);
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>
            {
                ["app1"] = true,
                ["app2"] = false
            });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.Items.Should().HaveCount(2);
        sut.ItemsView.Cast<AppItemViewModel>().Select(a => a.Id).Should().Equal("app1");
        sut.Categories.SelectMany(c => c.Apps).Select(a => a.Id).Should().Equal("app1");
        sut.IsInitialized.Should().BeTrue();
        sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_WhenAlreadyInitialized_SkipsLoading()
    {
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();

        await sut.LoadAppsAndCheckInstallationStatusAsync();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        _externalAppsService.Verify(s => s.GetAppsAsync(), Times.Once);
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_OnError_LogsErrorAndCompletes()
    {
        _externalAppsService.Setup(s => s.GetAppsAsync())
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
    public async Task LoadAppsAndCheckInstallationStatusAsync_BuildsCategories()
    {
        var items = new List<ItemDefinition>
        {
            CreateTestItem("app1", "Firefox", "Browsers"),
            CreateTestItem("app2", "Chrome", "Browsers"),
            CreateTestItem("app3", "7-Zip", "Compression")
        };
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(items);
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>
            {
                ["app1"] = true,
                ["app2"] = true,
                ["app3"] = true
            });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.Categories.Should().HaveCount(2);
    }

    [Fact]
    public async Task LoadAppsAndCheckInstallationStatusAsync_SortsAppsAlphabeticallyWithinCategories()
    {
        // Provide apps in reverse alphabetical order
        var items = new List<ItemDefinition>
        {
            CreateTestItem("app1", "Firefox", "Browsers"),
            CreateTestItem("app2", "Arc", "Browsers"),
            CreateTestItem("app3", "Chrome", "Browsers")
        };
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(items);
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>
            {
                ["app1"] = true,
                ["app2"] = true,
                ["app3"] = true
            });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.Categories.Should().HaveCount(1);
        var category = sut.Categories[0];
        var names = category.Apps.Select(i => i.Name).ToList();
        names.Should().BeInAscendingOrder();
        names.Should().ContainInOrder("Arc", "Chrome", "Firefox");
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
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(Enumerable.Empty<ItemDefinition>());
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool>());

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        await sut.RefreshInstallationStatusAsync();

        _externalAppsService.Verify(s => s.InvalidateStatusCache(), Times.Once);
        sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task UninstallAppsAsync_WhenNoItemsSelected_ShowsWarning()
    {
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[] { CreateTestItem("app1") });
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        await sut.UninstallAppsAsync();

        _dialogService.Verify(d => d.ShowWarningAsync(
            It.Is<string>(s => s.Contains("select at least one")),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ClearSelections_DeselectsAllItems()
    {
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", "Firefox"),
                CreateTestItem("app2", "Chrome")
            });
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
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
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[] { CreateTestItem("app1") });
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
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
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1"),
                CreateTestItem("app2")
            });
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync(new Dictionary<string, bool> { ["app1"] = true, ["app2"] = true });

        var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();

        sut.ToggleSelectAllCommand.Execute(null);

        sut.Items.Should().OnlyContain(i => i.IsSelected);
        sut.IsAllSelected.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleSelectAllInstalled_SelectsOnlyInstalledItems()
    {
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", isInstalled: true),
                CreateTestItem("app2", isInstalled: false)
            });
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
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
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", isInstalled: true),
                CreateTestItem("app2", isInstalled: false)
            });
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
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
    public async Task VisibleApps_RefreshWhenInstallationStatusChanges()
    {
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[] { CreateTestItem("app1"), CreateTestItem("app2") });
        var status = new Dictionary<string, bool> { ["app1"] = true, ["app2"] = false };
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
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
        sut.Categories.SelectMany(c => c.Apps).Select(a => a.Id).Should().Equal("app2");
    }

    [Fact]
    public async Task RefreshDiscoversNewProgramsAndPreservesExistingSelections()
    {
        var existing = CreateTestItem("existing", "Existing tool");
        var poedit = new ItemDefinition
        {
            Id = "installed-program-poedit", Name = "Poedit", Description = "", GroupName = "Other Utilities",
            RegistryUninstallKeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Poedit_is1",
            CanBeReinstalled = false,
        };
        _externalAppsService.SetupSequence(service => service.GetAppsAsync())
            .ReturnsAsync(new[] { existing })
            .ReturnsAsync(new[] { existing with { }, poedit })
            .ReturnsAsync(new[] { existing with { } });
        _externalAppsService.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .ReturnsAsync((IEnumerable<ItemDefinition> items) => items.ToDictionary(item => item.Id, _ => true));
        using var sut = CreateSut();
        await sut.LoadAppsAndCheckInstallationStatusAsync();
        var originalViewModel = Assert.Single(sut.Items);
        originalViewModel.IsSelected = true;

        await sut.RefreshInstallationStatusAsync();

        Assert.Same(originalViewModel, sut.Items.Single(item => item.Id == existing.Id));
        Assert.True(originalViewModel.IsSelected);
        Assert.Contains(sut.ItemsView.Cast<AppItemViewModel>(), item => item.Id == poedit.Id);
        Assert.Contains(sut.Categories.SelectMany(category => category.Apps), item => item.Id == poedit.Id);
        sut.SearchText = "Poedit";
        Assert.Equal(poedit.Id, Assert.Single(sut.ItemsView.Cast<AppItemViewModel>()).Id);
        sut.Items.Single(item => item.Id == poedit.Id).IsSelected = true;

        await sut.RefreshInstallationStatusAsync();

        Assert.DoesNotContain(sut.Items, item => item.Id == poedit.Id);
        Assert.Empty(sut.ItemsView);
        Assert.True(originalViewModel.IsSelected);
    }

    [Fact]
    public async Task SelectAll_WithSearch_SelectsOnlyMatchingInstalledApps()
    {
        _externalAppsService.Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new[]
            {
                CreateTestItem("app1", "Matching Installed"),
                CreateTestItem("app2", "Matching Available"),
                CreateTestItem("app3", "Other Installed")
            });
        _externalAppsService.Setup(s => s.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
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
