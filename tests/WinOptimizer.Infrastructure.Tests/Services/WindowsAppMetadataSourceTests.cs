using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

public class WindowsAppMetadataSourceTests
{
    [Fact]
    public void DiscoveredProgramMetadataUsesTheExactRegistrationScope()
    {
        var program = new InstalledProgram(WindowsInstalledProgramSource.Roots[2] + @"\Tool", "Tool", "remove.exe");
        var metadata = WindowsAppMetadataSource.Match(program.ToDefinition(),
        [
            new("Tool", "Tool", null, 999, null, WindowsInstalledProgramSource.Roots[0] + @"\Tool"),
            new("Tool", "Tool", null, 42, null, program.KeyPath),
        ], [], new Dictionary<string, DateTimeOffset>(), path => path);
        Assert.Equal(42, metadata.SizeBytes);
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
        Assert.Equal(new AppMetadata(date, 8192, date.AddHours(1)), WindowsAppMetadataSource.Match(item, registrations, [], usage, path => path));
        Assert.Equal(new AppMetadata(null, null, null), WindowsAppMetadataSource.Match(item, [], [], usage, path => path));
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
        Assert.Equal(new AppMetadata(date, 4096, date.AddHours(2)), WindowsAppMetadataSource.Match(item, [], packages, usage, path => path));
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
        Assert.Equal(@"D:\Applications\Vendor\app.exe", WindowsAppMetadataSource.ExpandPath(
            @"{6D809377-6AF0-444B-8957-A3773F02200E}\Vendor\app.exe", Resolve));
        Assert.Equal(@"C:\Users\Interactive\AppData\Local\Vendor\app.exe", WindowsAppMetadataSource.ExpandPath(
            @"{f1b32785-6fba-4fcf-9d55-7b8e7f157091}\Vendor\app.exe", Resolve));
        Assert.Equal(@"C:\Users\Interactive\AppData\Local\Vendor\app.exe", WindowsAppMetadataSource.ExpandPath(
            @"%localappdata%\Vendor\app.exe", Resolve));
    }
}
