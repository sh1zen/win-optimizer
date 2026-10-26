using FluentAssertions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Moq;
using Windows.Graphics;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Utilities;
using WinOptimizer.UI.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Utilities;

public class WindowSizeManagerTests
{
    /// <summary>Uses the production placement owner with isolated, typed preferences.</summary>
    private static Mock<IUserPreferencesService> Preferences(Dictionary<string, object> values)
    {
        var preferences = new Mock<IUserPreferencesService>();
        preferences.Setup(service => service.GetPreferenceAsync<double>(It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string key, double fallback) => Task.FromResult(values.TryGetValue(key, out var value) ? (double)value : fallback));
        preferences.Setup(service => service.GetPreferenceAsync<bool>(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string key, bool fallback) => Task.FromResult(values.TryGetValue(key, out var value) ? (bool)value : fallback));
        preferences.Setup(service => service.GetPreferencesAsync()).ReturnsAsync(() => new Dictionary<string, object>(values));
        preferences.Setup(service => service.UpdatePreferencesAsync(It.IsAny<IReadOnlyDictionary<string, object>>()))
            .Returns((IReadOnlyDictionary<string, object> saved) =>
            {
                foreach (var pair in saved) values[pair.Key] = pair.Value;
                return Task.FromResult(OperationResult.Succeeded());
            });
        return preferences;
    }

    /// <summary>First launch uses the same fixed dimensions on any screen.</summary>
    [Fact]
    public async Task InitializeWithoutSavedSizeUsesFixedDefault()
    {
        var window = new Window();
        try
        {
            var manager = new WindowSizeManager(window.AppWindow, Preferences(new()).Object, Mock.Of<ILogService>());
            await manager.InitializeAsync();
            window.AppWindow.Size.Width.Should().Be(1280);
            window.AppWindow.Size.Height.Should().Be(800);
            window.AppWindow.IsVisible.Should().BeFalse();
        }
        finally { window.Close(); }
    }

    /// <summary>Malformed saved dimensions use the fixed first-launch placement.</summary>
    [Theory]
    [InlineData(double.NaN, 720d)]
    [InlineData(1050d, double.PositiveInfinity)]
    [InlineData(-1d, 720d)]
    public async Task InvalidDimensionsUseFixedDefault(double width, double height)
    {
        var window = new Window();
        var values = new Dictionary<string, object>
        {
            [UserPreferenceKeys.WindowWidth] = width, [UserPreferenceKeys.WindowHeight] = height
        };
        try
        {
            await new WindowSizeManager(window.AppWindow, Preferences(values).Object, Mock.Of<ILogService>()).InitializeAsync();
            window.AppWindow.Size.Width.Should().Be(1280);
            window.AppWindow.Size.Height.Should().Be(800);
        }
        finally { window.Close(); }
    }

    /// <summary>A stale monitor position is corrected without changing the saved size.</summary>
    [Fact]
    public async Task OffScreenPlacementRetainsSavedDimensions()
    {
        var window = new Window();
        var values = new Dictionary<string, object>
        {
            [UserPreferenceKeys.WindowWidth] = 1050d, [UserPreferenceKeys.WindowHeight] = 720d,
            [UserPreferenceKeys.WindowLeft] = -100000d, [UserPreferenceKeys.WindowTop] = -100000d
        };
        try
        {
            await new WindowSizeManager(window.AppWindow, Preferences(values).Object, Mock.Of<ILogService>()).InitializeAsync();
            window.AppWindow.Size.Width.Should().Be(1050);
            window.AppWindow.Size.Height.Should().Be(720);
            window.AppWindow.Position.X.Should().NotBe(-100000);
        }
        finally { window.Close(); }
    }

