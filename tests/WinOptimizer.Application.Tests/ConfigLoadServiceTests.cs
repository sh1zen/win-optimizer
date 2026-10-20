using WinOptimizer.Application.Configuration;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using Xunit;

namespace WinOptimizer.Application.Tests;

public class ConfigLoadServiceTests
{
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IWindowsVersionService> _mockWindowsVersionService = new();
    private readonly Mock<ICompatibleSettingsRegistry> _mockCompatibleSettingsRegistry = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();
    private readonly Mock<IFileSystemService> _mockFileSystemService = new();
    private readonly Mock<IConfigurationFilePicker> _mockFilePicker = new();
    private readonly Mock<IConfigImportState> _mockConfigImportState = new();

    private ConfigLoadService CreateService()
    {
        return new ConfigLoadService(
            _mockLogService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockWindowsVersionService.Object,
            _mockCompatibleSettingsRegistry.Object,
            _mockInteractiveUserService.Object,
            _mockFileSystemService.Object,
            _mockFilePicker.Object,
            _mockConfigImportState.Object);
    }

    // -------------------------------------------------------
    // DetectIncompatibleSettings
    // -------------------------------------------------------

    [Fact]
    public void CompatibilityCheckingAndFiltering_PreserveRangePrecedenceOrderAndUnknownItems()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22621);
        var supported = new SettingDefinition
        {
            Id = "range", Name = "Range", Description = "Range",
            SupportedBuildRanges = [(22621, 22631)], MinimumBuildNumber = 30000
        };
        var rejected = supported with { Id = "other", Name = "Other", SupportedBuildRanges = [(26100, 26120)] };
        _mockCompatibleSettingsRegistry.Setup(r => r.GetBypassedSettings("feature"))
            .Returns([supported, rejected]);
        var items = new List<ConfigurationItem>
        {
            new() { Id = "unknown" }, new() { Id = "other" }, new() { Id = "range" }, new() { Id = "range" }
        };
        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                IsIncluded = true,
                Features = new Dictionary<string, ConfigSection> { ["feature"] = new ConfigSection { IsIncluded = true, Items = items } }
            }
        };
        var service = CreateService();

        service.DetectIncompatibleSettings(config).Should().Equal("Other (feature)");
        var filtered = service.FilterConfigForCurrentSystem(config);
        filtered.Optimize.Features["feature"].Items.Should().Equal(items[0], items[2], items[3]);
        filtered.Optimize.IsIncluded.Should().BeTrue();
        filtered.Optimize.Features["feature"].IsIncluded.Should().BeTrue();
        config.Optimize.Features["feature"].Items.Should().HaveCount(4);
    }

    [Fact]
    public void DetectIncompatibleSettings_WithEmptyConfig_ReturnsEmptyList()
    {
        var service = CreateService();
        var config = new UnifiedConfigurationFile();

        var result = service.DetectIncompatibleSettings(config);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DetectIncompatibleSettings_WithWindows10OnlySetting_OnWindows11_ReturnsIncompatible()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22621);

        var settingDef = new SettingDefinition
        {
            Id = "win10-setting",
            Name = "Win10 Setting",
            Description = "Only for Win10",
            IsWindows10Only = true
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { settingDef });

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "win10-setting", Name = "Win10 Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().ContainSingle()
            .Which.Should().Be("Win10 Setting (TestFeature)");
    }

    [Fact]
    public void DetectIncompatibleSettings_WithWindows11OnlySetting_OnWindows10_ReturnsIncompatible()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(false);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(19045);

        var settingDef = new SettingDefinition
        {
            Id = "win11-setting",
            Name = "Win11 Setting",
            Description = "Only for Win11",
            IsWindows11Only = true
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { settingDef });

        var config = new UnifiedConfigurationFile
        {
            Customize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "win11-setting", Name = "Win11 Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().ContainSingle()
            .Which.Should().Be("Win11 Setting (TestFeature)");
    }

    [Fact]
    public void DetectIncompatibleSettings_WithMinimumBuildNumber_BelowThreshold_ReturnsIncompatible()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22000);

        var settingDef = new SettingDefinition
        {
            Id = "new-build-setting",
            Name = "New Build Setting",
            Description = "Requires newer build",
            MinimumBuildNumber = 22621
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { settingDef });

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "new-build-setting", Name = "New Build Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().ContainSingle();
    }

    [Fact]
    public void DetectIncompatibleSettings_WithMaximumBuildNumber_AboveThreshold_ReturnsIncompatible()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(30000);

        var settingDef = new SettingDefinition
        {
            Id = "old-build-setting",
            Name = "Old Build Setting",
            Description = "For older builds only",
            MaximumBuildNumber = 22621
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { settingDef });

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "old-build-setting", Name = "Old Build Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().ContainSingle();
    }

    [Fact]
    public void DetectIncompatibleSettings_WithSupportedBuildRanges_OutOfRange_ReturnsIncompatible()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(25000);

        var settingDef = new SettingDefinition
        {
            Id = "range-setting",
            Name = "Range Setting",
            Description = "Has build ranges",
            SupportedBuildRanges = new List<(int MinBuild, int MaxBuild)>
            {
                (22000, 22621),
                (26000, 30000)
            }
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { settingDef });

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "range-setting", Name = "Range Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().ContainSingle();
    }

    [Fact]
    public void DetectIncompatibleSettings_WithSupportedBuildRanges_InRange_ReturnsEmpty()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22500);

        var settingDef = new SettingDefinition
        {
            Id = "range-setting",
            Name = "Range Setting",
            Description = "Has build ranges",
            SupportedBuildRanges = new List<(int MinBuild, int MaxBuild)>
            {
                (22000, 22621)
            }
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { settingDef });

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "range-setting", Name = "Range Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DetectIncompatibleSettings_WithCompatibleSetting_ReturnsEmpty()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22621);

        var settingDef = new SettingDefinition
        {
            Id = "compatible-setting",
            Name = "Compatible Setting",
            Description = "Works everywhere"
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { settingDef });

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "compatible-setting", Name = "Compatible Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DetectIncompatibleSettings_WithNullFeatures_SkipsSection()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22621);

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection { Features = new Dictionary<string, ConfigSection>() },
            Customize = new FeatureGroupSection { Features = new Dictionary<string, ConfigSection>() }
        };

        var service = CreateService();
        var result = service.DetectIncompatibleSettings(config);

        result.Should().BeEmpty();
    }

    // -------------------------------------------------------
    // FilterConfigForCurrentSystem
    // -------------------------------------------------------

    [Fact]
    public void FilterConfigForCurrentSystem_RemovesIncompatibleSettings()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22621);

        var compatibleDef = new SettingDefinition
        {
            Id = "compatible",
            Name = "Compatible",
            Description = "Works"
        };

        var incompatibleDef = new SettingDefinition
        {
            Id = "incompatible",
            Name = "Incompatible",
            Description = "Win10 only",
            IsWindows10Only = true
        };

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(new[] { compatibleDef, incompatibleDef });

        var config = new UnifiedConfigurationFile
        {
            Version = "2.0",
            Optimize = new FeatureGroupSection
            {
                IsIncluded = true,
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        IsIncluded = true,
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "compatible", Name = "Compatible" },
                            new ConfigurationItem { Id = "incompatible", Name = "Incompatible" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.FilterConfigForCurrentSystem(config);

        result.Version.Should().Be("2.0");
        result.Optimize.Features["TestFeature"].Items.Should().ContainSingle()
            .Which.Id.Should().Be("compatible");
    }

    [Fact]
    public void FilterConfigForCurrentSystem_KeepsSettingsNotInRegistry()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22621);

        _mockCompatibleSettingsRegistry
            .Setup(r => r.GetBypassedSettings("TestFeature"))
            .Returns(Enumerable.Empty<SettingDefinition>());

        var config = new UnifiedConfigurationFile
        {
            Optimize = new FeatureGroupSection
            {
                Features = new Dictionary<string, ConfigSection>
                {
                    ["TestFeature"] = new ConfigSection
                    {
                        Items = new List<ConfigurationItem>
                        {
                            new ConfigurationItem { Id = "unknown", Name = "Unknown Setting" }
                        }
                    }
                }
            }
        };

        var service = CreateService();
        var result = service.FilterConfigForCurrentSystem(config);

        result.Optimize.Features["TestFeature"].Items.Should().ContainSingle()
            .Which.Id.Should().Be("unknown");
    }

    [Fact]
    public void FilterConfigForCurrentSystem_PreservesWindowsAppsAndExternalApps()
    {
        _mockWindowsVersionService.Setup(w => w.IsWindows11()).Returns(true);
        _mockWindowsVersionService.Setup(w => w.GetWindowsBuildNumber()).Returns(22621);

        var config = new UnifiedConfigurationFile
        {
            WindowsApps = new ConfigSection
            {
                IsIncluded = true,
                Items = new List<ConfigurationItem>
                {
                    new ConfigurationItem { Id = "app1", Name = "App 1" }
                }
            },
            ExternalApps = new ConfigSection
            {
                IsIncluded = true,
                Items = new List<ConfigurationItem>
                {
                    new ConfigurationItem { Id = "ext-app1", Name = "External App 1" }
                }
            }
        };

        var service = CreateService();
        var result = service.FilterConfigForCurrentSystem(config);

        result.WindowsApps.Items.Should().ContainSingle();
        result.ExternalApps.Items.Should().ContainSingle();
    }

    // -------------------------------------------------------
    // LoadUserBackupConfigurationAsync
    // -------------------------------------------------------

    [Fact]
    public async Task LoadUserBackupConfigurationAsync_WhenDirectoryDoesNotExist_ShowsMessageAndReturnsNull()
    {
        _mockInteractiveUserService
            .Setup(s => s.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns(@"C:\Users\Test\AppData\Local");

        _mockFileSystemService
            .Setup(fs => fs.CombinePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(@"C:\Users\Test\AppData\Local\win-optimizer\Backup");

        _mockFileSystemService
            .Setup(fs => fs.DirectoryExists(It.IsAny<string>()))
            .Returns(false);

        var service = CreateService();
        var result = await service.LoadUserBackupConfigurationAsync();

        result.Should().BeNull();
        _mockDialogService.Verify(d => d.ShowMessage(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task LoadUserBackupConfigurationAsync_WhenNoBackupFiles_ShowsMessageAndReturnsNull()
    {
        _mockInteractiveUserService
            .Setup(s => s.GetInteractiveUserFolderPath(It.IsAny<Environment.SpecialFolder>()))
            .Returns(@"C:\Users\Test\AppData\Local");

        _mockFileSystemService
            .Setup(fs => fs.CombinePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(@"C:\Users\Test\AppData\Local\win-optimizer\Backup");

        _mockFileSystemService
            .Setup(fs => fs.DirectoryExists(It.IsAny<string>()))
            .Returns(true);

        _mockFileSystemService
            .Setup(fs => fs.GetFiles(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Array.Empty<string>());

        var service = CreateService();
        var result = await service.LoadUserBackupConfigurationAsync();

        result.Should().BeNull();
        _mockDialogService.Verify(d => d.ShowMessage(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // -------------------------------------------------------
    // LoadAndValidateConfigurationFromFileAsync
    // -------------------------------------------------------

    [Fact]
    public async Task LoadAndValidateConfigurationFromFileAsync_WhenNoMainWindow_ReturnsNull()
    {
        _mockFilePicker
            .Setup(p => p.IsAvailable)
            .Returns(false);

        var service = CreateService();
        var result = await service.LoadAndValidateConfigurationFromFileAsync();

        result.Should().BeNull();
        _mockDialogService.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task LoadRecommendedConfigurationAsync_ReadsApplicationResource()
    {
        var config = await CreateService().LoadRecommendedConfigurationAsync();

        config.Should().NotBeNull();
        config!.Version.Should().Be("2.0");
        config.Optimize.Features.Should().NotBeEmpty();
        config.Optimize.Features.Values.SelectMany(section => section.Items)
            .Single(item => item.Id == "updates-notification-level").IsSelected.Should().BeTrue();
        _mockDialogService.Verify(d => d.ShowMessage(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadWindowsDefaultsConfigurationAsync_ReadsMatchingApplicationResource(bool windows11)
    {
        _mockWindowsVersionService.Setup(v => v.IsWindows11()).Returns(windows11);
        var expectedName = windows11 ? "Windows11_25H2" : "Windows10_22H2";
        using var stream = typeof(ConfigLoadService).Assembly.GetManifestResourceStream(
            $"WinOptimizer.Application.Configuration.Resources.WinOptimizer_Default_Config_{expectedName}.win-optimizer");
        using var reader = new StreamReader(stream!);
        var expected = System.Text.Json.JsonSerializer.Deserialize<UnifiedConfigurationFile>(
            await reader.ReadToEndAsync(), WinOptimizer.Core.Features.Common.Constants.ConfigFileConstants.JsonOptions);

        var config = await CreateService().LoadWindowsDefaultsConfigurationAsync();

        System.Text.Json.JsonSerializer.Serialize(config, WinOptimizer.Core.Features.Common.Constants.ConfigFileConstants.JsonOptions)
            .Should().Be(System.Text.Json.JsonSerializer.Serialize(expected, WinOptimizer.Core.Features.Common.Constants.ConfigFileConstants.JsonOptions));
        config.Should().NotBeNull();
        config!.Optimize.Features.Values.SelectMany(section => section.Items)
            .Single(item => item.Id == "updates-notification-level").IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAndValidateConfigurationFromFileAsync_UsesPickerAndPreservesFileContract()
    {
        const string path = "example.win-optimizer";
        _mockFilePicker.Setup(p => p.IsAvailable).Returns(true);
        _mockFilePicker.Setup(p => p.OpenFile("Open Configuration", WinOptimizer.Core.Features.Common.Constants.ConfigFileConstants.FileFilter, WinOptimizer.Core.Features.Common.Constants.ConfigFileConstants.FilePattern, null))
            .Returns(path);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(path))
            .ReturnsAsync("""
                {"Version":"2.0","WindowsApps":{"Items":[
                    {"Id":"app","AppxPackageName":["Microsoft.App","Microsoft.App.Tools"]}
                ]},"Customize":{"Features":{"Taskbar":{"Items":[
                    {"Id":"taskbar-transparent","InputType":1,"SelectedIndex":2}
                ]}}}}
                """);

        var config = await CreateService().LoadAndValidateConfigurationFromFileAsync();

        config.Should().NotBeNull();
        config!.WindowsApps.Items.Single().AppxPackageName.Should().Equal("Microsoft.App", "Microsoft.App.Tools");
        var selection = config.Customize.Features["Taskbar"].Items.Single();
        selection.InputType.Should().Be(InputType.Selection);
        selection.SelectedIndex.Should().Be(2);
        _mockConfigImportState.VerifySet(s => s.SourceName = path);
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"Version\":\"1.0\"}")]
    [InlineData(true, "{\"Version\":\"1.0\"}")]
    [InlineData(false, "{\"Version\":null}")]
    [InlineData(true, "{\"Version\":null}")]
    public async Task ConfigurationSourcesRequireTheCurrentExplicitVersion(bool backup, string json)
    {
        const string path = "UserBackup_example.win-optimizer";
        _mockFilePicker.Setup(p => p.IsAvailable).Returns(true);
        _mockFilePicker.Setup(p => p.OpenFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null)).Returns(path);
        _mockFileSystemService.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(true);
        _mockFileSystemService.Setup(f => f.GetFiles(It.IsAny<string>(), It.IsAny<string>(), SearchOption.TopDirectoryOnly)).Returns([path]);
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(path)).ReturnsAsync(json);

        var service = CreateService();
        var result = backup ? await service.LoadUserBackupConfigurationAsync() : await service.LoadAndValidateConfigurationFromFileAsync();

        result.Should().BeNull();
        _mockFileSystemService.Verify(f => f.ReadAllTextAsync(path), Times.Once);
    }

    [Theory]
    [InlineData("\"Microsoft.App\"")]
    [InlineData("42")]
    [InlineData("[\"Microsoft.App\",42]")]
    public async Task LoadAndValidateConfigurationFromFileAsync_InvalidPackageNames_ReportsLoadError(string packageNames)
    {
        const string path = "invalid.win-optimizer";
        _mockFilePicker.Setup(p => p.IsAvailable).Returns(true);
        _mockFilePicker.Setup(p => p.OpenFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null))
            .Returns(path);
        var json = "{\"Version\":\"2.0\",\"WindowsApps\":{\"Items\":[{\"Id\":\"app\",\"AppxPackageName\":" + packageNames + "}]}}";
        _mockFileSystemService.Setup(f => f.ReadAllTextAsync(path)).ReturnsAsync(json);

        var result = await CreateService().LoadAndValidateConfigurationFromFileAsync();

        result.Should().BeNull();
        _mockLogService.Verify(l => l.Log(LogLevel.Error,
            It.Is<string>(message => message.Contains("Error loading configuration file")), null), Times.Once);
        _mockDialogService.Verify(d => d.ShowMessage(
            It.Is<string>(message => message.StartsWith("Error loading configuration:")), "Error"), Times.Once);
    }
}
