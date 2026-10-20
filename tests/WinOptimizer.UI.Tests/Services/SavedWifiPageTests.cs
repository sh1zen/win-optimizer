using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.Services;

/// <summary>Exercises the production password page on WinUI with a synthetic WLAN service.</summary>
public sealed class SavedWifiPageTests
{
    [Fact]
    public async Task NativePageRendersReadablePasswordsAndClearsThemOnNavigationAway()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(value => value.GetString(It.IsAny<string>())).Returns((string key) => key);
        var service = new Mock<ISavedWifiService>();
        service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SavedWifiSnapshot(
            [new("Example network", "Test Wi-Fi", "WPA2PSK", "AES", "synthetic-password", WifiPasswordState.Available)], ""));
        using var vm = new SavedWifiViewModel(service.Object, localization.Object, Mock.Of<IApplicationModeService>(),
            new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());
        var page = new SavedWifiPage(vm);
        var window = new Window { Content = page };
        try
        {
            var loaded = new TaskCompletionSource();
            page.Loaded += (_, _) => loaded.TrySetResult();
            window.Activate();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.UpdateLayout();
            Assert.Single(vm.Profiles);
            Assert.Contains(Descendants<TextBlock>(page), text => text.Text == "synthetic-password" && text.IsTextSelectionEnabled);
            var unloaded = new TaskCompletionSource();
            page.Unloaded += (_, _) => unloaded.TrySetResult();
            window.Content = new Grid();
            await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(vm.Profiles);
            service.Verify(value => value.ReadAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
