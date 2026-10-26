using System.ComponentModel;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Checks native ownership resolution without launching aliases or treating inaccessible files as absent.</summary>
public sealed class CleanerReferencePathTests
{
    [Fact]
    public void AppExecutionAliasRetainsItsExistingLocalPath()
    {
        using var alias = new TestAppExecutionAlias();
        Assert.Equal(alias.Path, CleanerFileDeletion.ResolveReferencePath(alias.Path.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
        Assert.True(File.GetAttributes(alias.Path).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void UnsupportedReparseTagRemainsAnOwnershipError()
    {
        using var alias = new TestAppExecutionAlias(0x800000FF);
        var error = Assert.Throws<Win32Exception>(() => CleanerFileDeletion.ResolveReferencePath(alias.Path));
        Assert.Equal(1920, error.NativeErrorCode);
        Assert.Contains(alias.Path, error.Message);
        Assert.Contains("1920", error.Message);
    }

    [Fact]
    public void OrdinaryAndMissingFilesRetainTheirOwnershipPaths()
    {
        var path = Path.Combine(Path.GetTempPath(), "optimizer-reference-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "fixture");
            Assert.Equal(path, CleanerFileDeletion.ResolveReferencePath(path));
            File.Delete(path);
            Assert.Equal(path, CleanerFileDeletion.ResolveReferencePath(path));
        }
        finally { File.Delete(path); }
    }
}
