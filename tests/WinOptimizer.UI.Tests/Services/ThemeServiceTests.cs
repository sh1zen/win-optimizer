using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Events;
using WinOptimizer.Core.Features.Common.Events.Settings;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Common.Services;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

public class ThemeServiceTests
{
    private readonly Mock<IUserPreferencesService> _mockUserPreferences = new();
    private readonly Mock<IWindowsRegistryService> _mockRegistryService = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IEventBus> _mockEventBus = new();
    private readonly Mock<IMainWindowProvider> _mockMainWindowProvider = new();

    public ThemeServiceTests()
    {
        // Default: not OTS elevation so constructor does not subscribe to SettingAppliedEvent
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);
        // Default: no main window (avoids WinUI framework calls in tests)
        _mockMainWindowProvider.Setup(p => p.MainWindow).Returns((Microsoft.UI.Xaml.Window?)null);
    }

    private ThemeService CreateService()
    {
        return new ThemeService(
            _mockUserPreferences.Object,
            _mockRegistryService.Object,
            _mockInteractiveUserService.Object,
            _mockEventBus.Object,
            _mockMainWindowProvider.Object);
    }

    [Theory]
    [InlineData(WinOptimizerTheme.System)]
    [InlineData(WinOptimizerTheme.LightNative)]
    [InlineData(WinOptimizerTheme.DarkNative)]
    public void SetTheme_SetsCorrectThemeForEachOption(WinOptimizerTheme theme)
    {
        _mockUserPreferences
            .Setup(u => u.SetPreferenceAsync("Theme", It.IsAny<string>()))
            .ReturnsAsync(OperationResult.Succeeded());

        var service = CreateService();

        service.SetTheme(theme);

        service.CurrentTheme.Should().Be(theme);
        _mockUserPreferences.Verify(
            u => u.SetPreferenceAsync("Theme", theme.ToString()),
            Times.Once);
    }

    [Fact]
    public void LoadSavedTheme_WithValidPreference_SetsCurrentTheme()
    {
        _mockUserPreferences
            .Setup(u => u.GetPreference<string>("Theme", string.Empty))
            .Returns("DarkNative");

        var service = CreateService();

        service.LoadSavedTheme();

        service.CurrentTheme.Should().Be(WinOptimizerTheme.DarkNative);
    }

    [Fact]
    public void LoadSavedTheme_WithLightNativePreference_SetsLightNative()
    {
        _mockUserPreferences
            .Setup(u => u.GetPreference<string>("Theme", string.Empty))
            .Returns("LightNative");

        var service = CreateService();

        service.LoadSavedTheme();

        service.CurrentTheme.Should().Be(WinOptimizerTheme.LightNative);
    }

    [Fact]
    public void GetEffectiveTheme_WhenLightNative_ReturnsLight()
    {
        var service = CreateService();

        service.SetTheme(WinOptimizerTheme.LightNative);

        service.GetEffectiveTheme().Should().Be(Microsoft.UI.Xaml.ElementTheme.Light);
    }

    [Fact]
    public void GetEffectiveTheme_WhenDarkNative_ReturnsDark()
    {
        var service = CreateService();

        service.SetTheme(WinOptimizerTheme.DarkNative);

        service.GetEffectiveTheme().Should().Be(Microsoft.UI.Xaml.ElementTheme.Dark);
    }

    [Fact]
    public void Constructor_WhenOtsElevation_SubscribesToSettingAppliedEvent()
    {
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(true);

        var service = CreateService();

        _mockEventBus.Verify(
            e => e.Subscribe(It.IsAny<Action<SettingAppliedEvent>>()),
            Times.Once);
    }

    [Fact]
    public void Constructor_WhenNotOtsElevation_DoesNotSubscribeToSettingAppliedEvent()
    {
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(false);

        var service = CreateService();

        _mockEventBus.Verify(
            e => e.Subscribe(It.IsAny<Action<SettingAppliedEvent>>()),
            Times.Never);
    }

    [Fact]
    public void OnSettingApplied_WhenNotThemeMode_DoesNothing()
    {
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(true);

        Action<SettingAppliedEvent>? capturedHandler = null;
        _mockEventBus
            .Setup(e => e.Subscribe(It.IsAny<Action<SettingAppliedEvent>>()))
            .Callback<Action<SettingAppliedEvent>>(handler => capturedHandler = handler)
            .Returns(Mock.Of<ISubscriptionToken>());

        var service = CreateService();
        capturedHandler.Should().NotBeNull();

        // Publish an unrelated setting event; no crash expected
        var unrelatedEvent = new SettingAppliedEvent("some-other-setting", true);
        capturedHandler!.Invoke(unrelatedEvent);

        // Verify no dispatch happened (MainWindow is null, so DispatcherQueue call would be skipped)
        _mockMainWindowProvider.Verify(p => p.MainWindow, Times.AtMostOnce());
    }

    [Fact]
    public void OnSettingApplied_WhenThemeModeButNotSystemTheme_DoesNothing()
    {
        _mockInteractiveUserService.Setup(s => s.IsOtsElevation).Returns(true);

        Action<SettingAppliedEvent>? capturedHandler = null;
        _mockEventBus
            .Setup(e => e.Subscribe(It.IsAny<Action<SettingAppliedEvent>>()))
            .Callback<Action<SettingAppliedEvent>>(handler => capturedHandler = handler)
            .Returns(Mock.Of<ISubscriptionToken>());

        var service = CreateService();

        // Set theme to DarkNative (not System), so the handler should bail early
        service.SetTheme(WinOptimizerTheme.DarkNative);

        var themeEvent = new SettingAppliedEvent("theme-mode-windows", true);
        capturedHandler!.Invoke(themeEvent);

        // With DarkNative active, the handler should not try to apply System theme
        // Just verify no exception is thrown
    }

    [Fact]
    public void SetTheme_MultipleTimes_FiresEventEachTime()
    {
        var service = CreateService();
        var receivedThemes = new List<WinOptimizerTheme>();
        service.ThemeChanged += (_, theme) => receivedThemes.Add(theme);

        service.SetTheme(WinOptimizerTheme.LightNative);
        service.SetTheme(WinOptimizerTheme.DarkNative);
        service.SetTheme(WinOptimizerTheme.System);

        receivedThemes.Should().HaveCount(3);
        receivedThemes[0].Should().Be(WinOptimizerTheme.LightNative);
        receivedThemes[1].Should().Be(WinOptimizerTheme.DarkNative);
        receivedThemes[2].Should().Be(WinOptimizerTheme.System);
    }

    [Fact]
    public void SetTheme_WhenSavePreferenceThrows_DoesNotThrow()
    {
        _mockUserPreferences
            .Setup(u => u.SetPreferenceAsync("Theme", It.IsAny<string>()))
            .ThrowsAsync(new Exception("Save failed"));

        var service = CreateService();

        // SetTheme does fire-and-forget save, so exception is silently caught
        var act = () => service.SetTheme(WinOptimizerTheme.DarkNative);

        act.Should().NotThrow();
        service.CurrentTheme.Should().Be(WinOptimizerTheme.DarkNative);
    }
}
