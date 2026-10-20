using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

/// <summary>Exercises discovery and exact-value repair with isolated registry boundaries and temporary files.</summary>
public sealed class CleanerRegistryReferenceTests : IDisposable
{
    private const string UserClasses = @"HKEY_CURRENT_USER\Software\Classes";
    private const string MachineClasses = @"HKEY_LOCAL_MACHINE\Software\Classes";
    private const string ComponentId = "{22A6BB70-473D-4E00-86CB-43214CA281A6}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cleaner-registry-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<(string Path, bool View32), HashSet<string>> _children = [];
    private readonly Mock<ICleanerRegistryReader> _reader = new();
    private readonly Mock<ICleanerRegistryEditor> _editor = new();
    private readonly Mock<IInteractiveUserService> _user = new();
    private readonly CleanerRegistryScanner _scanner;

    public CleanerRegistryReferenceTests()
    {
        Directory.CreateDirectory(_root);
        _user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.LocalApplicationData)).Returns(_root);
        _reader.Setup(x => x.GetSubKeyNames(It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string path, bool view32) => _children.TryGetValue((path, view32), out var children) ? children.ToArray() : []);
        _reader.Setup(x => x.GetValues(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>())).Returns([]);
        _scanner = new(new TestCleanerRegistrySessions(_user.Object, _reader.Object, _editor.Object), Mock.Of<ICleanerBackupVault>());
    }

    [Fact]
    public void LiteralDllAndIconPaths_WithExecutableNamesAndCommas_ArePreserved()
    {
        var library = Write(@"folder.exe name\valid.dll");
        var icon = Write("valid.exe,tools.dll");
        AddValues(UserClasses + @"\CLSID\" + ComponentId + @"\InprocServer32", false, new CleanerRegistryValue("", library, RegistryValueKind.String));
        AddValues(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\test", false,
            new CleanerRegistryValue("DisplayIcon", "\"" + icon + "\",-12", RegistryValueKind.String));

        Scan(RegistryArea.ActiveX, RegistryArea.Software).Findings.Should().BeEmpty();
    }

    [Fact]
    public void TypeLibraryResourceIdentifiers_ResolveToTheContainingModule()
    {
        var existing = Write("existing.dll");
        var missing = Path.Combine(_root, "removed.dll");
        var root = UserClasses + @"\TypeLib\" + ComponentId + @"\1.0\0";
        AddValues(root + @"\win64", false, new CleanerRegistryValue("", "\"" + existing + "\\2\"", RegistryValueKind.String));
        AddValues(root + @"\win32", false, new CleanerRegistryValue("", missing + "\\1", RegistryValueKind.String));

        var result = Scan(RegistryArea.Integrity);

        result.Notices.Should().BeEmpty();
        var target = (CleanerRegistryTarget)result.Findings.Should().ContainSingle().Which.Target;
        target.MissingPath.Should().Be(missing);
        target.View32.Should().BeFalse("type library storage is shared even when the referenced module is 32-bit");
    }

    [Fact]
    public void RelativeUserFonts_ResolveAgainstTheInteractiveUsersFontDirectory()
    {
        Write(@"Microsoft\Windows\Fonts\custom.ttf");
        AddValues(@"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\Fonts", false,
            new CleanerRegistryValue("Custom (TrueType)", "custom.ttf", RegistryValueKind.String),
            new CleanerRegistryValue("Removed (TrueType)", "removed.ttf", RegistryValueKind.String));

        var target = (CleanerRegistryTarget)Scan(RegistryArea.Fonts).Findings.Should().ContainSingle().Which.Target;

        target.MissingPath.Should().Be(Path.Combine(_root, @"Microsoft\Windows\Fonts\removed.ttf"));
    }

    [Fact]
    public void ComInterfaceReferences_CheckBothHivesAndPreserveExactRegistryTypes()
    {
        AddValues(UserClasses + @"\Interface\" + ComponentId + @"\ProxyStubClsid32", true,
            new CleanerRegistryValue("", ComponentId, RegistryValueKind.String), new CleanerRegistryValue("Description", ComponentId, RegistryValueKind.String));
        AddValues(UserClasses + @"\Interface\" + ComponentId + @"\TypeLib", true, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        _reader.Setup(x => x.KeyExists(MachineClasses + @"\CLSID\" + ComponentId, true)).Returns(true);

        var result = Scan(RegistryArea.Integrity);

        var finding = result.Findings.Should().ContainSingle().Which;
        var target = (CleanerRegistryTarget)finding.Target;
        finding.CanClean.Should().BeTrue();
        finding.Detail.Should().Contain("32-bit").And.Contain("(Default)");
        target.View32.Should().BeTrue();
        target.MissingPath.Should().Be(@"class:TypeLib\" + ComponentId);
        target.Kind.Should().Be((int)RegistryValueKind.String);
    }

    [Fact]
    public void ContextMenuHandlers_PreserveComponentsRegisteredInEitherArchitecture()
    {
        var path = UserClasses + @"\*\shellex\ContextMenuHandlers\test";
        AddValues(path, false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        _reader.Setup(x => x.KeyExists(MachineClasses + @"\CLSID\" + ComponentId, true)).Returns(true);

        Scan(RegistryArea.ActiveX).Findings.Should().BeEmpty();

        _reader.Setup(x => x.KeyExists(MachineClasses + @"\CLSID\" + ComponentId, true)).Returns(false);
        var target = (CleanerRegistryTarget)Scan(RegistryArea.ActiveX).Findings.Should().ContainSingle().Which.Target;
        target.MissingPath.Should().Be(@"class:CLSID\" + ComponentId);
        target.View32.Should().BeFalse("shell handler storage is shared");
        _reader.Verify(x => x.GetValues(path, true, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
    }

    [Theory]
    [InlineData(@"*\shellex\PropertySheetHandlers\test")]
    [InlineData(@"Directory\shellex\CopyHookHandlers\test")]
    [InlineData(@"Directory\Background\shellex\DragDropHandlers\test")]
    [InlineData(@"Folder\shellex\IconHandler")]
    [InlineData(@"Drive\shellex\DropHandler")]
    [InlineData(@"AllFilesystemObjects\shellex\{E357FCCD-A995-4576-B01F-234630154E96}")]
    public void StandardScan_FindsAdditionalShellHandlers_AndPreservesEitherComArchitecture(string relative)
    {
        var path = UserClasses + "\\" + relative;
        AddValues(path, false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String),
            new CleanerRegistryValue("Description", ComponentId, RegistryValueKind.String));
        var target = (CleanerRegistryTarget)Scan(RegistryArea.ActiveX).Findings.Should().ContainSingle().Which.Target;
        target.MissingPath.Should().Be(@"class:CLSID\" + ComponentId);
        target.KeyPath.Should().Be(path);

        _reader.Setup(x => x.KeyExists(MachineClasses + @"\CLSID\" + ComponentId, true)).Returns(true);
        Scan(RegistryArea.ActiveX).Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("TreatAs", ComponentId, @"CLSID\" + ComponentId)]
    [InlineData("AutoTreatAs", ComponentId, @"CLSID\" + ComponentId)]
    [InlineData("AutoConvertTo", ComponentId, @"CLSID\" + ComponentId)]
    [InlineData("TypeLib", ComponentId, @"TypeLib\" + ComponentId)]
    [InlineData("ProgID", "Test.Application.1", "Test.Application.1")]
    [InlineData("VersionIndependentProgID", "Test.Application", "Test.Application")]
    public async Task StandardScan_ComClassMappings_AreRevalidatedBeforeCorrection(string mapping, string value, string destination)
    {
        var path = UserClasses + @"\CLSID\" + ComponentId + "\\" + mapping;
        var entry = new CleanerRegistryValue("", value, RegistryValueKind.String);
        AddValues(path, true, entry);
        var target = (CleanerRegistryTarget)Scan(RegistryArea.ActiveX).Findings.Should().ContainSingle().Which.Target;
        target.MissingPath.Should().Be("class:" + destination);
        target.View32.Should().BeTrue();
        var key = new Mock<ICleanerRegistryKey>();
        key.Setup(x => x.ReadValue("")).Returns(entry);
        _editor.Setup(x => x.OpenKey(path, true, It.IsAny<bool>())).Returns(key.Object);
        await _scanner.CleanAsync(target, default);
        key.Verify(x => x.DeleteValue(""), Times.Once);

        _reader.Setup(x => x.KeyExists(MachineClasses + "\\" + destination, true)).Returns(true);
        await Assert.ThrowsAsync<IOException>(() => _scanner.CleanAsync(target, default));
        key.Verify(x => x.DeleteValue(""), Times.Once);
        Scan(RegistryArea.ActiveX).Findings.Should().BeEmpty();
    }

    [Fact]
    public void StandardScan_ComIcons_UsesIconResourceParsing()
    {
        var existing = Write("existing.dll");
        var missing = Path.Combine(_root, "missing.dll");
        AddValues(UserClasses + @"\CLSID\" + ComponentId + @"\DefaultIcon", false,
            new CleanerRegistryValue("", "\"" + missing + "\",-1", RegistryValueKind.String));
        AddValues(UserClasses + @"\CLSID\{A9CB8F39-3A3E-4E9E-8327-EC53CA939B51}\DefaultIcon", false,
            new CleanerRegistryValue("", "\"" + existing + "\",3", RegistryValueKind.String));
        ((CleanerRegistryTarget)Scan(RegistryArea.ActiveX).Findings.Should().ContainSingle().Which.Target).MissingPath.Should().Be(missing);
    }

    [Fact]
    public void DeepActiveXScan_VisitsFileTypeMappingsAndHandlers_WithoutRepeatingStandardHandlers()
    {
        AddValues(UserClasses + @"\Test.Application\CLSID", false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        AddValues(UserClasses + @"\Test.Application\CurVer", false, new CleanerRegistryValue("", "Test.Application.2", RegistryValueKind.String));
        AddValues(UserClasses + @"\.test\shellex\IconHandler", false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        AddValues(UserClasses + @"\Test.Application\shellex\PropertySheetHandlers\test", false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        var standard = UserClasses + @"\Directory\shellex\ContextMenuHandlers\test";
        AddValues(standard, false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));

        Scan(RegistryArea.ActiveX).Findings.Should().ContainSingle();
        _reader.Invocations.Clear();
        var result = _scanner.Scan(new() { RegistryAreas = [RegistryArea.ActiveX], DeepClean = true }, default);
        result.Findings.Should().HaveCount(5);
        _reader.Verify(x => x.GetValues(standard, false, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Once);

        _reader.Setup(x => x.KeyExists(MachineClasses + @"\CLSID\" + ComponentId, true)).Returns(true);
        _reader.Setup(x => x.KeyExists(MachineClasses + @"\Test.Application.2", false)).Returns(true);
        _scanner.Scan(new() { RegistryAreas = [RegistryArea.ActiveX], DeepClean = true }, default).Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(RegistryArea.FileExtensions, false, false)]
    [InlineData(RegistryArea.SystemExtensions, true, true)]
    public async Task OpenWithProgIds_UseValueNames_AndPreserveDataDuringCleanupAndRecovery(RegistryArea area, bool machine, bool binary)
    {
        var path = (machine ? MachineClasses : UserClasses) + @"\.test\OpenWithProgids";
        var entry = new CleanerRegistryValue("Missing.Application", binary ? Array.Empty<byte>() : "", binary ? RegistryValueKind.None : RegistryValueKind.String);
        AddValues(path, false, entry, new CleanerRegistryValue("Existing.Application", "", RegistryValueKind.String));
        _reader.Setup(x => x.KeyExists(MachineClasses + @"\Existing.Application", false)).Returns(true);
        var target = (CleanerRegistryTarget)Scan(area).Findings.Should().ContainSingle().Which.Target;
        target.ValueName.Should().Be(entry.Name);
        target.MissingPath.Should().Be("class:" + entry.Name);
        target.Kind.Should().Be((int)entry.Kind);
        var key = new Mock<ICleanerRegistryKey>();
        key.Setup(x => x.ReadValue(entry.Name)).Returns(entry);
        _editor.Setup(x => x.OpenKey(path, false, It.IsAny<bool>())).Returns(key.Object);

        await _scanner.CleanAsync(target, default);
        key.Verify(x => x.DeleteValue(entry.Name), Times.Once);
        key.Setup(x => x.ReadValue(entry.Name)).Returns((CleanerRegistryValue?)null);
        _scanner.Restore(target);
        key.Verify(x => x.SetValue(It.Is<CleanerRegistryValue>(v => v.Name == entry.Name && v.Kind == entry.Kind)), Times.Once);
    }

    [Fact]
    public void ExpandedRules_PreserveSentinelsMalformedMappingsAndUnrelatedMetadata()
    {
        AddValues(UserClasses + @"\CLSID\" + ComponentId + @"\TreatAs", false,
            new CleanerRegistryValue("", "{00000000-0000-0000-0000-000000000000}", RegistryValueKind.String));
        AddValues(UserClasses + @"\CLSID\" + ComponentId + @"\ProgID", false,
            new CleanerRegistryValue("", @"Invalid\Class", RegistryValueKind.String));
        AddValues(UserClasses + @"\Test.Application\CLSID", false,
            new CleanerRegistryValue("", "not a GUID", RegistryValueKind.String));
        AddValues(UserClasses + @"\.test\OpenWithProgids", false,
            new CleanerRegistryValue(@"Invalid\Class", "", RegistryValueKind.String), new CleanerRegistryValue("", "", RegistryValueKind.String));
        AddValues(UserClasses + @"\Folder\shellex\Metadata", false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        AddValues(UserClasses + @"\Folder\shellex\PropertySheetHandlers", false, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        AddValues(UserClasses + @"\Folder\shellex\IconHandler", false, new CleanerRegistryValue("", "-" + ComponentId, RegistryValueKind.String));

        _scanner.Scan(new() { RegistryAreas = [RegistryArea.ActiveX, RegistryArea.FileExtensions], DeepClean = true }, default).Findings.Should().BeEmpty();
    }

    [Fact]
    public void SoftwareScan_DetectsMissingExplicitUninstallers_AndPreservesSearchBasedCommands()
    {
        var missing = Path.Combine(_root, "uninstall.exe");
        AddValues(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\test", false,
            new CleanerRegistryValue("UninstallString", "\"" + missing + "\" /uninstall", RegistryValueKind.String),
            new CleanerRegistryValue("QuietUninstallString", "msiexec.exe /x {00000000-0000-0000-0000-000000000000}", RegistryValueKind.String),
            new CleanerRegistryValue("InstallLocation", _root, RegistryValueKind.String));

        var target = (CleanerRegistryTarget)Scan(RegistryArea.Software).Findings.Should().ContainSingle().Which.Target;

        target.ValueName.Should().Be("UninstallString");
        target.MissingPath.Should().Be(missing);
    }

    [Fact]
    public void SharedClassReferences_AreProbedOncePerScan_AndRefreshedOnTheNextScan()
    {
        AddValues(UserClasses + @"\.first", false, new CleanerRegistryValue("", "Missing.Application", RegistryValueKind.String));
        AddValues(UserClasses + @"\.second", false, new CleanerRegistryValue("", "missing.application", RegistryValueKind.String));

        Scan(RegistryArea.FileExtensions).Findings.Should().HaveCount(2);
        _reader.Verify(x => x.KeyExists(MachineClasses + @"\Missing.Application", false), Times.Once);
        _reader.Setup(x => x.KeyExists(MachineClasses + @"\Missing.Application", false)).Returns(true);
        _reader.Setup(x => x.KeyExists(MachineClasses + @"\missing.application", false)).Returns(true);

        Scan(RegistryArea.FileExtensions).Findings.Should().BeEmpty();
    }

    [Fact]
    public void UnreadableReference_ReportsItsValueAndContinuesWithinTheSameKey()
    {
        const string policy = @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
        const string help = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Help";
        _user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.ApplicationData)).Throws(new UnauthorizedAccessException("profile folder access denied"));
        AddValues(help, false, new CleanerRegistryValue("bad.chm", @"%APPDATA%\blocked.chm", RegistryValueKind.String),
            new CleanerRegistryValue("good.chm", Path.Combine(_root, "removed.chm"), RegistryValueKind.String));
        AddValues(policy, false, new CleanerRegistryValue("Startup", "\"" + Path.Combine(_root, "removed.exe") + "\" --start", RegistryValueKind.ExpandString));

        var result = Scan(RegistryArea.HelpFiles, RegistryArea.Startup);

        result.Findings.Should().HaveCount(2);
        result.Notices.Should().ContainSingle().Which.Should().Contain("bad.chm");
        _reader.Verify(x => x.GetValues(policy, true, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>()), Times.Never);
    }

    [Fact]
    public void FailedClassProbe_DoesNotHideTheNextExtensionOrBecomeAMissingClassFinding()
    {
        AddValues(UserClasses + @"\.blocked", false, new CleanerRegistryValue("", "Blocked.Application", RegistryValueKind.String));
        AddValues(UserClasses + @"\.missing", false, new CleanerRegistryValue("", "Missing.Application", RegistryValueKind.String));
        _reader.Setup(x => x.KeyExists(MachineClasses + @"\Blocked.Application", false)).Throws(new UnauthorizedAccessException("access denied"));

        var result = Scan(RegistryArea.FileExtensions);

        result.Findings.Should().ContainSingle().Which.Detail.Should().Contain(".missing");
        result.Notices.Should().ContainSingle().Which.Should().Contain(".blocked").And.Contain("access denied");
    }

    [Fact]
    public void CriticalSystemRegistrations_AreDiagnostic_AndIgnoreUnrelatedValues()
    {
        var missing = Path.Combine(_root, "removed.exe");
        AddValues(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\test", false, new CleanerRegistryValue("ImagePath", "\"" + missing + "\" -service", RegistryValueKind.String));
        AddValues(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\VirtualDeviceDrivers", false,
            new CleanerRegistryValue("VDD", new[] { missing }, RegistryValueKind.MultiString), new CleanerRegistryValue("Metadata", new[] { missing }, RegistryValueKind.MultiString));
        AddValues(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", false,
            new CleanerRegistryValue("test", "v2.30|Action=Allow|App=" + missing + "|", RegistryValueKind.String));

        var result = Scan(RegistryArea.WindowsServices, RegistryArea.VirtualDevices, RegistryArea.Firewall);

        result.Findings.Should().HaveCount(3).And.OnlyContain(x => !x.CanClean && ((CleanerRegistryTarget)x.Target).DiagnosticOnly);
    }

    [Fact]
    public async Task CleanAndRestore_UseTheScannedValueKindViewAndExactOriginalData()
    {
        var target = AssociationTarget();
        var key = SetupKey(target);

        await _scanner.CleanAsync(target, default);

        key.Verify(x => x.DeleteValue(""), Times.Once);
        _editor.Verify(x => x.OpenKey(target.KeyPath, false, true), Times.Once);
        key.Setup(x => x.ReadValue("")).Returns((CleanerRegistryValue?)null);
        _scanner.Restore(target);
        key.Verify(x => x.SetValue(It.Is<CleanerRegistryValue>(v => v.Name == "" && (string)v.Data == "Missing.Application" && v.Kind == RegistryValueKind.ExpandString)), Times.Once);
        key.Verify(x => x.Dispose(), Times.Exactly(3));
    }

    [Fact]
    public async Task TargetReappearingAfterVerification_IsPreservedAtTheWritableHandle()
    {
        var target = AssociationTarget();
        var key = SetupKey(target);
        _editor.Setup(x => x.OpenKey(target.KeyPath, false, true)).Callback(() =>
            _reader.Setup(x => x.KeyExists(MachineClasses + @"\Missing.Application", false)).Returns(true)).Returns(key.Object);

        await Assert.ThrowsAsync<IOException>(() => _scanner.CleanAsync(target, default));

        key.Verify(x => x.DeleteValue(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RedirectedInterfaceCorrection_UsesTheScanned32BitHandle()
    {
        var path = UserClasses + @"\Interface\" + ComponentId + @"\ProxyStubClsid32";
        AddValues(path, true, new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        var target = (CleanerRegistryTarget)Scan(RegistryArea.Integrity).Findings.Single().Target;
        var key = new Mock<ICleanerRegistryKey>();
        key.Setup(x => x.ReadValue("")).Returns(new CleanerRegistryValue("", ComponentId, RegistryValueKind.String));
        _editor.Setup(x => x.OpenKey(path, true, It.IsAny<bool>())).Returns(key.Object);

        await _scanner.CleanAsync(target, default);

        _editor.Verify(x => x.OpenKey(path, true, true), Times.Once);
        key.Verify(x => x.DeleteValue(""), Times.Once);
    }

    [Fact]
    public async Task CancellationAfterOpeningTheWritableHandle_PreservesTheValue()
    {
        var target = AssociationTarget();
        var key = SetupKey(target);
        using var cancellation = new CancellationTokenSource();
        _editor.Setup(x => x.OpenKey(target.KeyPath, false, true)).Callback(cancellation.Cancel).Returns(key.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.CleanAsync(target, cancellation.Token));

        key.Verify(x => x.DeleteValue(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedValueOrKindAfterVerification_IsPreserved(bool changeKind)
    {
        var target = AssociationTarget();
        var key = SetupKey(target);
        key.SetupSequence(x => x.ReadValue(""))
            .Returns(new CleanerRegistryValue("", "Missing.Application", RegistryValueKind.ExpandString))
            .Returns(new CleanerRegistryValue("", changeKind ? "Missing.Application" : "New.Application",
                changeKind ? RegistryValueKind.String : RegistryValueKind.ExpandString));

        await Assert.ThrowsAsync<IOException>(() => _scanner.CleanAsync(target, default));

        key.Verify(x => x.DeleteValue(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Restore_PreservesConflicts_AndIsIdempotentForIdenticalValues()
    {
        var target = AssociationTarget();
        var key = SetupKey(target);
        _scanner.Restore(target);
        key.Verify(x => x.SetValue(It.IsAny<CleanerRegistryValue>()), Times.Never);
        key.Setup(x => x.ReadValue("")).Returns(new CleanerRegistryValue("", "New.Application", RegistryValueKind.ExpandString));

        Assert.Throws<IOException>(() => _scanner.Restore(target));

        key.Verify(x => x.SetValue(It.IsAny<CleanerRegistryValue>()), Times.Never);
    }

    [Fact]
    public async Task DiagnosticAndCancelledCorrections_NeverDeleteValues()
    {
        var target = AssociationTarget();
        var key = SetupKey(target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _scanner.CleanAsync(target with { DiagnosticOnly = true }, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scanner.CleanAsync(target, cancellation.Token));

        key.Verify(x => x.DeleteValue(It.IsAny<string>()), Times.Never);
    }

    private CleanerRegistryTarget AssociationTarget()
    {
        AddValues(UserClasses + @"\.test", false, new CleanerRegistryValue("", "Missing.Application", RegistryValueKind.ExpandString));
        return (CleanerRegistryTarget)Scan(RegistryArea.FileExtensions).Findings.Single().Target;
    }

    private Mock<ICleanerRegistryKey> SetupKey(CleanerRegistryTarget target)
    {
        var key = new Mock<ICleanerRegistryKey>();
        key.Setup(x => x.ReadValue(target.ValueName)).Returns(new CleanerRegistryValue(target.ValueName, "Missing.Application", RegistryValueKind.ExpandString));
        _editor.Setup(x => x.OpenKey(target.KeyPath, target.View32, It.IsAny<bool>())).Returns(key.Object);
        return key;
    }

    private CleanerScanResult Scan(params RegistryArea[] areas) => _scanner.Scan(new() { RegistryAreas = areas }, default);

    /// <summary>Provides only the registrations owned by each test, including their discoverable parent keys.</summary>
    private void AddValues(string path, bool view32, params CleanerRegistryValue[] values)
    {
        _reader.Setup(x => x.GetValues(path, view32, It.IsAny<CancellationToken>(), It.IsAny<ICollection<string>?>())).Returns(values);
        for (int separator = path.LastIndexOf('\\'); separator > 0; separator = path.LastIndexOf('\\'))
        {
            var parent = path[..separator];
            if (!_children.TryGetValue((parent, view32), out var children)) _children.Add((parent, view32), children = []);
            children.Add(path[(separator + 1)..]);
            path = parent;
        }
    }

    private string Write(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test registry resource");
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
