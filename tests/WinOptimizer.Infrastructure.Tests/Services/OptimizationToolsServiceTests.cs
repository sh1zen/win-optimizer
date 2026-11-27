using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Optimize.Interfaces;
using WinOptimizer.Core.Features.Optimize.Models;
using WinOptimizer.Infrastructure.Features.Optimize.Services;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises the production orchestration using fake script, registry and setting-application boundaries.</summary>
public sealed class OptimizationToolsServiceTests
{
    private readonly Mock<IPowerShellRunner> _scripts = new();
    private readonly Mock<ISettingApplicationService> _settings = new();
    private readonly Mock<ICompatibleSettingsRegistry> _catalog = new();
    private readonly Mock<IWindowsRegistryService> _registry = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<IAppxDeployment> _deployment = new();
    private readonly Mock<IWindowsVersionService> _windows = new();
    private readonly Mock<ISystemSettingsDiscoveryService> _discovery = new();
    private const string InteractiveSid = "S-1-5-21-111-222-333-1001";

    public OptimizationToolsServiceTests()
    {
        var definitions = GamingAndPerformanceOptimizations.GetGamingAndPerformanceOptimizations().Settings
            .Concat(PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations().Settings).ToDictionary(item => item.Id);
        _catalog.Setup(catalog => catalog.GetById(It.IsAny<string>())).Returns((string id) => definitions.GetValueOrDefault(id));
        _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        _windows.Setup(windows => windows.IsWindows11()).Returns(true);
        _settings.Setup(settings => settings.ApplySettingAsync(It.IsAny<ApplySettingRequest>())).ReturnsAsync(OperationResult.Succeeded());
        _deployment.SetupGet(deployment => deployment.InteractiveAccountSid).Returns(InteractiveSid);
        _deployment.Setup(deployment => deployment.GetPackagesAsync(InteractiveSid, default)).ReturnsAsync([]);
    }

    private OptimizationToolsService Create() => new(_scripts.Object, _settings.Object, _catalog.Object,
        _registry.Object, _mode.Object, _deployment.Object, _windows.Object, _discovery.Object,
        new NetworkTuningOriginalStore(Mock.Of<IFileSystemService>()));

