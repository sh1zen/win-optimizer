using Microsoft.Win32;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Customize.Models;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Tests.Fixtures;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsRegistryServiceTests : IDisposable
{
    private readonly Mock<ILogService> _mockLog = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUser = new();
    private readonly Lazy<PrivateRegistryHive> _hive = new();
    private readonly WindowsRegistryService _sut;

    public WindowsRegistryServiceTests()
    {
        _mockInteractiveUser.Setup(x => x.IsOtsElevation).Returns(false);
        _sut = new WindowsRegistryService(_mockLog.Object, _mockInteractiveUser.Object, hive => _hive.Value.GetRoot(hive));
    }

    public void Dispose()
    {
        if (_hive.IsValueCreated) _hive.Value.Dispose();
    }

    [Theory]
    [InlineData("HKEY_CURRENT_USER", RegistryHive.CurrentUser)]
    [InlineData("HKCU", RegistryHive.CurrentUser)]
    [InlineData("HKEY_LOCAL_MACHINE", RegistryHive.LocalMachine)]
    [InlineData("HKLM", RegistryHive.LocalMachine)]
    [InlineData("HKEY_CLASSES_ROOT", RegistryHive.ClassesRoot)]
    [InlineData("HKCR", RegistryHive.ClassesRoot)]
    [InlineData("HKEY_USERS", RegistryHive.Users)]
    [InlineData("HKU", RegistryHive.Users)]
    [InlineData("HKEY_CURRENT_CONFIG", RegistryHive.CurrentConfig)]
    [InlineData("HKCC", RegistryHive.CurrentConfig)]
    public void RegistryAliasesRouteToTheExpectedHive(string alias, RegistryHive hive)
    {
        using var key = _hive.Value.GetRoot(hive).CreateSubKey(@"Software\Fixture");
        key.SetValue("Marker", 42, RegistryValueKind.DWord);
        var path = alias + @"\Software\Fixture";

        _sut.GetValue(path, "Marker").Should().Be(42);
        _sut.GetBatchValues([(path, "Marker")])[$@"{path}\Marker"].Should().Be(42);
    }

    [Theory]
    [InlineData(@"INVALID_HIVE\Software\Test")]
    [InlineData(@"HKLM_TYPO\Software\Test")]
    public void BatchRejectsAnInvalidHive(string keyPath)
    {
        var read = () => _sut.GetBatchValues([(keyPath, "Marker")]);
        read.Should().Throw<ArgumentException>().WithMessage("*Unrecognized registry hive*");
    }

    // ── DeleteKey safeguards (BP-3) ──

    [Theory]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKCU\Software")]
    public void DeleteKey_ShallowPath_ReturnsFalseAndLogs(string keyPath)
    {
        _sut.SetValue(keyPath, "Marker", 1, RegistryValueKind.DWord).Should().BeTrue();
        var result = _sut.DeleteKey(keyPath);
        result.Should().BeFalse();
        _sut.GetValue(keyPath, "Marker").Should().Be(1);
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows")]
    [InlineData(@"HKLM\SOFTWARE\Policies")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services")]
    public void DeleteKey_ProtectedPath_ReturnsFalseAndLogs(string keyPath)
    {
        _sut.SetValue(keyPath, "Marker", 1, RegistryValueKind.DWord).Should().BeTrue();
        var result = _sut.DeleteKey(keyPath);
        result.Should().BeFalse();
        _sut.GetValue(keyPath, "Marker").Should().Be(1);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")]
    [InlineData(@"software\\microsoft\windows nt\currentversion\")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion")]
    [InlineData(@"SOFTWARE\Policies\Microsoft")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\RpcSs")]
    public void IsProtectedDeletionPath_CriticalChildBranch_ReturnsTrue(string subKeyPath)
    {
        WindowsRegistryService.IsProtectedDeletionPath(subKeyPath).Should().BeTrue();
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows NTLike\CurrentVersion")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Example")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")]
    public void IsProtectedDeletionPath_IndividualSettingOrSibling_ReturnsFalse(string subKeyPath)
    {
        WindowsRegistryService.IsProtectedDeletionPath(subKeyPath).Should().BeFalse();
    }

    /// <summary>Supported key removals and policy cleanup must remain below the protected branches.</summary>
    [Fact]
    public void IsProtectedDeletionPath_AllCatalogKeyDeletions_RemainAllowed()
    {
        var groups = new[]
        {
            ExplorerCustomizations.GetExplorerCustomizations(), StartMenuCustomizations.GetStartMenuCustomizations(),
            TaskbarCustomizations.GetTaskbarCustomizations(), WindowsThemeCustomizations.GetWindowsThemeCustomizations(),
            PowerOptimizations.GetPowerOptimizations(), GamingAndPerformanceOptimizations.GetGamingAndPerformanceOptimizations(),
            NotificationOptimizations.GetNotificationOptimizations(), PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations(),
            SoundOptimizations.GetSoundOptimizations(), UpdateOptimizations.GetUpdateOptimizations()
        };
        var paths = groups.SelectMany(group => group.Settings).SelectMany(setting => setting.RegistrySettings)
            .Where(registry => registry.ValueName == null || registry.IsGroupPolicy)
            .Select(registry => registry.KeyPath).Distinct();

        foreach (var path in paths)
        {
            var subKeyPath = path[(path.IndexOf('\\') + 1)..];
            WindowsRegistryService.IsProtectedDeletionPath(subKeyPath).Should().BeFalse($"'{path}' is a supported removal target");
        }
    }

    [Fact]
    public void DeleteKey_NonExistentDeepPath_ReturnsTrue()
    {
        // Non-existent keys return true (nothing to delete)
        var result = _sut.DeleteKey(@"HKCU\Software\win-optimizer\TestKey\SubKey");
        result.Should().BeTrue();
    }

    // ── IsRegistryValueInEnabledState ──

    private static RegistrySetting CreateTestSetting(
        object?[]? enabledValue = null,
        object?[]? disabledValue = null)
    {
        return new RegistrySetting
        {
            KeyPath = @"HKCU\Software\Test",
            ValueName = "TestValue",
            ValueType = Microsoft.Win32.RegistryValueKind.DWord,
            EnabledValue = enabledValue,
            DisabledValue = disabledValue,
            RecommendedValue = null,
            DefaultValue = null
        };
    }

    [Fact]
    public void IsRegistryValueInEnabledState_NullSetting_ReturnsFalse()
    {
        _sut.IsRegistryValueInEnabledState(null!, null, false).Should().BeFalse();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_ValueNotExists_EnabledIncludesAbsence_ReturnsTrue()
    {
        // Game Mode scenario: value doesn't exist, EnabledValue=[1, null] → enabled (absence matches)
        var setting = CreateTestSetting(enabledValue: [1, null], disabledValue: [0]);
        _sut.IsRegistryValueInEnabledState(setting, null, false).Should().BeTrue();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_ValueNotExists_EnabledValueSet_ReturnsFalse()
    {
        var setting = CreateTestSetting(enabledValue: [1], disabledValue: [0]);
        _sut.IsRegistryValueInEnabledState(setting, null, false).Should().BeFalse();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_EnabledValueNull_MatchesDisabledValue_ReturnsFalse()
    {
        var setting = CreateTestSetting(enabledValue: null, disabledValue: [0]);
        _sut.IsRegistryValueInEnabledState(setting, 0, true).Should().BeFalse();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_EnabledValueNull_DoesNotMatchDisabledValue_ReturnsTrue()
    {
        var setting = CreateTestSetting(enabledValue: null, disabledValue: [0]);
        _sut.IsRegistryValueInEnabledState(setting, 1, true).Should().BeTrue();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_EnabledValueNull_BoolFalse_ReturnsFalse()
    {
        // Pre-processed BitMask where false means "bit not set"
        var setting = CreateTestSetting();
        _sut.IsRegistryValueInEnabledState(setting, false, true).Should().BeFalse();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_EnabledValueNull_BoolTrue_ReturnsTrue()
    {
        // Pre-processed BitMask where true means "bit is set"
        var setting = CreateTestSetting();
        _sut.IsRegistryValueInEnabledState(setting, true, true).Should().BeTrue();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_MatchesEnabledValue_ReturnsTrue()
    {
        var setting = CreateTestSetting(enabledValue: [1], disabledValue: [0]);
        _sut.IsRegistryValueInEnabledState(setting, 1, true).Should().BeTrue();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_MatchesNeither_ReturnsFalse()
    {
        var setting = CreateTestSetting(enabledValue: [1], disabledValue: [0]);
        _sut.IsRegistryValueInEnabledState(setting, 99, true).Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 5L, true)]
    [InlineData(5L, 5, true)]
    [InlineData(5L, 5L, true)]
    [InlineData(5, 10L, false)]
    [InlineData(5L, 10, false)]
    [InlineData("Hello", "hello", true)]
    [InlineData("Hello", "World", false)]
    [InlineData(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }, true)]
    [InlineData(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }, false)]
    public void IsRegistryValueInEnabledState_ComparesStoredAndConfiguredValues(
        object currentValue, object enabledValue, bool expected)
    {
        var setting = CreateTestSetting(enabledValue: [enabledValue]);

        _sut.IsRegistryValueInEnabledState(setting, currentValue, valueExists: true).Should().Be(expected);
    }

    // ── CompositeStringKey handling ──

    [Fact]
    public void IsRegistryValueInEnabledState_CompositeStringKey_MatchesEnabled_ReturnsTrue()
    {
        var setting = new RegistrySetting
        {
            KeyPath = @"HKCU\Software\Test",
            ValueName = "DirectXUserGlobalSettings",
            CompositeStringKey = "SwapEffectUpgradeEnable",
            EnabledValue = ["1"],
            DisabledValue = ["0"],
            DefaultValue = "1",
            ValueType = Microsoft.Win32.RegistryValueKind.String,
            RecommendedValue = null
        };
        _sut.IsRegistryValueInEnabledState(setting, "SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;", true)
            .Should().BeTrue();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_CompositeStringKey_MatchesDisabled_ReturnsFalse()
    {
        var setting = new RegistrySetting
        {
            KeyPath = @"HKCU\Software\Test",
            ValueName = "DirectXUserGlobalSettings",
            CompositeStringKey = "SwapEffectUpgradeEnable",
            EnabledValue = ["1"],
            DisabledValue = ["0"],
            DefaultValue = "1",
            ValueType = Microsoft.Win32.RegistryValueKind.String,
            RecommendedValue = null
        };
        _sut.IsRegistryValueInEnabledState(setting, "SwapEffectUpgradeEnable=0;VRROptimizeEnable=1;", true)
            .Should().BeFalse();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_CompositeStringKey_SubKeyAbsent_UsesDefaultValue()
    {
        var setting = new RegistrySetting
        {
            KeyPath = @"HKCU\Software\Test",
            ValueName = "DirectXUserGlobalSettings",
            CompositeStringKey = "SwapEffectUpgradeEnable",
            EnabledValue = ["1"],
            DisabledValue = ["0"],
            DefaultValue = "1",
            ValueType = Microsoft.Win32.RegistryValueKind.String,
            RecommendedValue = null
        };
        // Composite string exists but doesn't contain our sub-key — DefaultValue "1" == EnabledValue "1"
        _sut.IsRegistryValueInEnabledState(setting, "VRROptimizeEnable=0;", true)
            .Should().BeTrue();
    }

    [Fact]
    public void IsRegistryValueInEnabledState_CompositeStringKey_NullValue_UsesDefaultValue()
    {
        var setting = new RegistrySetting
        {
            KeyPath = @"HKCU\Software\Test",
            ValueName = "DirectXUserGlobalSettings",
            CompositeStringKey = "SwapEffectUpgradeEnable",
            EnabledValue = ["1"],
            DisabledValue = ["0"],
            DefaultValue = "1",
            ValueType = Microsoft.Win32.RegistryValueKind.String,
            RecommendedValue = null
        };
        // Value doesn't exist (key absent) — DefaultValue "1" == EnabledValue "1"
        _sut.IsRegistryValueInEnabledState(setting, null, false)
            .Should().BeTrue();
    }
}
