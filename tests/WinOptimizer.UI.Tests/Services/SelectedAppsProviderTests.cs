using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using WinOptimizer.UI.Features.SoftwareApps.Services;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class SelectedAppsProviderTests : IDisposable
{
    private readonly Mock<IWindowsAppsService> _mockWindowsAppsService = new();
    private readonly Mock<IAppUninstallWorkflow> _mockUninstallWorkflow = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IDispatcherService> _mockDispatcherService = new();
    private readonly Mock<IThemeService> _mockThemeService = new();

    private WindowsAppsViewModel? _windowsAppsVm;

    public SelectedAppsProviderTests()
    {
        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);
        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadWithContextAsync(It.IsAny<Func<Task>>()))
            .Callback<Func<Task>>(f => f().GetAwaiter().GetResult())
            .Returns(Task.CompletedTask);

        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        _mockThemeService
            .Setup(t => t.GetEffectiveTheme())
            .Returns(ElementTheme.Dark);
    }

    public void Dispose()
    {
        _windowsAppsVm?.Dispose();
    }

    private WindowsAppsViewModel CreateWindowsAppsVm()
    {
        _windowsAppsVm = new WindowsAppsViewModel(
            _mockWindowsAppsService.Object,
            _mockUninstallWorkflow.Object,
            _mockLogService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockDispatcherService.Object,
            _mockThemeService.Object,
            iconResolver: Mock.Of<IAppIconResolver>());
        return _windowsAppsVm;
    }

    private SelectedAppsProvider CreateSut(WindowsAppsViewModel? vm = null)
    {
        return new SelectedAppsProvider(vm ?? CreateWindowsAppsVm());
    }

    private AppItemViewModel CreateAppItemViewModel(
        string id,
        string name,
        bool isSelected = false,
        string[]? appxPackageName = null,
        string? capabilityName = null,
        string? optionalFeatureName = null)
    {
        var definition = new ItemDefinition
        {
            Id = id,
            Name = name,
            Description = $"Description for {name}",
            AppxPackageName = appxPackageName,
            CapabilityName = capabilityName,
            OptionalFeatureName = optionalFeatureName,
        };

        var vm = new AppItemViewModel(
            definition,
            _mockLocalizationService.Object,
            _mockDispatcherService.Object,
            _mockThemeService.Object);

        vm.IsSelected = isSelected;
        return vm;
    }

    [Fact]
    public async Task ExternalAppImportOmitsExplicitDeselectionsFromUninstall()
    {
        var items = new ObservableCollection<AppItemViewModel>
        {
            CreateAppItemViewModel("keep", "Keep", isSelected: true),
            CreateAppItemViewModel("remove", "Remove"),
            CreateAppItemViewModel("legacy", "Legacy")
        };
        var external = new Mock<IExternalAppsItemsProvider>();
        external.SetupGet(provider => provider.IsInitialized).Returns(true);
        external.SetupGet(provider => provider.Items).Returns(items);
        string?[]? removal = null;
        external.Setup(provider => provider.UninstallAppsAsync()).Callback(() =>
        {
            removal = items.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
        }).Returns(Task.CompletedTask);
        var sut = new ConfigAppSelectionService(_mockLogService.Object, Mock.Of<IWindowsAppsItemsProvider>(), external.Object);

        await sut.ProcessExternalAppsRemovalAsync(new ConfigSection
        {
            Items = [new() { Id = "keep", IsSelected = false }, new() { Id = "remove", IsSelected = true }, new() { Id = "legacy" }]
        });

        Assert.Equal(new[] { "remove", "legacy" }, removal);
        Assert.False(items[0].IsSelected);
        external.Verify(provider => provider.UninstallAppsAsync(), Times.Once);
    }

    [Fact]
    public async Task GetSelectedWindowsAppsAsync_ExportsOnlySelectedAppsAndPrefersAppxIdentity()
    {
        var vm = CreateWindowsAppsVm();

        _mockWindowsAppsService
            .Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new List<ItemDefinition>());

        await vm.LoadItemsAsync();

        var selectedApp = CreateAppItemViewModel("app1", "Selected App", isSelected: true,
            appxPackageName: ["Microsoft.App1"], capabilityName: "App1.Capability~~~~0.0.1.0");
        var unselectedApp = CreateAppItemViewModel("app2", "Unselected App", isSelected: false, appxPackageName: ["Microsoft.App2"]);
        var anotherSelectedApp = CreateAppItemViewModel("app3", "Another Selected", isSelected: true, appxPackageName: ["Microsoft.App3"]);

        vm.Items.Add(selectedApp);
        vm.Items.Add(unselectedApp);
        vm.Items.Add(anotherSelectedApp);

        var sut = CreateSut(vm);

        var result = await sut.GetSelectedWindowsAppsAsync();

        result.Select(item => item.Id).Should().BeEquivalentTo(new[] { "app1", "app3" });
        var item = result.Single(item => item.Id == "app1");
        item.AppxPackageName.Should().Equal("Microsoft.App1");
        item.CapabilityName.Should().BeNull();
    }
}
