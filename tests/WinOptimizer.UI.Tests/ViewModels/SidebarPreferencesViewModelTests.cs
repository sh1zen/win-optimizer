using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Settings.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks explicit sidebar restoration, ordered saves, restart persistence and failure recovery.</summary>
public sealed class SidebarPreferencesViewModelTests
{
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private bool _saved = true;

    public SidebarPreferencesViewModelTests()
    {
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, true)).ReturnsAsync(() => _saved);
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, It.IsAny<bool>()))
            .Callback<string, bool>((_, expanded) => _saved = expanded).ReturnsAsync(OperationResult.Succeeded());
    }

    private SidebarPreferencesViewModel CreateViewModel() => new(_preferences.Object, _localization.Object);

    [Fact]
    public async Task ConstructionUsesExpandedDefault_AndExplicitLoadRestoresCompactWithoutSaving()
    {
        _saved = false;
        using var vm = CreateViewModel();
        Assert.True(vm.IsExpanded);
        Assert.False(vm.CanEdit);
        _preferences.VerifyNoOtherCalls();
        Assert.True(await vm.LoadAsync());
        Assert.False(vm.IsExpanded);
        Assert.True(vm.CanEdit);
        _preferences.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentShellAndSettingsLoadsShareOneRead()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, true)).Returns(pending.Task);
        using var vm = CreateViewModel();
        var shellLoad = vm.LoadAsync();
        var settingsLoad = vm.LoadAsync();
        Assert.Same(shellLoad, settingsLoad);
        Assert.False(vm.CanEdit);
        pending.SetResult(false);
        Assert.True(await shellLoad);
        Assert.False(vm.IsExpanded);
        _preferences.Verify(service => service.GetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, true), Times.Once);
    }

    [Fact]
    public async Task RapidEditsUpdateLiveLayoutImmediatelyAndFlushSavesInOrder()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        var pending = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<bool>();
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, It.IsAny<bool>()))
            .Returns((string _, bool expanded) =>
            {
                writes.Add(expanded);
                return writes.Count == 1 ? pending.Task : Task.FromResult(OperationResult.Succeeded());
            });
        vm.IsExpanded = false;
        Assert.False(vm.IsExpanded);
        vm.IsExpanded = true;
        Assert.True(vm.IsExpanded);
        Assert.Equal(new[] { false }, writes);
        var flush = vm.FlushAsync();
        Assert.False(flush.IsCompleted);
        pending.SetResult(OperationResult.Succeeded());
        Assert.True(await flush);
        Assert.Equal(new[] { false, true }, writes);
    }

    [Fact]
    public async Task CompactChoiceSurvivesReopening_AndResetPersistsExpandedDefault()
    {
        using (var vm = CreateViewModel())
        {
            await vm.LoadAsync();
            vm.IsExpanded = false;
            Assert.True(await vm.FlushAsync());
        }
        using var reopened = CreateViewModel();
        await reopened.LoadAsync();
        Assert.False(reopened.IsExpanded);
        Assert.True(await reopened.ResetAsync());
        Assert.True(reopened.IsExpanded);
        Assert.True(_saved);
    }

    [Fact]
    public async Task FailedSaveReportsErrorAndNextEditCanRetry()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        _preferences.SetupSequence(service => service.SetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, It.IsAny<bool>()))
            .ReturnsAsync(OperationResult.Failed("read-only preferences"))
            .ReturnsAsync(OperationResult.Succeeded());
        vm.IsExpanded = false;
        Assert.False(await vm.FlushAsync());
        Assert.False(vm.IsExpanded);
        Assert.Equal("read-only preferences", vm.Status);
        vm.IsExpanded = true;
        Assert.True(await vm.FlushAsync());
        Assert.Empty(vm.Status);
    }

    [Fact]
    public async Task FailedReadDisablesEditsAndCanBeRetried()
    {
        _preferences.SetupSequence(service => service.GetPreferenceAsync(UserPreferenceKeys.SidebarExpanded, true))
            .ThrowsAsync(new IOException("preferences unavailable"))
            .ReturnsAsync(false);
        using var vm = CreateViewModel();
        Assert.False(await vm.LoadAsync());
        Assert.True(vm.IsExpanded);
        Assert.False(vm.CanEdit);
        Assert.Equal("preferences unavailable", vm.Status);
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.CanEdit);
        Assert.False(vm.IsExpanded);
        Assert.Empty(vm.Status);
    }

    [Fact]
    public void LanguageChangesRefreshSwitchLabelsAndDisposeUnsubscribes()
    {
        var vm = CreateViewModel();
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        _localization.Raise(service => service.LanguageChanged += null, this, EventArgs.Empty);
        Assert.Contains(string.Empty, notifications);
        Assert.Equal("Settings_ExpandNavigation", vm.ExpandedLabel);
        Assert.Equal("Settings_ExpandNavigationDescription", vm.ExpandedDescription);
        vm.Dispose();
        notifications.Clear();
        _localization.Raise(service => service.LanguageChanged += null, this, EventArgs.Empty);
        Assert.Empty(notifications);
    }
}