    [Fact]
    public async Task ReadsRestrictionsFromExistingOwnersAndIgnoresAbsentServices()
    {
        _registry.Setup(registry => registry.KeyExists(It.Is<string>(path => path.EndsWith("\\Fax")))).Returns(true);
        _discovery.Setup(discovery => discovery.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                ["gaming-fax-service"] = new() { Success = true, CurrentValue = 0 },
                ["privacy-advertising-id"] = new() { Success = true, IsEnabled = false },
                ["privacy-tailored-experiences"] = new() { Success = true, IsEnabled = true }
            });
        var service = Create();
        Assert.True(await service.ReadFeatureAsync(WindowsFeatureOptimization.OptionalServices));
        Assert.False(await service.ReadFeatureAsync(WindowsFeatureOptimization.Advertising));
        _settings.VerifyNoOtherCalls(); _scripts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AbsentTargetsAreUnavailableAndFailedReadsRemainErrors()
    {
        var service = Create();
        Assert.Null(await service.ReadFeatureAsync(WindowsFeatureOptimization.OptionalServices));
        _discovery.VerifyNoOtherCalls();
        _discovery.Setup(discovery => discovery.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult>
            {
                ["privacy-allow-cortana"] = new() { ErrorMessage = "Access denied" }
            });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadFeatureAsync(WindowsFeatureOptimization.Cortana));
        Assert.Contains("Access denied", error.Message);
    }

    [Theory]
    [InlineData(WindowsFeatureOptimization.OptionalServices)]
    [InlineData(WindowsFeatureOptimization.Telemetry)]
    [InlineData(WindowsFeatureOptimization.Cortana)]
    [InlineData(WindowsFeatureOptimization.Copilot)]
    [InlineData(WindowsFeatureOptimization.ActivityHistory)]
    [InlineData(WindowsFeatureOptimization.Advertising)]
    public async Task OffRestoresEnabledPoliciesAndDeclaredServiceDefaultsWithoutRemovingApps(WindowsFeatureOptimization feature)
    {
        _registry.Setup(registry => registry.KeyExists(It.IsAny<string>())).Returns(true);
        var applied = new List<ApplySettingRequest>();
        _settings.Setup(settings => settings.ApplySettingAsync(It.IsAny<ApplySettingRequest>()))
            .Callback<ApplySettingRequest>(applied.Add).ReturnsAsync(OperationResult.Succeeded());
        await Create().SetFeatureAsync(feature, false);
        Assert.Equal(OptimizationToolsService.Plan(feature).Count, applied.Count);
        foreach (var request in applied)
        {
            Assert.True(request.Enable);
            var definition = _catalog.Object.GetById(request.SettingId)!;
            if (definition.InputType == InputType.Selection)
                Assert.True(definition.ComboBox!.Options[(int)request.Value!].IsDefault);
            else Assert.Null(request.Value);
        }
        _scripts.VerifyNoOtherCalls();
    }

    [Fact]
    public void ConstructionDoesNotQueryOrChangeWindows()
    {
        _ = Create();
        _scripts.VerifyNoOtherCalls(); _settings.VerifyNoOtherCalls(); _registry.VerifyNoOtherCalls();
        _mode.VerifyNoOtherCalls(); _deployment.VerifyNoOtherCalls(); _windows.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task FeatureWritesAreBlockedOutsideNormalMode(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        var service = Create();
        foreach (var feature in Enum.GetValues<WindowsFeatureOptimization>())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyFeatureAsync(feature));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetFeatureAsync(feature, false));
        }
        _settings.VerifyNoOtherCalls();
        _scripts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisablesOnlyInstalledOptionalServicesUsingTheirExistingSelectionControls()
    {
        _registry.Setup(registry => registry.KeyExists(It.Is<string>(path => path.EndsWith("\\Fax")))).Returns(true);
        await Create().ApplyFeatureAsync(WindowsFeatureOptimization.OptionalServices);
        _settings.Verify(settings => settings.ApplySettingAsync(It.Is<ApplySettingRequest>(request =>
            request.SettingId == "gaming-fax-service" && Equals(request.Value, 0) && !request.Enable)), Times.Once);
        _settings.Verify(settings => settings.ApplySettingAsync(It.IsAny<ApplySettingRequest>()), Times.Once);
        _scripts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AbsentOptionalServicesCannotCreatePhantomRegistrations()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ApplyFeatureAsync(WindowsFeatureOptimization.OptionalServices));
        _settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreflightsEveryTargetAndStopsWhenAnExistingSettingReportsFailure()
    {
        _catalog.Setup(catalog => catalog.GetById("gaming-telemetry-service")).Returns((SettingDefinition?)null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ApplyFeatureAsync(WindowsFeatureOptimization.Telemetry));
        _settings.VerifyNoOtherCalls();
        _settings.Setup(settings => settings.ApplySettingAsync(It.Is<ApplySettingRequest>(request => request.SettingId == "privacy-advertising-id")))
            .ReturnsAsync(OperationResult.Failed("Access denied"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ApplyFeatureAsync(WindowsFeatureOptimization.Advertising));
        Assert.Contains("Access denied", error.Message);
        _settings.Verify(settings => settings.ApplySettingAsync(It.IsAny<ApplySettingRequest>()), Times.Once);
    }

    [Fact]
    public async Task CopilotTargetsTheInteractiveAccountAndReportsAppRemovalFailuresAfterPolicies()
    {
        const string microsoftPackage = "Microsoft.Copilot_1.0.0.0_x64__8wekyb3d8bbwe";
        var packages = new AppxPackageIdentity[] { new("Microsoft.Copilot", microsoftPackage),
            new("Microsoft.Copilot", "Microsoft.Copilot_1.0.0.0_x64__otherpublisher") };
        _deployment.SetupSequence(deployment => deployment.GetPackagesAsync(InteractiveSid, default))
            .ReturnsAsync(packages).ReturnsAsync([packages[1]]).ReturnsAsync(packages);
        var service = Create();
        await service.ApplyFeatureAsync(WindowsFeatureOptimization.Copilot);
        _settings.Verify(settings => settings.ApplySettingAsync(It.IsAny<ApplySettingRequest>()), Times.Exactly(5));
        _deployment.Verify(deployment => deployment.RemovePackageAsync(microsoftPackage,
            InteractiveSid, It.IsAny<Action<int>>(), default), Times.Once);
        _deployment.Verify(deployment => deployment.RemovePackageAsync(packages[1].FullName,
            InteractiveSid, It.IsAny<Action<int>>(), default), Times.Never);
        _deployment.Setup(deployment => deployment.RemovePackageAsync(microsoftPackage,
            InteractiveSid, It.IsAny<Action<int>>(), default)).ThrowsAsync(new IOException("Package is in use"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyFeatureAsync(WindowsFeatureOptimization.Copilot));
        Assert.Contains("policies were applied", error.Message); Assert.Contains("Package is in use", error.Message);
    }

    [Fact]
    public async Task RejectsUnsupportedCopilotAndInvalidUserIdentityBeforeChangingPolicies()
    {
        _windows.Setup(windows => windows.IsWindows11()).Returns(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ApplyFeatureAsync(WindowsFeatureOptimization.Copilot));
        _windows.Setup(windows => windows.IsWindows11()).Returns(true);
        _deployment.SetupGet(deployment => deployment.InteractiveAccountSid).Throws(new IOException("Account unavailable"));
        await Assert.ThrowsAsync<IOException>(() => Create().ApplyFeatureAsync(WindowsFeatureOptimization.Copilot));
        _settings.VerifyNoOtherCalls(); _scripts.VerifyNoOtherCalls();
    }

    [Fact]
    public void PlansReferenceRealOwnersAndTheDisabledServiceOption()
    {
        var definitions = GamingAndPerformanceOptimizations.GetGamingAndPerformanceOptimizations().Settings
            .Concat(PrivacyAndSecurityOptimizations.GetPrivacyAndSecurityOptimizations().Settings).ToDictionary(item => item.Id);
        foreach (var feature in Enum.GetValues<WindowsFeatureOptimization>())
            foreach (var request in OptimizationToolsService.Plan(feature))
            {
                var definition = definitions[request.SettingId];
                if (definition.InputType == InputType.Selection)
                    Assert.Equal(4, definition.ComboBox!.Options[(int)request.Value!].ValueMappings!["Start"]);
                else Assert.False(request.Enable);
            }
    }
}
