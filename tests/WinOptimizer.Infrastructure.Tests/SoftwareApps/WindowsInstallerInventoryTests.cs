using System.ComponentModel;
using System.Text;
using Microsoft.Win32;
using WinOptimizer.Infrastructure.Features.SoftwareApps.Leftover;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.SoftwareApps;

/// <summary>Exercises the product-state policy through a fake read-only MSI property boundary.</summary>
public sealed class WindowsInstallerInventoryTests
{
    private const string Product = "{12345678-1234-1234-1234-123456789012}";

    [Fact]
    public void AdvertisedProductWithoutSourceListDoesNotStopTheInventory()
    {
        var sources = new SourceFixture((_, _) => (1610u, ""), () => true);
        var inventory = new WindowsInstallerInventory(new PropertyFixture(_ => "1"), sources);
        HashSet<string> files = [], directories = [];
        Assert.False(inventory.ReadProductReferences(Product, null, 4, files, directories));
        inventory.ReadSources(Product, null, 4, false, directories, default);
        Assert.Empty(directories); Assert.Empty(files);
        Assert.Equal(1, sources.AbsenceChecks);
        Assert.Equal(new[] { (1u, 0u) }, sources.Requests);
    }

    [Fact]
    public void ExistingProductAndPatchSourceListsRetainAllLocalRepairPaths()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "source-reference-" + Guid.NewGuid().ToString("N"));
        foreach (bool patch in new[] { false, true })
        {
            var sources = new SourceFixture((options, index) => (options & 3) == 1 && index == 0
                ? (0u, directory) : (259u, ""), () => throw new InvalidOperationException("No registry probe is needed"));
            HashSet<string> directories = [];
            new WindowsInstallerInventory(new PropertyFixture(_ => "5"), sources).ReadSources(Product, null, 4, patch, directories, default);
            Assert.Equal(directory, Assert.Single(directories)); Assert.Equal(0, sources.AbsenceChecks);
            var flags = patch ? 0x40000000u : 0u;
            Assert.Equal(new[] { (flags | 1u, 0u), (flags | 1u, 1u), (flags | 2u, 0u) }, sources.Requests);
        }
    }

    [Theory]
    [InlineData(1610u, false, 0u, 1)]
    [InlineData(1610u, true, 0u, 0)]
    [InlineData(1610u, false, 1u, 0)]
    [InlineData(5u, false, 0u, 0)]
    [InlineData(1605u, false, 0u, 0)]
    public void DamagedUnreadableOrChangingSourceListsRemainErrors(uint error, bool patch, uint failingIndex, int probes)
    {
        var sources = new SourceFixture((_, index) => index < failingIndex ? (0u, "https://example.invalid/repair/") : (error, ""), () => false);
        var failure = Assert.Throws<Win32Exception>(() => new WindowsInstallerInventory(new PropertyFixture(_ => "5"), sources)
            .ReadSources(Product, null, 4, patch, [], default));
        Assert.Equal((int)error, failure.NativeErrorCode);
        Assert.Contains(Product, failure.Message); Assert.Contains("context 4", failure.Message);
        Assert.Contains("index " + failingIndex, failure.Message);
        Assert.Equal(probes, sources.AbsenceChecks);
    }

    [Fact]
    public void InaccessibleRegistryProbeCannotBeTreatedAsAnEmptySourceList()
    {
        var sources = new SourceFixture((_, _) => (1610u, ""), () => throw new UnauthorizedAccessException("Fixture registry access denied"));
        Assert.Throws<UnauthorizedAccessException>(() => new WindowsInstallerInventory(new PropertyFixture(_ => "1"), sources)
            .ReadSources(Product, null, 4, false, [], default));
    }

    [Fact]
    public void SourceListDisappearingBetweenSourceTypesCannotBeTreatedAsEmpty()
    {
        var sources = new SourceFixture((options, index) => options == 1
            ? index == 0 ? (0u, "https://example.invalid/repair/") : (259u, "")
            : (1610u, ""), () => throw new InvalidOperationException("Partial enumeration must not probe for absence"));
        var failure = Assert.Throws<Win32Exception>(() => new WindowsInstallerInventory(new PropertyFixture(_ => "5"), sources)
            .ReadSources(Product, null, 4, false, [], default));
        Assert.Equal(1610, failure.NativeErrorCode); Assert.Equal(0, sources.AbsenceChecks);
    }

    [Fact]
    public void CancelledSourceInventoryDoesNotQueryWindows()
    {
        var sources = new SourceFixture((_, _) => throw new InvalidOperationException("No native call is allowed"), () => false);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new WindowsInstallerInventory(new PropertyFixture(_ => "1"), sources)
            .ReadSources(Product, null, 4, false, [], cancellation.Token));
        Assert.Empty(sources.Requests);
    }

    [Theory]
    [InlineData(4u, null, RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\Products\87654321432143212143214365870921")]
    [InlineData(1u, "S-1-5-21-1-2-3-1001", RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed\S-1-5-21-1-2-3-1001\Installer\Products\87654321432143212143214365870921")]
    [InlineData(2u, "S-1-5-21-1-2-3-1001", RegistryHive.Users, @"S-1-5-21-1-2-3-1001\Software\Microsoft\Installer\Products\87654321432143212143214365870921")]
    public void SourceListProbeUsesTheExactProductAccountAndContext(uint context, string? sid, RegistryHive hive, string path)
    {
        Assert.Equal((hive, path), WindowsInstallerSourceLists.ProductRegistration(Product, sid, context));
    }

    [Theory]
    [InlineData("invalid", null, 4u)]
    [InlineData(Product, null, 2u)]
    [InlineData(Product, "invalid\\path", 2u)]
    [InlineData(Product, "S-1-5-21-1-2-3-1001", 7u)]
    public void SourceListProbeRejectsInvalidRegistrationIdentities(string product, string? sid, uint context)
    {
        Assert.Throws<IOException>(() => WindowsInstallerSourceLists.ProductRegistration(product, sid, context));
    }

    /// <summary>Supplies native enumeration results and explicit registry evidence without querying installed products.</summary>
    private sealed class SourceFixture(Func<uint, uint, (uint Code, string Value)> enumerate, Func<bool> absent) : IInstallerSourceLists
    {
        public List<(uint Options, uint Index)> Requests { get; } = [];
        public int AbsenceChecks { get; private set; }
        public uint Enumerate(string id, string? sid, uint context, uint options, uint index, StringBuilder source, ref uint length)
        {
            Assert.Equal(Product, id); Assert.Null(sid); Assert.Equal(4u, context);
            Requests.Add((options, index));
            var (code, value) = enumerate(options, index);
            source.Append(value); length = (uint)value.Length;
            return code;
        }
        public bool IsProductSourceListAbsent(string product, string? sid, uint context)
        {
            Assert.Equal(Product, product); Assert.Null(sid); Assert.Equal(4u, context);
            AbsenceChecks++;
            return absent();
        }
    }

    [Fact]
    public void AdvertisedProductDoesNotQueryInstalledOnlyProperties()
    {
        var properties = new PropertyFixture(property => property == "State" ? "1" : throw new Win32Exception(1608));
        var inventory = new WindowsInstallerInventory(properties);
        HashSet<string> files = [], directories = [];
        Assert.False(inventory.ReadProductReferences(Product, null, 4, files, directories));
        Assert.Empty(files); Assert.Empty(directories);
        Assert.Equal(new[] { "State" }, properties.Requests);
    }

    [Theory]
    [InlineData(1u, "S-1-5-21-1-2-3-1001")]
    [InlineData(2u, "S-1-5-21-1-2-3-1001")]
    [InlineData(4u, null)]
    public void InstalledProductKeepsCacheSourceAndInstallationDirectoryReferences(uint context, string? sid)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "msi-reference-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cached.msi");
        var source = Path.Combine(root, "Source");
        var installation = Path.Combine(root, "Installed");
        var properties = new PropertyFixture(property => property switch
        {
            "State" => "5", "LocalPackage" => cache, "InstallSource" => source, "InstallLocation" => installation,
            _ => throw new InvalidOperationException("Unexpected property")
        }, sid, context);
        HashSet<string> files = [], directories = [];
        Assert.True(new WindowsInstallerInventory(properties).ReadProductReferences(Product, sid, context, files, directories));
        Assert.Equal(cache, Assert.Single(files));
        Assert.Equal(new[] { source, installation }, directories);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1608)]
    [InlineData(1610)]
    public void InstalledProductErrorsRemainErrorsInsteadOfMissingReferences(int error)
    {
        var properties = new PropertyFixture(property => property == "State" ? "5" : throw new Win32Exception(error));
        var failure = Assert.Throws<Win32Exception>(() => new WindowsInstallerInventory(properties)
            .ReadProductReferences(Product, null, 4, [], []));
        Assert.Equal(error, failure.NativeErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("unexpected")]
    public void UnrecognizedProductStateCannotAuthorizeCacheDeletion(string state)
    {
        var properties = new PropertyFixture(_ => state);
        Assert.Throws<Win32Exception>(() => new WindowsInstallerInventory(properties)
            .ReadProductReferences(Product, null, 4, [], []));
    }

    /// <summary>Enforces the product, account and context at the native property boundary.</summary>
    private sealed class PropertyFixture(Func<string, string> read, string? expectedSid = null, uint expectedContext = 4) : IInstallerProductProperties
    {
        public List<string> Requests { get; } = [];
        public string Read(string product, string? sid, uint context, string property)
        {
            Assert.Equal(Product, product); Assert.Equal(expectedSid, sid); Assert.Equal(expectedContext, context);
            Requests.Add(property);
            return read(property);
        }
    }
}
