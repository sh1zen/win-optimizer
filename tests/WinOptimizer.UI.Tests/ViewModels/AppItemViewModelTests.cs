using System.IO;
using FluentAssertions;
using Microsoft.UI.Xaml;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class AppItemViewModelTests
{
    private readonly Mock<ILocalizationService> _mockLocalization = new();
    private readonly Mock<IDispatcherService> _mockDispatcher = new();
    private readonly Mock<IThemeService> _mockThemeService = new();
    private readonly ItemDefinition _defaultDefinition;

    public AppItemViewModelTests()
    {
        // Set up dispatcher to execute actions synchronously
        _mockDispatcher
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(action => action());

        _mockLocalization
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => key);

        // Default to dark theme so existing tests are unaffected.
        _mockThemeService
            .Setup(t => t.GetEffectiveTheme())
            .Returns(ElementTheme.Dark);

        _defaultDefinition = new ItemDefinition
        {
            Id = "test-app",
            Name = "Test App",
            Description = "A test application",
            GroupName = "TestGroup",
            IsInstalled = false,
            CanBeReinstalled = true,
        };
    }

    private AppItemViewModel CreateViewModel(ItemDefinition? definition = null)
    {
        return new AppItemViewModel(
            definition ?? _defaultDefinition,
            _mockLocalization.Object,
            _mockDispatcher.Object,
            _mockThemeService.Object);
    }

    [Fact]
    public void IsInstalled_SetToNewValue_UpdatesDefinitionAndRaisesPropertyChanged()
    {
        var vm = CreateViewModel();
        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        vm.IsInstalled = true;

        vm.IsInstalled.Should().BeTrue();
        _defaultDefinition.IsInstalled.Should().BeTrue();
        changedProperties.Should().Contain(nameof(vm.InstalledStatusText));
    }

    [Fact]
    public void IsInstalled_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        _defaultDefinition.IsInstalled = false;
        var vm = CreateViewModel();
        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        vm.IsInstalled = false; // same value

        changedProperties.Should().BeEmpty();
    }

    [Fact]
    public void IsInstalled_SetTrue_DispatchesToUIThread()
    {
        var vm = CreateViewModel();

        vm.IsInstalled = true;

        _mockDispatcher.Verify(d => d.RunOnUIThread(It.IsAny<Action>()), Times.Once);
    }

    [Fact]
    public void ItemTypeDescription_CapabilityTakesPriorityOverOptionalFeature()
    {
        var def = new ItemDefinition
        {
            Id = "x",
            Name = "x",
            Description = "x",
            CapabilityName = "Cap",
            OptionalFeatureName = "Feat",
            AppxPackageName = new[] { "Pkg" },
        };
        var vm = CreateViewModel(def);

        vm.ItemTypeDescription.Should().Be("Legacy Capability");
    }

    [Fact]
    public void Dispose_UnsubscribesFromLanguageChanged()
    {
        var vm = CreateViewModel();
        vm.Dispose();

        // After dispose, raising LanguageChanged should not trigger PropertyChanged
        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalization.Raise(l => l.LanguageChanged += null, EventArgs.Empty);

        changedProperties.Should().BeEmpty();
    }

    [Fact]
    public void InstabilityWarningProperties_FireOnLanguageChanged()
    {
        var vm = CreateViewModel();
        var changedProperties = new List<string>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalization.Raise(l => l.LanguageChanged += null, EventArgs.Empty);

        changedProperties.Should().Contain(nameof(vm.InstabilityWarningLabel));
        changedProperties.Should().Contain(nameof(vm.InstabilityWarningTooltip));
    }

    [Fact]
    public void IconSource_LightTheme_WithLightSibling_DecodesFromLightPath()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "WinOptimizerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var primaryPath = Path.Combine(tmpDir, "icon.f7a3.png");
            var lightPath = Path.Combine(tmpDir, "icon.f7a3.light.png");
            File.WriteAllBytes(primaryPath, MinimalPng());
            File.WriteAllBytes(lightPath, MinimalPng());

            var def = new ItemDefinition { Id = "x", Name = "X", Description = "X", IconPath = primaryPath };
            _mockThemeService.Setup(t => t.GetEffectiveTheme()).Returns(ElementTheme.Light);

            var vm = CreateViewModel(def);
            var bmp = vm.IconSource;

            bmp.Should().NotBeNull();
            bmp!.UriSource.LocalPath.Should().Be(lightPath);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void IconSource_LightTheme_NoLightSibling_FallsBackToPrimary()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "WinOptimizerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var primaryPath = Path.Combine(tmpDir, "icon.b8e1.png");
            File.WriteAllBytes(primaryPath, MinimalPng());

            var def = new ItemDefinition { Id = "x", Name = "X", Description = "X", IconPath = primaryPath };
            _mockThemeService.Setup(t => t.GetEffectiveTheme()).Returns(ElementTheme.Light);

            var vm = CreateViewModel(def);
            vm.IconSource!.UriSource.LocalPath.Should().Be(primaryPath);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void IconSource_DarkTheme_NoDarkSibling_UsesPrimary()
    {
        // Mono-light source (white): no .dark.png written; primary is the
        // correct dark-mode rendering.
        var tmpDir = Path.Combine(Path.GetTempPath(), "WinOptimizerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var primaryPath = Path.Combine(tmpDir, "icon.f7a3.png");
            var lightPath = Path.Combine(tmpDir, "icon.f7a3.light.png");
            File.WriteAllBytes(primaryPath, MinimalPng());
            File.WriteAllBytes(lightPath, MinimalPng());

            var def = new ItemDefinition { Id = "x", Name = "X", Description = "X", IconPath = primaryPath };
            _mockThemeService.Setup(t => t.GetEffectiveTheme()).Returns(ElementTheme.Dark);

            var vm = CreateViewModel(def);
            vm.IconSource!.UriSource.LocalPath.Should().Be(primaryPath);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void IconSource_DarkTheme_WithDarkSibling_DecodesFromDarkPath()
    {
        // Mono-dark source (e.g. Xbox Game Bar #333): synthesizer wrote a
        // .dark.png; the VM must prefer it in dark mode.
        var tmpDir = Path.Combine(Path.GetTempPath(), "WinOptimizerTest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var primaryPath = Path.Combine(tmpDir, "icon.7c44.png");
            var lightPath = Path.Combine(tmpDir, "icon.7c44.light.png");
            var darkPath = Path.Combine(tmpDir, "icon.7c44.dark.png");
            File.WriteAllBytes(primaryPath, MinimalPng());
            File.WriteAllBytes(lightPath, MinimalPng());
            File.WriteAllBytes(darkPath, MinimalPng());

            var def = new ItemDefinition { Id = "x", Name = "X", Description = "X", IconPath = primaryPath };
            _mockThemeService.Setup(t => t.GetEffectiveTheme()).Returns(ElementTheme.Dark);

            var vm = CreateViewModel(def);
            vm.IconSource!.UriSource.LocalPath.Should().Be(darkPath);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void ThemeChanged_RaisesPropertyChangedForIconSource()
    {
        var def = new ItemDefinition { Id = "x", Name = "X", Description = "X", IconPath = "C:\\fake\\icon.png" };
        _mockThemeService.Setup(t => t.GetEffectiveTheme()).Returns(ElementTheme.Dark);

        var vm = CreateViewModel(def);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _mockThemeService.Raise(t => t.ThemeChanged += null, this, WinOptimizerTheme.LightNative);

        raised.Should().Contain(nameof(AppItemViewModel.IconSource));
    }

    [Fact]
    public void Dispose_UnsubscribesFromThemeChanged()
    {
        var def = new ItemDefinition { Id = "x", Name = "X", Description = "X", IconPath = "C:\\fake\\icon.png" };
        _mockThemeService.Setup(t => t.GetEffectiveTheme()).Returns(ElementTheme.Dark);

        var vm = CreateViewModel(def);
        vm.Dispose();

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _mockThemeService.Raise(t => t.ThemeChanged += null, this, WinOptimizerTheme.LightNative);

        raised.Should().NotContain(nameof(AppItemViewModel.IconSource));
    }

    /// <summary>
    /// PNG-ish junk bytes — sufficient for File.Exists checks and lazy
    /// BitmapImage URI assignment. The tests in this file only inspect
    /// `BitmapImage.UriSource.LocalPath`, so the decoder never runs over
    /// these bytes and their malformed CRC/payload doesn't matter.
    /// </summary>
    private static byte[] MinimalPng() => new byte[]
    {
        0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A,
        0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
        0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,
        0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
        0x89,0x00,0x00,0x00,0x0D,0x49,0x44,0x41,
        0x54,0x78,0x9C,0x62,0x00,0x01,0x00,0x00,
        0x05,0x00,0x01,0x0D,0x0A,0x2D,0xB4,0x00,
        0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,
        0x42,0x60,0x82
    };
}
