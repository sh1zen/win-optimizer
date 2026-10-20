using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
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
    public async Task GetSelectedWindowsAppsAsync_WithMixedSelection_ReturnsOnlySelected()
    {
        var vm = CreateWindowsAppsVm();

        _mockWindowsAppsService
            .Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new List<ItemDefinition>());

        await vm.LoadItemsAsync();

        var selectedApp = CreateAppItemViewModel("app1", "Selected App", isSelected: true, appxPackageName: ["Microsoft.App1"]);
        var unselectedApp = CreateAppItemViewModel("app2", "Unselected App", isSelected: false, appxPackageName: ["Microsoft.App2"]);
        var anotherSelectedApp = CreateAppItemViewModel("app3", "Another Selected", isSelected: true, appxPackageName: ["Microsoft.App3"]);

        vm.Items.Add(selectedApp);
        vm.Items.Add(unselectedApp);
        vm.Items.Add(anotherSelectedApp);

        var sut = CreateSut(vm);

        var result = await sut.GetSelectedWindowsAppsAsync();

        result.Should().HaveCount(2);
        result.Select(r => r.Id).Should().Contain("app1");
        result.Select(r => r.Id).Should().Contain("app3");
        result.Select(r => r.Id).Should().NotContain("app2");
    }

    [Fact]
    public async Task GetSelectedWindowsAppsAsync_WithAppxAndCapability_PrefersAppx()
    {
        var vm = CreateWindowsAppsVm();

        _mockWindowsAppsService
            .Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new List<ItemDefinition>());

        await vm.LoadItemsAsync();

        // An item with both AppxPackageName and CapabilityName set
        // The code checks AppxPackageName first via !string.IsNullOrEmpty
        var definition = new ItemDefinition
        {
            Id = "dual-app",
            Name = "Dual App",
            Description = "Has both",
            AppxPackageName = ["Microsoft.DualApp"],
            CapabilityName = "DualApp.Capability~~~~0.0.1.0"
        };

        var appVm = new AppItemViewModel(
            definition,
            _mockLocalizationService.Object,
            _mockDispatcherService.Object,
            _mockThemeService.Object);
        appVm.IsSelected = true;
        vm.Items.Add(appVm);

        var sut = CreateSut(vm);

        var result = await sut.GetSelectedWindowsAppsAsync();

        result.Should().ContainSingle();
        var item = result[0];
        item.AppxPackageName.Should().BeEquivalentTo(new[] { "Microsoft.DualApp" });
        // CapabilityName should NOT be set because the Appx branch ran
        item.CapabilityName.Should().BeNull();
    }

    [Fact]
    public async Task GetSelectedWindowsAppsAsync_WhenNotInitialized_CallsLoadItemsAsync()
    {
        var vm = CreateWindowsAppsVm();

        _mockWindowsAppsService
            .Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new List<ItemDefinition>());

        // Do NOT call LoadItemsAsync - let the provider do it
        var sut = CreateSut(vm);

        var result = await sut.GetSelectedWindowsAppsAsync();

        // The VM should now be initialized
        result.Should().BeEmpty();
        _mockWindowsAppsService.Verify(s => s.GetAppsAsync(), Times.Once);
    }

    [Fact]
    public async Task GetSelectedWindowsAppsAsync_WhenAlreadyInitialized_DoesNotReload()
    {
        var vm = CreateWindowsAppsVm();

        _mockWindowsAppsService
            .Setup(s => s.GetAppsAsync())
            .ReturnsAsync(new List<ItemDefinition>());

        // Pre-initialize the VM
        await vm.LoadItemsAsync();

        var sut = CreateSut(vm);

        await sut.GetSelectedWindowsAppsAsync();

        // Should only have been called once (the explicit call above), not again
        _mockWindowsAppsService.Verify(s => s.GetAppsAsync(), Times.Once);
    }
}
