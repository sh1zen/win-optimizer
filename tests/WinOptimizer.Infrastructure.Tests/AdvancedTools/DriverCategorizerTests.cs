using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.AdvancedTools.Helpers;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.AdvancedTools;

public class DriverCategorizerTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("ansi")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public void CopiesStoragePackagesWithTheirNestedPayloadForSupportedInfEncodings(string encodingName)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinOptimizer-driver-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "Source", "Controller");
        var winpe = Path.Combine(root, "WinPE");
        var oem = Path.Combine(root, "OEM");
        Directory.CreateDirectory(Path.Combine(package, "amd64"));
        try
        {
            Encoding encoding = encodingName switch
            {
                "utf16-le" => Encoding.Unicode,
                "utf16-be" => Encoding.BigEndianUnicode,
                "ansi" => Encoding.Latin1,
                _ => new UTF8Encoding(false)
            };
            File.WriteAllText(Path.Combine(package, "controller.inf"), "; Contrôleur\r\n[Version]\r\nClass = SCSIAdapter\r\n", encoding);
            File.WriteAllBytes(Path.Combine(package, "amd64", "controller.sys"), [1, 2, 3, 4]);
            var categorizer = new WinOptimizer.Infrastructure.Features.AdvancedTools.Helpers.DriverCategorizer(
                Mock.Of<ILogService>(), new WinOptimizer.Infrastructure.Features.Common.Services.FileSystemService());

            Assert.Equal(1, categorizer.CategorizeAndCopyDrivers(Path.Combine(root, "Source"), winpe, oem));

            Assert.True(File.Exists(Path.Combine(winpe, "Controller", "controller.inf")));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(winpe, "Controller", "amd64", "controller.sys")));
            Assert.False(Directory.Exists(oem));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private readonly Mock<ILogService> _logService = new();
    private readonly Mock<IFileSystemService> _fileSystemService = new();
    private readonly DriverCategorizer _sut;

    public DriverCategorizerTests()
    {
        _sut = new DriverCategorizer(_logService.Object, _fileSystemService.Object);
    }

    // ---------------------------------------------------------------
    // IsStorageDriver - Filename keyword detection
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("iaahci.inf")]
    [InlineData("iastor.inf")]
    [InlineData("iastorac.inf")]
    [InlineData("iastora.inf")]
    [InlineData("iastorv.inf")]
    [InlineData("vmd.inf")]
    [InlineData("irst.inf")]
    [InlineData("rst.inf")]
    public void IsStorageDriver_StorageFilenameKeyword_ReturnsTrue(string fileName)
    {
        var infPath = $"C:\\Drivers\\{fileName}";
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns(fileName);

        var result = _sut.IsStorageDriver(infPath);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsStorageDriver_NonStorageFilename_ChecksFileContent()
    {
        var infPath = "C:\\Drivers\\network.inf";
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("network.inf");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Returns("[Version]\nClass=Net\nClassGuid={something}");

        var result = _sut.IsStorageDriver(infPath);

        result.Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // IsStorageDriver - Class-based detection
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("SCSIAdapter")]
    [InlineData("hdc")]
    [InlineData("HDC")]
    public void IsStorageDriver_StorageClass_ReturnsTrue(string className)
    {
        var infPath = "C:\\Drivers\\storage.inf";
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("storage.inf");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Returns($"[Version]\nClass = {className}\nClassGuid={{something}}");

        var result = _sut.IsStorageDriver(infPath);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsStorageDriver_NonStorageClass_ReturnsFalse()
    {
        var infPath = "C:\\Drivers\\display.inf";
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("display.inf");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Returns("[Version]\nClass=Display\nClassGuid={something}");

        var result = _sut.IsStorageDriver(infPath);

        result.Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // IsStorageDriver - UTF-8 input
    // ---------------------------------------------------------------

    [Fact]
    public void IsStorageDriver_Utf8Read_RecognizesStorageClass()
    {
        var infPath = "C:\\Drivers\\storage.inf";
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("storage.inf");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Returns("[Version]\nClass=SCSIAdapter\nClassGuid={something}");

        var result = _sut.IsStorageDriver(infPath);

        result.Should().BeTrue();
    }

    // ---------------------------------------------------------------
    // IsStorageDriver - Exception handling
    // ---------------------------------------------------------------

    [Fact]
    public void IsStorageDriver_ExceptionThrown_ReturnsFalse()
    {
        var infPath = "C:\\Drivers\\bad.inf";
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("bad.inf");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Throws(new IOException("Disk error"));

        var result = _sut.IsStorageDriver(infPath);

        result.Should().BeFalse();
        _logService.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("Could not categorize driver"))), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - No .inf files found
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_NoInfFiles_ReturnsZero()
    {
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(Array.Empty<string>());

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM");

        result.Should().Be(0);
        _logService.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("No .inf files"))), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - Storage driver routed to WinPE
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_StorageDriver_CopiesToWinPePath()
    {
        var infPath = "C:\\Source\\DriverFolder\\iastor.inf";
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(new[] { infPath });
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("iastor.inf");
        _fileSystemService.Setup(f => f.GetDirectoryName(infPath)).Returns("C:\\Source\\DriverFolder");
        _fileSystemService.Setup(f => f.GetFileName("C:\\Source\\DriverFolder")).Returns("DriverFolder");
        _fileSystemService.Setup(f => f.CombinePath("C:\\WinPE", "DriverFolder")).Returns("C:\\WinPE\\DriverFolder");
        _fileSystemService.Setup(f => f.DirectoryExists("C:\\WinPE\\DriverFolder")).Returns(false);
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source\\DriverFolder", "*", SearchOption.AllDirectories))
            .Returns(new[] { infPath, "C:\\Source\\DriverFolder\\iastor.sys" });
        _fileSystemService.Setup(f => f.GetFileName("C:\\Source\\DriverFolder\\iastor.sys")).Returns("iastor.sys");
        _fileSystemService.Setup(f => f.CombinePath("C:\\WinPE\\DriverFolder", "iastor.inf")).Returns("C:\\WinPE\\DriverFolder\\iastor.inf");
        _fileSystemService.Setup(f => f.CombinePath("C:\\WinPE\\DriverFolder", "iastor.sys")).Returns("C:\\WinPE\\DriverFolder\\iastor.sys");

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM");

        result.Should().Be(1);
        _fileSystemService.Verify(f => f.CreateDirectory("C:\\WinPE\\DriverFolder"), Times.Once);
        _fileSystemService.Verify(f => f.CopyFile(infPath, "C:\\WinPE\\DriverFolder\\iastor.inf", true), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - Non-storage driver routed to OEM
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_NonStorageDriver_CopiesToOemPath()
    {
        var infPath = "C:\\Source\\NetDriver\\network.inf";
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(new[] { infPath });
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("network.inf");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Returns("[Version]\nClass=Net");
        _fileSystemService.Setup(f => f.GetDirectoryName(infPath)).Returns("C:\\Source\\NetDriver");
        _fileSystemService.Setup(f => f.GetFileName("C:\\Source\\NetDriver")).Returns("NetDriver");
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM", "NetDriver")).Returns("C:\\OEM\\NetDriver");
        _fileSystemService.Setup(f => f.DirectoryExists("C:\\OEM\\NetDriver")).Returns(false);
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source\\NetDriver", "*", SearchOption.AllDirectories))
            .Returns(new[] { infPath });
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM\\NetDriver", "network.inf")).Returns("C:\\OEM\\NetDriver\\network.inf");

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM");

        result.Should().Be(1);
        _fileSystemService.Verify(f => f.CreateDirectory("C:\\OEM\\NetDriver"), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - Excludes working directory
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_WithWorkingDirectoryExclude_FiltersDrivers()
    {
        var excludedInf = "C:\\Work\\Temp\\driver.inf";
        var validInf = "C:\\Source\\OtherDriver\\network.inf";

        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(new[] { excludedInf, validInf });
        _fileSystemService.Setup(f => f.GetFileName(validInf)).Returns("network.inf");
        _fileSystemService.Setup(f => f.ReadAllText(validInf, Encoding.UTF8))
            .Returns("[Version]\nClass=Net");
        _fileSystemService.Setup(f => f.GetDirectoryName(validInf)).Returns("C:\\Source\\OtherDriver");
        _fileSystemService.Setup(f => f.GetFileName("C:\\Source\\OtherDriver")).Returns("OtherDriver");
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM", "OtherDriver")).Returns("C:\\OEM\\OtherDriver");
        _fileSystemService.Setup(f => f.DirectoryExists("C:\\OEM\\OtherDriver")).Returns(false);
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source\\OtherDriver", "*", SearchOption.AllDirectories))
            .Returns(new[] { validInf });
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM\\OtherDriver", "network.inf")).Returns("C:\\OEM\\OtherDriver\\network.inf");

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM", "C:\\Work");

        result.Should().Be(1);
        _logService.Verify(l => l.LogInformation(It.Is<string>(s => s.Contains("Excluded 1 driver"))), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - Duplicate target directory appends suffix
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_DuplicateTargetDir_AppendsSuffix()
    {
        var infPath = "C:\\Source\\DriverFolder\\network.inf";
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(new[] { infPath });
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("network.inf");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Returns("[Version]\nClass=Net");
        _fileSystemService.Setup(f => f.GetDirectoryName(infPath)).Returns("C:\\Source\\DriverFolder");
        _fileSystemService.Setup(f => f.GetFileName("C:\\Source\\DriverFolder")).Returns("DriverFolder");
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM", "DriverFolder")).Returns("C:\\OEM\\DriverFolder");
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM", "DriverFolder_1")).Returns("C:\\OEM\\DriverFolder_1");
        _fileSystemService.Setup(f => f.DirectoryExists("C:\\OEM\\DriverFolder")).Returns(true);
        _fileSystemService.Setup(f => f.DirectoryExists("C:\\OEM\\DriverFolder_1")).Returns(false);
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source\\DriverFolder", "*", SearchOption.AllDirectories))
            .Returns(new[] { infPath });
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM\\DriverFolder_1", "network.inf")).Returns("C:\\OEM\\DriverFolder_1\\network.inf");

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM");

        result.Should().Be(1);
        _fileSystemService.Verify(f => f.CreateDirectory("C:\\OEM\\DriverFolder_1"), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - Same folder processed only once
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_MultipleInfsInSameFolder_ProcessedOnce()
    {
        var inf1 = "C:\\Source\\DriverFolder\\driver1.inf";
        var inf2 = "C:\\Source\\DriverFolder\\driver2.inf";

        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(new[] { inf1, inf2 });
        _fileSystemService.Setup(f => f.GetFileName(inf1)).Returns("driver1.inf");
        _fileSystemService.Setup(f => f.GetFileName(inf2)).Returns("driver2.inf");
        _fileSystemService.Setup(f => f.ReadAllText(inf1, Encoding.UTF8))
            .Returns("[Version]\nClass=Net");
        _fileSystemService.Setup(f => f.GetDirectoryName(inf1)).Returns("C:\\Source\\DriverFolder");
        _fileSystemService.Setup(f => f.GetDirectoryName(inf2)).Returns("C:\\Source\\DriverFolder");
        _fileSystemService.Setup(f => f.GetFileName("C:\\Source\\DriverFolder")).Returns("DriverFolder");
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM", "DriverFolder")).Returns("C:\\OEM\\DriverFolder");
        _fileSystemService.Setup(f => f.DirectoryExists("C:\\OEM\\DriverFolder")).Returns(false);
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source\\DriverFolder", "*", SearchOption.AllDirectories))
            .Returns(new[] { inf1, inf2 });
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM\\DriverFolder", "driver1.inf")).Returns("C:\\OEM\\DriverFolder\\driver1.inf");
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM\\DriverFolder", "driver2.inf")).Returns("C:\\OEM\\DriverFolder\\driver2.inf");

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM");

        // Only 1 folder processed even though 2 inf files
        result.Should().Be(1);
        _fileSystemService.Verify(f => f.CreateDirectory("C:\\OEM\\DriverFolder"), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - Copy failure logs error and continues
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_CopyFailure_LogsErrorAndContinues()
    {
        var infPath = "C:\\Source\\DriverFolder\\network.inf";
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(new[] { infPath });
        _fileSystemService.Setup(f => f.GetFileName(infPath)).Returns("network.inf");
        _fileSystemService.Setup(f => f.GetDirectoryName(infPath)).Returns("C:\\Source\\DriverFolder");
        _fileSystemService.Setup(f => f.ReadAllText(infPath, Encoding.UTF8))
            .Returns("[Version]\nClass=Net");
        _fileSystemService.Setup(f => f.GetFileName("C:\\Source\\DriverFolder")).Returns("DriverFolder");
        _fileSystemService.Setup(f => f.CombinePath("C:\\OEM", "DriverFolder")).Returns("C:\\OEM\\DriverFolder");
        _fileSystemService.Setup(f => f.DirectoryExists("C:\\OEM\\DriverFolder")).Returns(false);
        _fileSystemService.Setup(f => f.CreateDirectory("C:\\OEM\\DriverFolder"))
            .Throws(new IOException("Permission denied"));

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM");

        result.Should().Be(0);
        _logService.Verify(l => l.LogError(
            It.Is<string>(s => s.Contains("Failed to copy driver")),
            It.IsAny<Exception>()), Times.Once);
    }

    // ---------------------------------------------------------------
    // CategorizeAndCopyDrivers - All files excluded returns zero
    // ---------------------------------------------------------------

    [Fact]
    public void CategorizeAndCopyDrivers_AllFilesExcluded_ReturnsZero()
    {
        var excludedInf = "C:\\Work\\driver.inf";
        _fileSystemService.Setup(f => f.GetFiles("C:\\Source", "*.inf", SearchOption.AllDirectories))
            .Returns(new[] { excludedInf });

        var result = _sut.CategorizeAndCopyDrivers("C:\\Source", "C:\\WinPE", "C:\\OEM", "C:\\Work");

        result.Should().Be(0);
        _logService.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("No valid drivers"))), Times.Once);
    }
}
