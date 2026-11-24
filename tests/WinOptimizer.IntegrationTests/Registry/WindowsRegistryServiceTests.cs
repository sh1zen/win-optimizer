using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Tests.Fixtures;
using Xunit;

namespace WinOptimizer.IntegrationTests.Registry;

[Trait("Category", "Integration")]
public class WindowsRegistryServiceTests : IDisposable
{
    private readonly PrivateRegistryHive _hive = new();
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly WindowsRegistryService _service;

    public WindowsRegistryServiceTests()
    {
        _service = new WindowsRegistryService(Mock.Of<ILogService>(), _user.Object, _hive.GetRoot);
    }

    private static string TestPath(string subKey) => $@"HKCU\Software\Fixture\{subKey}";

    [Fact]
    public void WindowsDefaultsClearsManagedPolicyButPreservesNeighbouringValuesAndSubkeys()
    {
        var path = TestPath("Policies");
        var child = path + @"\OtherVendor";
        Assert.True(_service.SetValue(path, "Managed", 1, RegistryValueKind.DWord));
        Assert.True(_service.SetValue(path, "Custom", 2, RegistryValueKind.DWord));
        Assert.True(_service.SetValue(child, "Custom", 3, RegistryValueKind.DWord));
        var catalogue = new Mock<ICompatibleSettingsRegistry>();
        catalogue.Setup(service => service.GetAllBypassedSettings()).Returns(
            new Dictionary<string, IEnumerable<SettingDefinition>>
            {
                ["Fixture"] =
                [
                    new()
                    {
                        Id = "fixture", Name = "Fixture", Description = "Fixture",
                        RegistrySettings =
                        [
                            new() { KeyPath = path, ValueName = "Managed", IsGroupPolicy = true, ValueType = RegistryValueKind.DWord, RecommendedValue = 1, DefaultValue = null },
                            new() { KeyPath = path + @"\Absent", ValueName = "Managed", IsGroupPolicy = true, ValueType = RegistryValueKind.DWord, RecommendedValue = 1, DefaultValue = null }
                        ]
                    }
                ]
            });

        var cleanup = new PolicyCleanupService(catalogue.Object, _service, Mock.Of<ILogService>());
        cleanup.CleanupPolicyValues();

        Assert.Null(_service.GetValue(path, "Managed"));
        Assert.Equal(2, _service.GetValue(path, "Custom"));
        Assert.Equal(3, _service.GetValue(child, "Custom"));
        Assert.False(_service.KeyExists(path + @"\Absent"));
    }

    [Fact]
    public void SetValue_DWord_ReadBackMatches()
    {
        // Arrange
        var path = TestPath("DWordTest");

        // Act
        var result = _service.SetValue(path, "TestDWord", 42, RegistryValueKind.DWord);
        var readBack = _service.GetValue(path, "TestDWord");

        // Assert
        result.Should().BeTrue();
        readBack.Should().Be(42);
    }

    [Fact]
    public void SetValue_String_ReadBackMatches()
    {
        // Arrange
        var path = TestPath("StringTest");
        var testValue = "Hello, win-optimizer Integration Tests!";

        // Act
        var result = _service.SetValue(path, "TestString", testValue, RegistryValueKind.String);
        var readBack = _service.GetValue(path, "TestString");

        // Assert
        result.Should().BeTrue();
        readBack.Should().Be(testValue);
    }

    [Fact]
    public void SetValue_Binary_ReadBackMatches()
    {
        // Arrange
        var path = TestPath("BinaryTest");
        var testValue = new byte[] { 0x01, 0x02, 0x03, 0xFF };

        // Act
        var result = _service.SetValue(path, "TestBinary", testValue, RegistryValueKind.Binary);
        var readBack = _service.GetValue(path, "TestBinary");

        // Assert
        result.Should().BeTrue();
        readBack.Should().BeOfType<byte[]>();
        ((byte[])readBack!).Should().BeEquivalentTo(testValue);
    }

    [Fact]
    public void DeleteValue_RemovesValue()
    {
        // Arrange
        var path = TestPath("DeleteValueTest");
        _service.SetValue(path, "ToDelete", "gone", RegistryValueKind.String);
        _service.ValueExists(path, "ToDelete").Should().BeTrue();
        _service.ValueExists(path, "DoesNotExist").Should().BeFalse();

        // Act
        var result = _service.DeleteValue(path, "ToDelete");

        // Assert
        result.Should().BeTrue();
        _service.KeyExists(path).Should().BeTrue();
        _service.ValueExists(path, "ToDelete").Should().BeFalse();
    }

    [Fact]
    public void DeleteKey_RemovesSubKeyTree()
    {
        // Arrange
        var parentPath = TestPath("DeleteKeyTest");
        var childPath = TestPath(@"DeleteKeyTest\Child");
        _service.SetValue(childPath, "Val", 1, RegistryValueKind.DWord);
        _service.KeyExists(parentPath).Should().BeTrue();

        // Act
        var result = _service.DeleteKey(parentPath);

        // Assert
        result.Should().BeTrue();
        _service.KeyExists(parentPath).Should().BeFalse();
        _service.KeyExists(childPath).Should().BeFalse();
    }