    /// <summary>Closing after a resize persists the latest bounds for the next activation.</summary>
    [Fact]
    public async Task SaveAndReopenRetainsLastUsedSize()
    {
        var values = new Dictionary<string, object>();
        var preferences = Preferences(values);
        var content = new Grid();
        var window = new Window { Content = content };
        try
        {
            var manager = new WindowSizeManager(window.AppWindow, preferences.Object, Mock.Of<ILogService>());
            await manager.InitializeAsync();
            window.Activate();
            window.AppWindow.Resize(new SizeInt32(1100, 750));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => Task.CompletedTask);
            values["Theme"] = "Dark";
            await manager.SaveWindowSettingsAsync();
            Assert.Equal("Dark", values["Theme"]);
            preferences.Verify(service => service.UpdatePreferencesAsync(It.Is<IReadOnlyDictionary<string, object>>(patch => patch.Count == 5 && !patch.ContainsKey("Theme"))), Times.Once);
            preferences.Verify(service => service.GetPreferencesAsync(), Times.Never);
            preferences.Verify(service => service.SavePreferencesAsync(It.IsAny<Dictionary<string, object>>()), Times.Never);
        }
        finally { window.Close(); }

        var reopened = new Window();
        try
        {
            await new WindowSizeManager(reopened.AppWindow, preferences.Object, Mock.Of<ILogService>()).InitializeAsync();
            reopened.AppWindow.Size.Width.Should().Be(1100);
            reopened.AppWindow.Size.Height.Should().Be(750);
            reopened.AppWindow.IsVisible.Should().BeFalse();
        }
        finally { reopened.Close(); }
    }

    /// <summary>Maximized launches retain their normal dimensions across repeated saves.</summary>
    [Fact]
    public async Task RestoredMaximizedWindowPreservesNormalSize()
    {
        var values = new Dictionary<string, object>
        {
            [UserPreferenceKeys.WindowWidth] = 1050d, [UserPreferenceKeys.WindowHeight] = 720d,
            [UserPreferenceKeys.WindowMaximized] = true
        };
        var preferences = Preferences(values);
        var content = new Grid();
        var window = new Window { Content = content };
        try
        {
            var manager = new WindowSizeManager(window.AppWindow, preferences.Object, Mock.Of<ILogService>());
            await manager.InitializeAsync();
            ((OverlappedPresenter)window.AppWindow.Presenter).State.Should().Be(OverlappedPresenterState.Maximized);
            window.AppWindow.IsVisible.Should().BeFalse();
            window.Activate();
            await StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => Task.CompletedTask);
            ((OverlappedPresenter)window.AppWindow.Presenter).State.Should().Be(OverlappedPresenterState.Maximized);
            await manager.SaveWindowSettingsAsync();
            values[UserPreferenceKeys.WindowWidth].Should().Be(1050d);
            values[UserPreferenceKeys.WindowHeight].Should().Be(720d);
            values[UserPreferenceKeys.WindowMaximized].Should().Be(true);
        }
        finally { window.Close(); }
    }

    private static RectInt32 R(int x, int y, int w, int h) => new(x, y, w, h);

    /// <summary>Centering keeps oversized windows' title bars inside small, offset and negative-coordinate displays.</summary>
    [Theory]
    [InlineData(0, 0, 1280, 680, 1280, 800, 0, 0)]
    [InlineData(0, 0, 1024, 728, 1280, 800, 0, 0)]
    [InlineData(-1280, -720, 1280, 680, 1280, 800, -1280, -720)]
    [InlineData(0, 40, 1920, 1040, 1280, 800, 320, 160)]
    [InlineData(1920, 0, 1280, 680, 1050, 720, 2035, 0)]
    public void CenteredPosition_KeepsTitleBarReachable(int x, int y, int w, int h, int width, int height, int expectedX, int expectedY)
    {
        var work = R(x, y, w, h);
        var point = WindowSizeManager.GetCenteredPosition(new SizeInt32(width, height), work);
        point.X.Should().Be(expectedX);
        point.Y.Should().Be(expectedY);
        WindowSizeManager.IsWindowRectVisible(R(point.X, point.Y, width, height), [work]).Should().BeTrue();
    }

    /// <summary>An overlapping body cannot make an off-screen title bar a usable saved placement.</summary>
    [Theory]
    [InlineData(0, -60, 1280, 680)]
    [InlineData(-1280, -780, 1280, 680)]
    [InlineData(0, 0, 1920, 1040)]
    public void SavedPosition_RejectsTitleBarAboveWorkArea(int x, int y, int w, int h)
    {
        WindowSizeManager.IsWindowRectVisible(R(x, y, 1280, 800), [R(x, y + 60, w, h)]).Should().BeFalse();
    }

    /// <summary>Minimization preserves the latest resize and the prior maximized state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingWhileMinimizedRetainsLastVisiblePlacement(bool maximized)
    {
        var values = new Dictionary<string, object>();
        var content = new Grid();
        var window = new Window { Content = content };
        try
        {
            var manager = new WindowSizeManager(window.AppWindow, Preferences(values).Object, Mock.Of<ILogService>());
            await manager.InitializeAsync();
            window.Activate();
            window.AppWindow.Resize(new SizeInt32(1100, 750));
            await StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => Task.CompletedTask);
            var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
            if (maximized)
            {
                presenter.Maximize();
                await StartupUiCoordinator.RunAfterFirstRenderAsync(content, () => Task.CompletedTask);
            }
            presenter.Minimize();
            await manager.SaveWindowSettingsAsync();
            values[UserPreferenceKeys.WindowWidth].Should().Be(1100d);
            values[UserPreferenceKeys.WindowHeight].Should().Be(750d);
            values[UserPreferenceKeys.WindowMaximized].Should().Be(maximized);
        }
        finally { window.Close(); }
    }

    // A single 1920x1080 primary display with a standard taskbar reserved.
    private static readonly RectInt32[] SingleDisplay = { R(0, 0, 1920, 1032) };

    // Two displays side-by-side: primary (1920x1080) + secondary to the right (1920x1080).
    private static readonly RectInt32[] DualDisplay = { R(0, 0, 1920, 1032), R(1920, 0, 1920, 1032) };

    [Fact]
    public void IsWindowRectVisible_FullyOnPrimary_ReturnsTrue()
    {
        WindowSizeManager.IsWindowRectVisible(R(100, 100, 1280, 800), SingleDisplay)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWindowRectVisible_PartiallyOffRight_StillVisibleEnough_ReturnsTrue()
    {
        // Window mostly off the right edge but with 300px still visible.
        WindowSizeManager.IsWindowRectVisible(R(1620, 100, 1280, 800), SingleDisplay)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWindowRectVisible_OffScreenNegative_FromDisconnectedMonitor_ReturnsFalse()
    {
        // Reproduces issue #585: secondary monitor was to the left, saved Left=-1524.
        // After disconnect, only primary (0..1920) remains — window is entirely off.
        WindowSizeManager.IsWindowRectVisible(R(-1524, 200, 1280, 800), SingleDisplay)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWindowRectVisible_OnSecondaryDisplay_ReturnsTrue()
    {
        // Window on the secondary monitor (x=2500) — valid when dual-display is active.
        WindowSizeManager.IsWindowRectVisible(R(2500, 100, 1280, 800), DualDisplay)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWindowRectVisible_OnSecondary_AfterItDisconnects_ReturnsFalse()
    {
        // Same rect as above, but the secondary display is no longer in the list.
        WindowSizeManager.IsWindowRectVisible(R(2500, 100, 1280, 800), SingleDisplay)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWindowRectVisible_TinySliverOverlap_ReturnsFalse()
    {
        // Only 10 pixels of the window's right edge are on the primary — below threshold.
        WindowSizeManager.IsWindowRectVisible(R(-1270, 100, 1280, 800), SingleDisplay)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWindowRectVisible_OverlapMeetsMinimumExactly_ReturnsTrue()
    {
        int x = 1920 - WindowSizeManager.MinVisibleWidth;
        WindowSizeManager.IsWindowRectVisible(R(x, 100, 1280, 800), SingleDisplay)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWindowRectVisible_OverlapOnePixelShy_ReturnsFalse()
    {
        int x = 1920 - WindowSizeManager.MinVisibleWidth + 1;
        WindowSizeManager.IsWindowRectVisible(R(x, 100, 1280, 800), SingleDisplay)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWindowRectVisible_EmptyDisplayList_ReturnsFalse()
    {
        WindowSizeManager.IsWindowRectVisible(R(0, 0, 1280, 800), System.Array.Empty<RectInt32>())
            .Should().BeFalse();
    }

    [Fact]
    public void IsWindowRectVisible_BelowBottomEdge_ReturnsFalse()
    {
        // Window positioned below the work area (e.g. tall monitor removed).
        WindowSizeManager.IsWindowRectVisible(R(100, 2000, 1280, 800), SingleDisplay)
            .Should().BeFalse();
    }
}
