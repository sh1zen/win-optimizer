using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Settings.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks saved update choices, ordered writes, failure recovery and explicit loading.</summary>
public sealed class UpdatePreferencesViewModelTests
{
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private UpdateOptions _saved = new();

    public UpdatePreferencesViewModelTests()
    {
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .ReturnsAsync(() => _saved);
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .Callback<string, UpdateOptions>((_, options) => _saved = options).ReturnsAsync(OperationResult.Succeeded());
    }

    private UpdatePreferencesViewModel CreateViewModel() => new(_preferences.Object, _localization.Object);

    [Fact]
    public async Task ConstructionPerformsNoPersistence_AndExplicitLoadRestoresBothChoicesWithoutSaving()
    {
        _saved = new() { CheckApplicationUpdatesAutomatically = false, UpdateRemovalScriptsAutomatically = false };
        using var vm = CreateViewModel();
        _preferences.VerifyNoOtherCalls();
        Assert.False(vm.CanEdit);
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.CanEdit);
        Assert.False(vm.CheckApplicationUpdatesAutomatically);
        Assert.False(vm.UpdateRemovalScriptsAutomatically);
        _preferences.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()), Times.Never);
    }

    [Fact]
    public async Task RapidEditsAreSavedInOrder_AndOnlySuccessfulWritesPublishTheirSnapshot()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        var pending = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<UpdateOptions>();
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .Returns((string _, UpdateOptions options) =>
            {
                writes.Add(options);
                return writes.Count == 1 ? pending.Task : Task.FromResult(OperationResult.Succeeded());
            });
        vm.CheckApplicationUpdatesAutomatically = false;
        vm.UpdateRemovalScriptsAutomatically = false;
        Assert.Single(writes);
        Assert.Equal(new UpdateOptions(), vm.Options);
        pending.SetResult(OperationResult.Succeeded());
        Assert.True(await vm.FlushAsync());
        Assert.Equal(new[]
        {
            new UpdateOptions { CheckApplicationUpdatesAutomatically = false },
            new UpdateOptions { CheckApplicationUpdatesAutomatically = false, UpdateRemovalScriptsAutomatically = false }
        }, writes);
        Assert.Equal(writes[1], vm.Options);
    }

    [Fact]
    public async Task FailedSaveRetainsPublishedPreferences_AndNextEditCanRetry()
    {
        using var vm = CreateViewModel();
        await vm.LoadAsync();
        _preferences.SetupSequence(service => service.SetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .ReturnsAsync(OperationResult.Failed("read-only preferences"))
            .ReturnsAsync(OperationResult.Succeeded());
        vm.CheckApplicationUpdatesAutomatically = false;
        Assert.False(await vm.FlushAsync());
        Assert.Equal("read-only preferences", vm.Status);
        Assert.Equal(new UpdateOptions(), vm.Options);
        vm.UpdateRemovalScriptsAutomatically = false;
        Assert.True(await vm.FlushAsync());
        Assert.Empty(vm.Status);
        Assert.False(vm.Options.CheckApplicationUpdatesAutomatically);
        Assert.False(vm.Options.UpdateRemovalScriptsAutomatically);
    }

    [Fact]
    public async Task SavedChoicesSurviveReopening_AndResetRestoresEnabledDefaults()
    {
        using (var vm = CreateViewModel())
        {
            await vm.LoadAsync();
            vm.CheckApplicationUpdatesAutomatically = false;
            vm.UpdateRemovalScriptsAutomatically = false;
            Assert.True(await vm.FlushAsync());
        }
        using var reopened = CreateViewModel();
        await reopened.LoadAsync();
        Assert.False(reopened.CheckApplicationUpdatesAutomatically);
        Assert.False(reopened.UpdateRemovalScriptsAutomatically);
        Assert.True(await reopened.ResetAsync());
        Assert.True(reopened.CheckApplicationUpdatesAutomatically);
        Assert.True(reopened.UpdateRemovalScriptsAutomatically);
        Assert.Equal(new UpdateOptions(), _saved);
    }

    [Fact]
    public async Task FailedLoadDisablesEditsAndCanBeRetried()
    {
        _preferences.SetupSequence(service => service.GetPreferenceAsync(UserPreferenceKeys.Updates, It.IsAny<UpdateOptions>()))
            .ThrowsAsync(new IOException("preferences unavailable"))
            .ReturnsAsync(new UpdateOptions());
        using var vm = CreateViewModel();
        Assert.False(await vm.LoadAsync());
        Assert.False(vm.CanEdit);
        Assert.Equal("preferences unavailable", vm.Status);
        Assert.True(await vm.LoadAsync());
        Assert.True(vm.CanEdit);
        Assert.Empty(vm.Status);
    }
}
