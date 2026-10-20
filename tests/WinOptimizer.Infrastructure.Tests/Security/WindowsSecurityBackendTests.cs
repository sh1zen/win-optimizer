using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Security;
using WinOptimizer.Infrastructure.Features.Security;
using WinOptimizer.Infrastructure.Extensions.DI;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Security;

/// <summary>Tests process contracts and existing setting delegation without touching Windows protection.</summary>
public sealed class WindowsSecurityBackendTests
{
    private readonly Mock<IProcessExecutor> _process = new();
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly Mock<ICompatibleSettingsRegistry> _catalog = new();
    private readonly Mock<ISystemSettingsDiscoveryService> _discovery = new();
    private readonly Mock<ISettingApplicationService> _settings = new();
    private readonly WindowsSecurityBackend _backend;
    private string _output = "[]";
    private string _script = "";
    private CancellationToken _nativeToken;
    public WindowsSecurityBackendTests()
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        _user.SetupGet(user => user.InteractiveUserSid).Returns("S-1-5-21-111-222-333-1001");
        _backend = new(_process.Object, _user.Object, _catalog.Object, _discovery.Object, _settings.Object);
        _process.Setup(process => process.ExecuteWithStreamingAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
            .Callback<string, string, Action<string>?, Action<string>?, CancellationToken>((_, args, _, _, token) =>
            { _script = Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ')[^1])); _nativeToken = token; })
            .ReturnsAsync(() => new ProcessExecutionResult { StandardOutput = _output });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("[{\"Id\":\"Firewall\",\"State\":\"Good\",\"Current\":\"Enabled\",\"Evidence\":\"all\",\"CanFix\":true}]")]
    [InlineData("[{\"Id\":\"Firewall\",\"State\":\"Good\"}]")]
    public async Task IncompleteOrMalformedNativeResultsAreNotSuccess(string output)
    {
        _output = output;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.ReadAsync([SecurityCheckId.Firewall], default));
    }

    [Fact]
    public async Task ReadsCarryInteractiveIdentityAndCancellationInEncodedData()
    {
        _output = "[{\"Id\":\"Firewall\",\"State\":\"Attention\",\"Current\":\"Disabled\",\"Evidence\":\"Public=False\",\"CanFix\":true}]";
        using var source = new CancellationTokenSource();
        var item = Assert.Single(await _backend.ReadAsync([SecurityCheckId.Firewall], source.Token));
        Assert.True(item.CanFix); Assert.Equal(source.Token, _nativeToken);
        var encoded = _script.Split("FromBase64String('")[1].Split("')")[0];
        using var request = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.Equal("S-1-5-21-111-222-333-1001", request.RootElement.GetProperty("Sid").GetString());
        Assert.Null(request.RootElement.GetProperty("Fix").GetString());
    }

    [Fact]
    public async Task FixWaitsForItsNativeUnitAndIncludesExpectedEvidence()
    {
        _output = "[{\"Id\":\"Teredo\",\"State\":\"Good\",\"Current\":\"Disabled\",\"Evidence\":\"Disabled\",\"CanFix\":false}]";
        using var source = new CancellationTokenSource();
        await _backend.FixAsync(new(SecurityCheckId.Teredo, SecurityCheckState.Attention, "Client", "Client", true), source.Token);
        Assert.Equal(CancellationToken.None, _nativeToken);
        Assert.True(_script.Length < 11250, "The encoded query must remain inside Windows' command-line size limit.");
    }

    [Fact]
    public async Task ExistingPrivacySettingOwnsReadApplyAndReadback()
    {
        var definition = new SettingDefinition { Id = "privacy-language-list", Name = "Language list", Description = "Language preference" };
        _catalog.Setup(catalog => catalog.GetById(definition.Id)).Returns(definition);
        _discovery.SetupSequence(discovery => discovery.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult> { [definition.Id] = new() { Success = true, IsEnabled = true } })
            .ReturnsAsync(new Dictionary<string, SettingStateResult> { [definition.Id] = new() { Success = true, IsEnabled = false } });
        _settings.Setup(settings => settings.ApplySettingAsync(It.Is<ApplySettingRequest>(request => request.SettingId == definition.Id && !request.Enable)))
            .ReturnsAsync(OperationResult.Succeeded());
        await _backend.FixAsync(new(SecurityCheckId.LanguageList, SecurityCheckState.Attention, "Enabled", "True", true), default);
        _settings.VerifyAll(); _process.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ChangedPrivacyStateBlocksMutation()
    {
        var definition = new SettingDefinition { Id = "privacy-app-launch-tracking", Name = "App tracking", Description = "Launch history preference" };
        _catalog.Setup(catalog => catalog.GetById(definition.Id)).Returns(definition);
        _discovery.Setup(discovery => discovery.GetSettingStatesAsync(It.IsAny<IEnumerable<SettingDefinition>>()))
            .ReturnsAsync(new Dictionary<string, SettingStateResult> { [definition.Id] = new() { Success = true, IsEnabled = false } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _backend.FixAsync(new(SecurityCheckId.AppTracking, SecurityCheckState.Attention, "Enabled", "True", true), default));
        _settings.VerifyNoOtherCalls();
    }

    [Fact]
    public void ProductionRegistrationsConstructSecurityWithoutWindowsWork()
    {
        var services = new ServiceCollection();
        services.AddInfrastructureServices();
        services.AddSingleton(_process.Object); services.AddSingleton(_user.Object);
        services.AddSingleton(_catalog.Object); services.AddSingleton(_discovery.Object); services.AddSingleton(_settings.Object);
        services.AddSingleton(Mock.Of<IApplicationModeService>());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<SecurityScanService>(provider.GetRequiredService<ISecurityScanService>());
        Assert.IsType<WindowsSecurityBackend>(provider.GetRequiredService<ISecurityBackend>());
        _process.VerifyNoOtherCalls(); _user.VerifyNoOtherCalls(); _catalog.VerifyNoOtherCalls(); _settings.VerifyNoOtherCalls(); _discovery.VerifyNoOtherCalls();
    }
}
