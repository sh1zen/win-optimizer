using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinOptimizer.Core.Features.AdvancedTools.Experimental;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Experimental;
using WinOptimizer.Tests.Fixtures;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public sealed class ExperimentalWindowsBackendTests
{
    [Fact]
    public void ProfileFileReadsVersionedJsonAndRejectsAmbiguousData()
    {
        var folder = Directory.CreateTempSubdirectory("win-optimizer-profiles-");
        var path = Path.Combine(folder.FullName, "profiles.json");
        const string json = """
            {
              "schemaVersion": 1,
              "profiles": [{
                "featureKey": "Run",
                "target": { "build": 28120, "revision": 3542, "architectures": ["x64"] },
                "requirements": [{ "id": 58989092, "enable": { "state": "enabled", "variant": 4 }, "disable": null }],
                "sources": ["https://betawiki.net/wiki/Windows_11_build_28120.3542"]
              }]
            }
            """;
        try
        {
            var reader = new ExperimentalProfileFile();
            File.WriteAllText(path, json);
            var profile = Assert.Single(reader.Read(path));
            Assert.Equal(Architecture.X64, Assert.Single(profile.Target.Architectures));
            Assert.Equal(4u, Assert.Single(profile.Requirements).Enable.Variant);

            File.WriteAllText(path, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
            Assert.Throws<InvalidDataException>(() => reader.Read(path));
            File.WriteAllText(path, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1"));
            Assert.Throws<System.Text.Json.JsonException>(() => reader.Read(path));
        }
        finally
        {
            File.Delete(path);
            folder.Delete();
        }
    }

    [Fact]
    public void ConfigurationAbiRetainsPriorityStateAndVariant()
    {
        var observed = WindowsExperimentalFeatureBackend.Decode(8u | (2u << 4) | (1u << 6) | (23u << 8) | (2u << 14), 0xf1234567);
        Assert.Equal(new FeatureOverride(FeatureOverrideState.Enabled, 1, 23, 2, 0xf1234567), observed.UserOverride);
        var reset = new WindowsExperimentalFeatureBackend.FeatureUpdate(57156807, null);
        Assert.Equal(32, Marshal.SizeOf<WindowsExperimentalFeatureBackend.FeatureUpdate>());
        Assert.Equal(8u, reset.Priority);
        Assert.Equal(4u, reset.Operation);
        Assert.Equal(57156807u, reset.Id);
    }

    [Fact]
    public void FlagInventoryRetriesGrowthAndUsesTheReturnedCount()
    {
        const int overflow = unchecked((int)0x80000005);
        var calls = 0;
        int Query(uint store, ref ulong stamp, WindowsExperimentalFeatureBackend.NativeFeature[]? buffer, ref uint count)
        {
            Assert.Equal(1u, store);
            calls++;
            if (buffer is null)
            {
                Assert.Equal(0u, count);
                count = 1;
                return overflow;
            }
            Assert.Equal((uint)buffer.Length, count);
            if (count == 1)
            {
                count = 3;
                return overflow;
            }
            buffer[0] = new() { Id = 101, Flags = 8 | (2u << 4) };
            buffer[1] = new() { Id = 101, Flags = 4 | (3u << 4) };
            count = 2;
            return 0;
        }

        var result = WindowsExperimentalFeatureBackend.ReadFlagStore(1, Query, CancellationToken.None);
        Assert.Equal(12, Marshal.SizeOf<WindowsExperimentalFeatureBackend.NativeFeature>());
        Assert.Null(result.Error);
        Assert.Equal(3, calls);
        Assert.Equal(new ExperimentalFlagConfiguration(101, 8, FeatureOverrideState.Enabled), result.Configurations[0]);
        Assert.Equal(new ExperimentalFlagConfiguration(101, 4, null), result.Configurations[1]);
        Assert.Equal(2, result.Configurations.Count);
    }

    [Fact]
    public void FlagInventoryReportsInvalidSizesAndHonorsCancellation()
    {
        int Query(uint store, ref ulong stamp, WindowsExperimentalFeatureBackend.NativeFeature[]? buffer, ref uint count)
        {
            Assert.Null(buffer);
            count = uint.MaxValue;
            return unchecked((int)0x80000005);
        }

        var result = WindowsExperimentalFeatureBackend.ReadFlagStore(0, Query, CancellationToken.None);
        Assert.Empty(result.Configurations);
        Assert.Contains("unexpected feature configuration count", result.Error);
        Assert.Throws<OperationCanceledException>(() =>
            WindowsExperimentalFeatureBackend.ReadFlagStore(0, Query, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void PersistentResetAndExactRestoreAreScopedToIdAndUserPriority()
    {
        using var hive = new PrivateRegistryHive();
        var root = hive.GetRoot(RegistryHive.LocalMachine);
        const uint id = 57156807, otherId = 57259990;
        Assert.Equal("2384866959", WindowsFeaturePersistence.KeyName(id));
        var prior = new FeatureOverride(FeatureOverrideState.Disabled, Variant: 63, PayloadKind: 2, Payload: uint.MaxValue);
        WindowsFeaturePersistence.Write(root, id, null, prior);
        WindowsFeaturePersistence.Write(root, otherId, null, prior);
        var otherPriority = WindowsFeaturePersistence.UserOverrides[..^1] + "10";
        using var higherPriority = root.CreateSubKey($@"{otherPriority}\{WindowsFeaturePersistence.KeyName(id)}");
        higherPriority.SetValue("EnabledState", 2);

        WindowsFeaturePersistence.Write(root, id, prior, null);
        Assert.Null(WindowsFeaturePersistence.Read(root, id));
        Assert.Equal(prior, WindowsFeaturePersistence.Read(root, otherId));
        Assert.Equal(2, higherPriority.GetValue("EnabledState"));

        WindowsFeaturePersistence.Write(root, id, null, prior);
        Assert.Equal(prior, WindowsFeaturePersistence.Read(root, id));
        Assert.Throws<IOException>(() => WindowsFeaturePersistence.Write(root, id, null, null));
    }
}
