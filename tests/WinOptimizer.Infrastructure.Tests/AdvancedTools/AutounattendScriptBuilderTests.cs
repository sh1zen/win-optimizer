using System.Text;
using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class AutounattendScriptBuilderTests
{
    private readonly Mock<IPowerSettingsQueryService> _powerSettingsQueryService = new();
    private readonly Mock<IHardwareDetectionService> _hardwareDetectionService = new();
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IComboBoxResolver> _comboBoxResolver = new();
    private readonly Mock<IPowerShellRunner> _powerShellRunner = new();
    private readonly AutounattendScriptBuilder _sut;

    public AutounattendScriptBuilderTests()
    {
        // Default setup for power settings query (always needed since BuildOptimizationsScriptAsync calls it)
        _powerSettingsQueryService.Setup(s => s.GetActivePowerPlanAsync())
            .ReturnsAsync(new PowerPlan { Guid = "balanced-guid", Name = "Balanced" });
        _powerSettingsQueryService.Setup(s => s.GetAllPowerSettingsACDCAsync(It.IsAny<string>()))
            .ReturnsAsync(new Dictionary<string, (int? acValue, int? dcValue)>());
        _hardwareDetectionService.Setup(s => s.HasBatteryAsync()).ReturnsAsync(false);

        // Syntax validation succeeds by default
        _powerShellRunner.Setup(s => s.ValidateScriptSyntaxAsync(It.IsAny<string>(), default))
            .Returns(Task.CompletedTask);

        _sut = new AutounattendScriptBuilder(
            _powerSettingsQueryService.Object,
            _hardwareDetectionService.Object,
            _logService.Object,
            _comboBoxResolver.Object,
            _powerShellRunner.Object);
    }

    [Fact]
    public async Task BuildOptimizationsScriptAsync_EmptyConfig_ContainsRequiredStructure()
    {
        var config = new UnifiedConfigurationFile();
        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>();

        var result = await _sut.BuildOptimizationsScriptAsync(config, allSettings);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain(".SYNOPSIS");
        result.Should().Contain("param(");
        result.Should().Contain("function Write-Log");
        result.Should().Contain("$LogPath");
        result.Should().Contain("function Set-RegistryValue");
        result.Should().Contain("function Start-ProcessAsUser");
        result.Should().Contain("if (-not $UserCustomizations)");
        result.Should().Contain("if ($UserCustomizations)");
        result.Should().Contain("Script Completed");
        result.Should().Contain("SYSTEM WIDE");
        result.Should().Contain("USER SPECIFIC");
        result.Should().Contain("# Start here");
        result.Should().Contain("# End here");
        result.Should().Contain("$scriptsDir");
        result.Should().Contain("Install WinOptimizer.lnk");
        result.Should().Contain("START MENU LAYOUT");
        result.Should().Contain("WinOptimizerUserCustomizations");
        result.Should().Contain("$runningAsSystem");
        result.Should().Contain("S-1-5-18");
        result.Should().Contain("UserCustomizationsApplied");
        _powerShellRunner.Verify(r => r.ValidateScriptSyntaxAsync(
            It.IsAny<string>(), default), Times.Once);
    }

    [Fact]
    public async Task BuildOptimizationsScriptAsync_SyntaxValidationFails_Throws()
    {
        _powerShellRunner.Setup(s => s.ValidateScriptSyntaxAsync(It.IsAny<string>(), default))
            .ThrowsAsync(new InvalidOperationException("Syntax error at line 42"));

        var config = new UnifiedConfigurationFile();
        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>();

        var act = () => _sut.BuildOptimizationsScriptAsync(config, allSettings);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Syntax error*");
    }

    [Fact]
    public async Task BuildOptimizationsScriptAsync_WithWindowsApps_EmitsAppRemoval()
    {
        var config = new UnifiedConfigurationFile
        {
            WindowsApps = new ConfigSection
            {
                Items = new List<ConfigurationItem>
                {
                    new ConfigurationItem
                    {
                        Id = "windows-app-cortana",
                        AppxPackageName = new[] { "Microsoft.549981C3F5F10" }
                    }
                }
            }
        };
        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>();

        var result = await _sut.BuildOptimizationsScriptAsync(config, allSettings);

        result.Should().Contain("WINDOWS APPS REMOVAL");
        result.Should().Contain("BloatRemoval");
    }

    [Fact]
    public async Task BuildOptimizationsScriptAsync_WithOptimizeFeatures_EmitsHklmRegistryEntries()
    {
        var settingDef = new SettingDefinition
        {
            Id = "test-optimize-setting",
            Name = "Optimize Setting",
            Description = "Test optimize",
            RegistrySettings = new[]
            {
                new RegistrySetting
                {
                    KeyPath = "HKEY_LOCAL_MACHINE\\SOFTWARE\\Test",
                    ValueName = "OptVal",
                    ValueType = RegistryValueKind.DWord,
                    EnabledValue = [1],
                    DisabledValue = [0],
                    RecommendedValue = null,
                    DefaultValue = null
                }
            }
        };

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    {
                        "TestOptimize", new ConfigSection
                        {
                            Items = new List<ConfigurationItem>
                            {
                                new ConfigurationItem
                                {
                                    Id = "test-optimize-setting",
                                    IsSelected = true,
                                    InputType = InputType.Toggle
                                }
                            }
                        }
                    }
                }
            }
        };

        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            { "TestOptimize", new[] { settingDef } }
        };

        var result = await _sut.BuildOptimizationsScriptAsync(config, allSettings);

        result.Should().Contain("Set-RegistryValue");
        result.Should().Contain("OptVal");
    }

    [Fact]
    public async Task BuildOptimizationsScriptAsync_WithCustomizeFeatures_EmitsHkcuInUserBlock()
    {
        var settingDef = new SettingDefinition
        {
            Id = "test-customize-setting",
            Name = "Customize Setting",
            Description = "Test customize",
            RegistrySettings = new[]
            {
                new RegistrySetting
                {
                    KeyPath = "HKEY_CURRENT_USER\\Software\\Test",
                    ValueName = "CustVal",
                    ValueType = RegistryValueKind.DWord,
                    EnabledValue = [1],
                    DisabledValue = [0],
                    RecommendedValue = null,
                    DefaultValue = null
                }
            }
        };

        var config = new UnifiedConfigurationFile
        {
            Customize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    {
                        "TestCustomize", new ConfigSection
                        {
                            Items = new List<ConfigurationItem>
                            {
                                new ConfigurationItem
                                {
                                    Id = "test-customize-setting",
                                    IsSelected = true,
                                    InputType = InputType.Toggle
                                }
                            }
                        }
                    }
                }
            }
        };

        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>
        {
            { "TestCustomize", new[] { settingDef } }
        };

        var result = await _sut.BuildOptimizationsScriptAsync(config, allSettings);

        // The HKCU entries should appear after "if ($UserCustomizations)"
        var userBlockIndex = result.IndexOf("if ($UserCustomizations)");
        var custValIndex = result.IndexOf("CustVal", userBlockIndex);
        custValIndex.Should().BeGreaterThan(userBlockIndex);
    }

    [Fact]
    public async Task BuildOptimizationsScriptAsync_ValidSyntax_LogsSuccess()
    {
        var config = new UnifiedConfigurationFile();
        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>();

        await _sut.BuildOptimizationsScriptAsync(config, allSettings);

        _logService.Verify(l => l.Log(
            LogLevel.Info,
            It.Is<string>(s => s.Contains("passed PowerShell syntax validation")),
            null), Times.Once);
    }

    [Fact]
    public async Task BuildOptimizationsScriptAsync_FailedSyntax_LogsError()
    {
        _powerShellRunner.Setup(s => s.ValidateScriptSyntaxAsync(It.IsAny<string>(), default))
            .ThrowsAsync(new InvalidOperationException("Bad syntax"));

        var config = new UnifiedConfigurationFile();
        var allSettings = new Dictionary<string, IEnumerable<SettingDefinition>>();

        try { await _sut.BuildOptimizationsScriptAsync(config, allSettings); }
        catch { /* expected */ }

        _logService.Verify(l => l.Log(
            LogLevel.Error,
            It.Is<string>(s => s.Contains("failed PowerShell syntax validation")),
            null), Times.Once);
    }
}
