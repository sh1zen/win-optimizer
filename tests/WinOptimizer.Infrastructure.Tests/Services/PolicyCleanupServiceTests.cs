using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class PolicyCleanupServiceTests
{
    private const string PolicyPath = @"HKCU\Software\Policies\Fixture";
    private readonly Mock<ICompatibleSettingsRegistry> _catalogue = new();
    private readonly Mock<IWindowsRegistryService> _registry = new(MockBehavior.Strict);

    [Fact]
    public void CleanupPolicyValues_ClearsDistinctManagedValuesIncludingChildren()
    {
        var child = PolicyPath + @"\Child";
        SetCatalogue(Value(PolicyPath, "First"), Value(PolicyPath.ToLowerInvariant(), "FIRST"),
            Value(PolicyPath, "Second"), Value(child, "Third"), Value(PolicyPath, "Unmanaged", isPolicy: false));
        _registry.Setup(registry => registry.DeleteValue(PolicyPath, "First")).Returns(true);
        _registry.Setup(registry => registry.DeleteValue(PolicyPath, "Second")).Returns(true);
        _registry.Setup(registry => registry.DeleteValue(child, "Third")).Returns(true);

        Assert.Equal(3, CreateService().CleanupPolicyValues());
        _registry.Verify(registry => registry.DeleteValue(PolicyPath, "First"), Times.Once);
        _registry.Verify(registry => registry.DeleteValue(PolicyPath, "Second"), Times.Once);
        _registry.Verify(registry => registry.DeleteValue(child, "Third"), Times.Once);
        _registry.VerifyNoOtherCalls();
    }

    [Fact]
    public void CleanupPolicyValues_ReportsRejectionAndStillAttemptsOtherValues()
    {
        SetCatalogue(Value(PolicyPath, "Rejected"), Value(PolicyPath, "Cleared"));
        _registry.Setup(registry => registry.DeleteValue(PolicyPath, "Rejected")).Returns(false);
        _registry.Setup(registry => registry.DeleteValue(PolicyPath, "Cleared")).Returns(true);

        var error = Assert.Throws<IOException>(() => CreateService().CleanupPolicyValues());

        Assert.Contains(@"Fixture\Rejected", error.Message);
        _registry.Verify(registry => registry.DeleteValue(PolicyPath, "Cleared"), Times.Once);
    }

    private PolicyCleanupService CreateService() => new(_catalogue.Object, _registry.Object, Mock.Of<ILogService>());

    private void SetCatalogue(params RegistrySetting[] values) =>
        _catalogue.Setup(catalogue => catalogue.GetAllBypassedSettings()).Returns(
            new Dictionary<string, IEnumerable<SettingDefinition>>
            {
                ["Fixture"] = [new() { Id = "fixture", Name = "Fixture", Description = "Fixture", RegistrySettings = values }]
            });

    private static RegistrySetting Value(string path, string name, bool isPolicy = true) =>
        new() { KeyPath = path, ValueName = name, ValueType = RegistryValueKind.DWord, IsGroupPolicy = isPolicy, RecommendedValue = 1, DefaultValue = null };
}
