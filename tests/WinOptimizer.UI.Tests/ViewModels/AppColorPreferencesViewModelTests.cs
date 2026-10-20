using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Resources;
using WinOptimizer.UI.Features.Settings.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks actual accent resources and the production preference queue without filesystem or Windows writes.</summary>
public sealed class AppColorPreferencesViewModelTests
{
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly AppColorResources _resources = new();
    private string _saved = nameof(AppColor.Blue);

    public AppColorPreferencesViewModelTests()
    {
        _localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.AppColor, nameof(AppColor.Blue))).ReturnsAsync(() => _saved);
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.AppColor, It.IsAny<string>()))
            .Callback<string, string>((_, color) => _saved = color).ReturnsAsync(OperationResult.Succeeded());
    }

    private AppColorPreferencesViewModel Create() => new(_preferences.Object, _localization.Object, _resources);

    [Fact]
    public async Task ExplicitLoadRestoresColorWithoutSavingOrConstructorSideEffects()
    {
        _saved = nameof(AppColor.Purple);
        using var vm = Create();
        _preferences.VerifyNoOtherCalls();
        Assert.False(vm.CanEdit);
        Assert.True(await vm.LoadAsync());
        Assert.Equal(AppColor.Purple, vm.SelectedColor);
        Assert.True(vm.CanEdit);
        _preferences.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.AppColor, It.IsAny<string>()), Times.Never);
        AssertBrush(AppColor.Purple);
    }

    [Fact]
    public async Task ConcurrentLoadsShareOneRead()
    {
        var pending = new TaskCompletionSource<string>();
        _preferences.Setup(service => service.GetPreferenceAsync(UserPreferenceKeys.AppColor, nameof(AppColor.Blue))).Returns(pending.Task);
        using var vm = Create();
        var first = vm.LoadAsync();
        Assert.Same(first, vm.LoadAsync());
        pending.SetResult(nameof(AppColor.Teal));
        Assert.True(await first);
        _preferences.Verify(service => service.GetPreferenceAsync(UserPreferenceKeys.AppColor, nameof(AppColor.Blue)), Times.Once);
    }

    [Fact]
    public async Task RapidChoicesApplyImmediatelyAndPersistInOrder()
    {
        using var vm = Create();
        await vm.LoadAsync();
        var firstWrite = new TaskCompletionSource<OperationResult>();
        var writes = new List<string>();
        _preferences.Setup(service => service.SetPreferenceAsync(UserPreferenceKeys.AppColor, It.IsAny<string>()))
            .Returns((string _, string color) => { writes.Add(color); return writes.Count == 1 ? firstWrite.Task : Task.FromResult(OperationResult.Succeeded()); });
        vm.SelectedColor = AppColor.Green;
        vm.SelectedColor = AppColor.Pink;
        AssertBrush(AppColor.Pink);
        Assert.Equal(new[] { "Green" }, writes);
        var flush = vm.FlushAsync();
        Assert.False(flush.IsCompleted);
        firstWrite.SetResult(OperationResult.Succeeded());
        Assert.True(await flush);
        Assert.Equal(new[] { "Green", "Pink" }, writes);
    }

    [Fact]
    public async Task ChoiceSurvivesRestartAndResetRestoresBlue()
    {
        using (var vm = Create())
        {
            await vm.LoadAsync();
            vm.SelectedColor = AppColor.Orange;
            Assert.True(await vm.FlushAsync());
        }
        using var reopened = Create();
        await reopened.LoadAsync();
        Assert.Equal(AppColor.Orange, reopened.SelectedColor);
        Assert.True(await reopened.ResetAsync());
        Assert.Equal(nameof(AppColor.Blue), _saved);
        AssertBrush(AppColor.Blue);
    }

    [Fact]
    public async Task InvalidSavedColorIsReportedAndExplicitResetRecovers()
    {
        _saved = "999";
        using var vm = Create();
        Assert.False(await vm.LoadAsync());
        Assert.False(vm.CanEdit);
        Assert.NotEmpty(vm.Status);
        _preferences.Verify(service => service.SetPreferenceAsync(UserPreferenceKeys.AppColor, It.IsAny<string>()), Times.Never);
        Assert.True(await vm.ResetAsync());
        Assert.True(vm.CanEdit);
        Assert.Empty(vm.Status);
        Assert.Equal(nameof(AppColor.Blue), _saved);
    }

    [Fact]
    public async Task ReadFailureCanRetryWithoutWriting()
    {
        _preferences.SetupSequence(service => service.GetPreferenceAsync(UserPreferenceKeys.AppColor, nameof(AppColor.Blue)))
            .ThrowsAsync(new IOException("Read denied")).ReturnsAsync(nameof(AppColor.Green));
        using var vm = Create();
        Assert.False(await vm.LoadAsync());
        Assert.Equal("Read denied", vm.Status);
        Assert.True(await vm.LoadAsync());
        Assert.Equal(AppColor.Green, vm.SelectedColor);
    }

    [Fact]
    public async Task SaveFailureIsVisibleAndNextChoiceRetries()
    {
        using var vm = Create();
        await vm.LoadAsync();
        _preferences.SetupSequence(service => service.SetPreferenceAsync(UserPreferenceKeys.AppColor, It.IsAny<string>()))
            .ReturnsAsync(OperationResult.Failed("Write denied")).ReturnsAsync(OperationResult.Succeeded());
        vm.SelectedColor = AppColor.Green;
        Assert.False(await vm.FlushAsync());
        Assert.Equal("Write denied", vm.Status);
        vm.SelectedColor = AppColor.Purple;
        Assert.True(await vm.FlushAsync());
        Assert.Empty(vm.Status);
    }

    [Fact]
    public void EveryColorUpdatesSharedBrushesAndRetainsHighContrastResources()
    {
        var light = (ResourceDictionary)_resources.ThemeDictionaries["Light"];
        var original = (SolidColorBrush)light["AccentFillColorDefaultBrush"];
        var highContrast = (ResourceDictionary)_resources.ThemeDictionaries["HighContrast"];
        var protectedBrush = (SolidColorBrush)highContrast["SidebarBackgroundBrush"];
        var highContrastColor = protectedBrush.Color;
        var highContrastIconBackground = ((SolidColorBrush)highContrast["PageIconBackgroundBrush"]).Color;
        var highContrastIconForeground = ((SolidColorBrush)highContrast["PageIconForegroundBrush"]).Color;
        foreach (var color in Enum.GetValues<AppColor>())
        {
            _resources.Apply(color);
            Assert.Same(original, light["AccentFillColorDefaultBrush"]);
            Assert.Same(original, light["ToggleSwitchFillOn"]);
            Assert.Same(original, light["AccentButtonBackground"]);
            AssertBrush(color);
            Assert.Equal(highContrastColor, protectedBrush.Color);
            Assert.Equal(highContrastIconBackground, ((SolidColorBrush)highContrast["PageIconBackgroundBrush"]).Color);
            Assert.Equal(highContrastIconForeground, ((SolidColorBrush)highContrast["PageIconForegroundBrush"]).Color);
        }
    }

    [Fact]
    public void PageIconTilesStayWhiteAndKeepReadableContrastForEveryColorAndTheme()
    {
        foreach (var color in Enum.GetValues<AppColor>())
        {
            _resources.Apply(color);
            foreach (var theme in new[] { "Light", "Default" })
            {
                var dictionary = (ResourceDictionary)_resources.ThemeDictionaries[theme];
                var foreground = ((SolidColorBrush)dictionary["PageIconForegroundBrush"]).Color;
                var background = ((SolidColorBrush)dictionary["PageIconBackgroundBrush"]).Color;
                Assert.Equal(Microsoft.UI.Colors.White, foreground);
                Assert.Equal(AppColorResources.ColorsFor(color).Light, background);
                static double Linear(byte channel) => channel / 255.0 <= 0.04045
                    ? channel / 255.0 / 12.92 : Math.Pow((channel / 255.0 + 0.055) / 1.055, 2.4);
                var luminance = 0.2126 * Linear(background.R) + 0.7152 * Linear(background.G) + 0.0722 * Linear(background.B);
                Assert.True(1.05 / (luminance + 0.05) >= 4.5, $"White icon contrast is too low for {color}/{theme}.");
            }
        }
    }

    [Fact]
    public void LanguageChangeRefreshesChoiceLabels()
    {
        using var vm = Create();
        var changed = false;
        vm.Colors[0].PropertyChanged += (_, e) => changed |= e.PropertyName == "DisplayText";
        _localization.Raise(service => service.LanguageChanged += null, EventArgs.Empty);
        Assert.True(changed);
    }

    private void AssertBrush(AppColor color)
    {
        var (light, dark) = AppColorResources.ColorsFor(color);
        Assert.Equal(light, ((SolidColorBrush)((ResourceDictionary)_resources.ThemeDictionaries["Light"])["AccentTextFillColorPrimaryBrush"]).Color);
        Assert.Equal(dark, ((SolidColorBrush)((ResourceDictionary)_resources.ThemeDictionaries["Default"])["AccentTextFillColorPrimaryBrush"]).Color);
    }
}