    [Fact]
    public void GetSubKeyNames_ReturnsCreatedKeys()
    {
        // Arrange
        var basePath = TestPath("SubKeyTest");
        _service.SetValue(TestPath(@"SubKeyTest\Alpha"), "x", 1, RegistryValueKind.DWord);
        _service.SetValue(TestPath(@"SubKeyTest\Beta"), "x", 1, RegistryValueKind.DWord);
        _service.SetValue(TestPath(@"SubKeyTest\Gamma"), "x", 1, RegistryValueKind.DWord);

        // Act
        var subKeys = _service.GetSubKeyNames(basePath);

        // Assert
        subKeys.Should().Contain("Alpha");
        subKeys.Should().Contain("Beta");
        subKeys.Should().Contain("Gamma");
        subKeys.Should().HaveCount(3);
    }

    [Fact]
    public void GetBatchValues_ReturnsAllQueried()
    {
        // Arrange
        var path = TestPath("BatchTest");
        var other = TestPath("OtherBatchKey");
        var missing = TestPath("MissingBatchKey");
        _service.SetValue(path, "A", 1, RegistryValueKind.DWord);
        _service.SetValue(path, "B", "hello", RegistryValueKind.String);
        _service.SetValue(path, "C", 99, RegistryValueKind.DWord);
        _service.SetValue(other, "A", 2, RegistryValueKind.DWord);

        var queries = new (string keyPath, string? valueName)[]
        {
            (path, "A"),
            (path, "B"),
            (other, "A"),
            (missing, null),
            (missing, "A"),
            (path, "C"),
        };

        // Act
        var results = _service.GetBatchValues(queries);

        // Assert
        results.Keys.Should().Equal(path + @"\A", path + @"\B", other + @"\A", missing + @"\__KEY_EXISTS__", missing + @"\A", path + @"\C");
        results.Values.Should().Equal(1, "hello", 2, false, null, 99);
    }

    [Fact]
    public void BatchUsesTheInteractiveUsersHiveWithoutMixingTheElevatedAccount()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        const string userPath = @"HKCU\Software\Fixture";
        const string machinePath = @"HKLM\Software\Fixture";
        string interactivePath = $@"HKU\{sid}\Software\Fixture";
        _service.SetValue(userPath, "Owner", "elevated", RegistryValueKind.String).Should().BeTrue();
        _service.SetValue(machinePath, "Owner", "machine", RegistryValueKind.String).Should().BeTrue();
        _user.SetupGet(user => user.InteractiveUserSid).Returns(sid);
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        _service.SetValue(userPath, "Owner", "interactive", RegistryValueKind.String).Should().BeTrue();

        var batch = _service.GetBatchValues([(userPath, "Owner"), (machinePath, "Owner"), (interactivePath, "Owner")]);

        batch[$@"{userPath}\Owner"].Should().Be("interactive");
        batch[$@"{interactivePath}\Owner"].Should().Be("interactive");
        batch[$@"{machinePath}\Owner"].Should().Be("machine");
        _user.SetupGet(user => user.IsOtsElevation).Returns(false);
        _service.GetValue(userPath, "Owner").Should().Be("elevated");
    }

    [Theory]
    [InlineData(@"HKCU\Software", true)]
    [InlineData(@"HKCU\Software\Policies", true)]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion", true)]
    [InlineData(@"HKU\S-1-5-21-1-2-3-1001\Software\Policies", false)]
    [InlineData(@"HKLM\Software\Microsoft", false)]
    public void DeleteKeyPreservesProtectedBranchesAcrossRegistryRoots(string path, bool alternateAccount)
    {
        _user.SetupGet(user => user.IsOtsElevation).Returns(alternateAccount);
        _user.SetupGet(user => user.InteractiveUserSid).Returns("S-1-5-21-1-2-3-1001");
        var child = path + @"\Fixture";
        _service.SetValue(child, "Marker", 42, RegistryValueKind.DWord).Should().BeTrue();
        _service.KeyExists(path).Should().BeTrue();

        _service.DeleteKey(path).Should().BeFalse();

        _service.GetValue(child, "Marker").Should().Be(42);
    }

    [Fact]
    public void DeleteKeyStillRemovesATargetedKeyFromTheInteractiveAccount()
    {
        const string path = @"HKCU\Software\Fixture\Target";
        _service.SetValue(path, "Marker", "elevated", RegistryValueKind.String).Should().BeTrue();
        _user.SetupGet(user => user.IsOtsElevation).Returns(true);
        _user.SetupGet(user => user.InteractiveUserSid).Returns("S-1-5-21-1-2-3-1001");
        _service.SetValue(path, "Marker", "interactive", RegistryValueKind.String).Should().BeTrue();

        _service.DeleteKey(path).Should().BeTrue();

        _service.KeyExists(path).Should().BeFalse();
        _user.SetupGet(user => user.IsOtsElevation).Returns(false);
        _service.GetValue(path, "Marker").Should().Be("elevated");
    }

    public void Dispose() => _hive.Dispose();
}
