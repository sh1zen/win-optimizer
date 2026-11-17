using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsAppMetadataSourceTests
{
    /// <summary>Exercises real package-size reads in an isolated directory, without package APIs.</summary>
    private static void WithPackageDirectory(Action<WindowsAppMetadataSource, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "WinOptimizerMetadataTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { check(new WindowsAppMetadataSource(Mock.Of<IInteractiveUserService>(), Mock.Of<ILogService>()), path); }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public void PackageSizesAreReusedOnlyForTheSameInstalledVersionAndLocation()
    {
        WithPackageDirectory((source, path) =>
        {
            var file = Path.Combine(path, "payload.bin");
            File.WriteAllBytes(file, new byte[42]);
            Assert.Equal(42, source.GetPackageSize("Vendor.Tool_1", path, false));
            // Deployed package contents are immutable; another version must measure its own payload.
            File.WriteAllBytes(file, new byte[84]);
            Assert.Equal(42, source.GetPackageSize("Vendor.Tool_1", path, false));
            Assert.Equal(84, source.GetPackageSize("Vendor.Tool_2", path, false));
            var moved = Path.Combine(path, "moved");
            Directory.CreateDirectory(moved);
            File.WriteAllBytes(Path.Combine(moved, "payload.bin"), new byte[21]);
            Assert.Equal(21, source.GetPackageSize("Vendor.Tool_1", moved, false));
        });
    }

    [Fact]
    public void DevelopmentPackagesAndFailedMeasurementsAreReadAgain()
    {
        WithPackageDirectory((source, path) =>
        {
            var payload = Path.Combine(path, "payload.bin");
            File.WriteAllBytes(payload, new byte[42]);
            Assert.Equal(42, source.GetPackageSize("Development.Tool_1", path, true));
            File.WriteAllBytes(payload, new byte[84]);
            Assert.Equal(84, source.GetPackageSize("Development.Tool_1", path, true));
            var unavailable = Path.Combine(path, "unavailable");
            Assert.Null(source.GetPackageSize("Vendor.Tool_1", unavailable, false));
            Directory.CreateDirectory(unavailable);
            File.WriteAllBytes(Path.Combine(unavailable, "payload.bin"), new byte[21]);
            Assert.Equal(21, source.GetPackageSize("Vendor.Tool_1", unavailable, false));
        });
    }

    [Fact]
    public void SharedAppxRegistrationsReadDetailsOnlyForMatchingPackagesAndIsolateFailures()
    {
        WithPackageDirectory((source, path) =>
        {
            File.WriteAllBytes(Path.Combine(path, "payload.bin"), new byte[42]);
            var installed = DateTimeOffset.UnixEpoch;
            var package = new ItemDefinition { Id = "app", Name = "App", Description = "", AppxPackageName = ["Test.App"] };
            var result = source.ReadPackages([package],
            [
                new("Other.App", () => throw new InvalidOperationException("Unmatched package must stay lazy.")),
                new("Test.App", () => throw new UnauthorizedAccessException("App registration unavailable.")),
                new("TEST.APP", () => new("Test.App_1", "Test.App_family", installed, path, false))
            ]);
            Assert.Equal(new WindowsAppMetadataSource.PackageMetadata("TEST.APP", "Test.App_family", installed, 42), Assert.Single(result));
        });
    }

    [Fact]
    public void DiscoveredProgramMetadataUsesTheExactRegistrationScope()
    {
        var program = new InstalledProgram(WindowsInstalledProgramSource.Roots[2] + @"\Tool", "Tool", "remove.exe");
        var snapshot = new WindowsAppMetadataSource.MetadataSnapshot(
        [
            new("Tool", "Tool", null, 999, null, WindowsInstalledProgramSource.Roots[0] + @"\Tool"),
            new("Tool", "Tool", null, 42, null, program.KeyPath),
        ], [], new Dictionary<string, DateTimeOffset>());
        Assert.Equal(42, snapshot.Match(program.ToDefinition(), path => path).SizeBytes);
    }

    [Fact]
    public void MetadataUsesExactFingerprintsAndKeepsMissingValuesUnknown()
    {
        var item = new ItemDefinition { Id = "tool", Name = "Tool v1", Description = "", RegistryDisplayName = "Tool 1.{version}" };
        var date = DateTimeOffset.UnixEpoch.AddYears(55);
        WindowsAppMetadataSource.Registration[] registrations =
        [
            new("one", "Tool 1.5", date, 8192, @"C:\Tools\tool.exe"),
            new("two", "Tool 2.0", date.AddDays(1), 99999, @"C:\Tools\other.exe"),
            new("copy", "Tool 1.5", date, 8192, @"C:\Tools\tool.exe"),
        ];
        var usage = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Tools\TOOL.exe"] = date.AddHours(1), [@"C:\Tools\other.exe"] = date.AddDays(2),
        };
        var snapshot = new WindowsAppMetadataSource.MetadataSnapshot(registrations, [], usage);
        Assert.Equal(new AppMetadata(date, 8192, date.AddHours(1)), snapshot.Match(item, path => path));
        var missing = new WindowsAppMetadataSource.MetadataSnapshot([], [], usage);
        Assert.Equal(new AppMetadata(null, null, null), missing.Match(item, path => path));
    }

    [Fact]
    public void PackageIdentityMatchesUsageWithoutConfusingSiblingFamilies()
    {
        var item = new ItemDefinition { Id = "app", Name = "App", Description = "", AppxPackageName = ["Vendor.App"] };
        var date = DateTimeOffset.UnixEpoch.AddYears(55);
        WindowsAppMetadataSource.PackageMetadata[] packages = [new("Vendor.App", "Vendor.App_123", date, 4096)];
        var usage = new Dictionary<string, DateTimeOffset>
        {
            ["Vendor.App_123!Main"] = date.AddHours(2), ["Vendor.App_1234!Main"] = date.AddDays(2),
        };
        var snapshot = new WindowsAppMetadataSource.MetadataSnapshot([], packages, usage);
        Assert.Equal(new AppMetadata(date, 4096, date.AddHours(2)), snapshot.Match(item, path => path));
    }

    [Fact]
    public void SharedSnapshotUsesLatestMatchingUsageAcrossPathsAndPackageEntries()
    {
        var date = DateTimeOffset.UnixEpoch.AddYears(55);
        var item = new ItemDefinition { Id = "app", Name = "Tool", Description = "", AppxPackageName = ["vendor.app"] };
        var second = new ItemDefinition { Id = "other", Name = "Other", Description = "", DetectionPaths = [@"C:\Tools\other.exe"] };
        var snapshot = new WindowsAppMetadataSource.MetadataSnapshot(
            [new("tool", "TOOL", date, 2048, @"C:\Tools\tool.exe")],
            [new("Vendor.App", "Vendor.App_123", date.AddDays(1), 4096)],
            new Dictionary<string, DateTimeOffset>
            {
                [@"C:\TOOLS\TOOL.exe"] = date.AddHours(3),
                [@"C:\Tools\tool.exe"] = date.AddHours(4),
                ["Vendor.App_123!First"] = date.AddHours(2),
                ["VENDOR.APP_123!Second"] = date.AddHours(5),
                [@"C:\Tools\other.exe"] = date.AddHours(6),
            });

        Assert.Equal(new AppMetadata(date.AddDays(1), 4096, date.AddHours(5)), snapshot.Match(item, path => path));
        Assert.Equal(new AppMetadata(null, null, date.AddHours(6)), snapshot.Match(second, path => path));
    }

    [Fact]
    public void ParsesRegistryUnitsDatesAndQuotedIconPaths()
    {
        Assert.Equal(2048, WindowsAppMetadataSource.ParseSize(2));
        Assert.Equal((long)uint.MaxValue * 1024, WindowsAppMetadataSource.ParseSize(-1));
        Assert.Null(WindowsAppMetadataSource.ParseSize("not a number"));
        Assert.Null(WindowsAppMetadataSource.ParseSize(long.MaxValue));
        Assert.Null(WindowsAppMetadataSource.ParseInstallDate("20250230"));
        Assert.Equal(new DateTimeOffset(2025, 2, 28, 0, 0, 0, TimeSpan.Zero), WindowsAppMetadataSource.ParseInstallDate("20250228"));
        Assert.Equal(@"C:\Tools, Inc\tool.exe", WindowsAppMetadataSource.ParseIconPath("\"C:\\Tools, Inc\\tool.exe\",0"));
        Assert.Equal(@"C:\Tools\tool.exe", WindowsAppMetadataSource.ParseIconPath(@"C:\Tools\tool.exe,-1"));
    }

    [Fact]
    public void ShellHistoryRequiresRecognizedBinaryDataAndARealTimestamp()
    {
        var date = DateTimeOffset.UnixEpoch.AddYears(55);
        var bytes = new byte[72];
        Assert.Null(WindowsAppMetadataSource.ParseUsageTime(bytes));
        Assert.Null(WindowsAppMetadataSource.ParseUsageTime(new byte[16]));
        BitConverter.GetBytes(date.ToFileTime()).CopyTo(bytes, 60);
        Assert.Equal(date, WindowsAppMetadataSource.ParseUsageTime(bytes));
        BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, 60);
        Assert.Null(WindowsAppMetadataSource.ParseUsageTime(bytes));
        Assert.Equal(@"C:\Tools\app.exe", WindowsAppMetadataSource.DecodeUsageName(@"P:\Gbbyf\ncc.rkr"));
    }

    [Fact]
    public void ShellFolderIdsAndUserVariablesResolveToTheRequestedAccount()
    {
        static string Resolve(Environment.SpecialFolder folder) => folder switch
        {
            Environment.SpecialFolder.ProgramFiles => @"D:\Applications",
            Environment.SpecialFolder.LocalApplicationData => @"C:\Users\Interactive\AppData\Local",
            _ => "",
        };
        Assert.Equal(@"D:\Applications\Vendor\app.exe", AppPaths.Expand(
            @"{6D809377-6AF0-444B-8957-A3773F02200E}\Vendor\app.exe", Resolve));
        Assert.Equal(@"C:\Users\Interactive\AppData\Local\Vendor\app.exe", AppPaths.Expand(
            @"{f1b32785-6fba-4fcf-9d55-7b8e7f157091}\Vendor\app.exe", Resolve));
        Assert.Equal(@"C:\Users\Interactive\AppData\Local\Vendor\app.exe", AppPaths.Expand(
            @"%localappdata%\Vendor\app.exe", Resolve));
    }
}
