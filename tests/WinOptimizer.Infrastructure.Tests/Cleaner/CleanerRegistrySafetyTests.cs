using FluentAssertions;
using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Cleaner.Models;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Cleaner;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Cleaner;

public class CleanerRegistrySafetyTests
{
    [Theory]
    [InlineData(RegistryArea.ActiveX, @"Classes\CLSID\test\InprocServer32", "ThreadingModel", false)]
    [InlineData(RegistryArea.ActiveX, @"Classes\CLSID\test\InprocServer32", "", true)]
    [InlineData(RegistryArea.ApplicationPaths, @"Software\App Paths\test", "Path", false)]
    [InlineData(RegistryArea.ApplicationPaths, @"Software\App Paths\test", "", true)]
    [InlineData(RegistryArea.Integrity, @"Software\Classes\TypeLib\test\HELPDIR", "", false)]
    [InlineData(RegistryArea.Integrity, @"Software\Classes\TypeLib\test\win64", "", true)]
    [InlineData(RegistryArea.Software, @"Software\Uninstall\test", "InstallLocation", false)]
    [InlineData(RegistryArea.Shortcuts, @"Software\Classes\Applications\test", "FriendlyAppName", false)]
    public void MetadataAndSearchPathValues_AreNotTreatedAsMissingFiles(RegistryArea area, string key, string name, bool expected) =>
        CleanerRegistryScanner.IsReferenceField(area, key, name).Should().Be(expected);

    [Theory]
    [InlineData("C:\\Program Files\\app.exe -start", null)]
    [InlineData("\"C:\\Program Files\\app.exe\" -start", "C:\\Program Files\\app.exe")]
    [InlineData("C:\\app.exe -start", "C:\\app.exe")]
    [InlineData("C:\\app.com -start", "C:\\app.com")]
    [InlineData("\"C:\\Program Files\\app.exe\"bad", null)]
    [InlineData("\"C:\\Program Files\\app.exe", null)]
    [InlineData("app.exe --start", null)]
    [InlineData("rundll32.exe C:\\missing.dll,Entry", null)]
    [InlineData("C:\\app.dll,Entry", null)]
    [InlineData("C:\\directory\\file.txt", null)]
    [InlineData("C:\\Program.exe Files\\app.exe", null)]
    [InlineData("\\\\server\\app.exe --start", null)]
    [InlineData(" C:\\app.exe\t--start ", "C:\\app.exe")]
    public void AmbiguousUnquotedCommands_ArePreserved(string text, string? expected) =>
        CleanerRegistryScanner.ExtractCommandPath(text).Should().Be(expected);

    [Theory]
    [InlineData("C:\\folder.exe name\\component.dll", "C:\\folder.exe name\\component.dll")]
    [InlineData("C:\\app.exe,component.dll", "C:\\app.exe,component.dll")]
    [InlineData("\"C:\\Program Files\\component.dll\"", "C:\\Program Files\\component.dll")]
    [InlineData("\"C:\\component.dll\",1", null)]
    [InlineData("C:\\component.dll:stream", null)]
    [InlineData("C:\\%UNKNOWN%\\component.dll", null)]
    [InlineData("C:\\component*.dll", null)]
    [InlineData("C:\\component.dll\0", null)]
    [InlineData("C:\\component.dll\r\n", null)]
    [InlineData("\\\\?\\C:\\component.dll", null)]
    [InlineData("\\\\server\\share\\app.exe", null)]
    [InlineData("app.exe", null)]
    public void LiteralReferences_KeepCompletePathsAndRejectUnresolvedTargets(string text, string? expected) =>
        CleanerRegistryScanner.ExtractLocalPath(text).Should().Be(expected);

    [Theory]
    [InlineData("C:\\Program Files\\app.exe,-1", "C:\\Program Files\\app.exe")]
    [InlineData("\"C:\\Program Files\\app.exe\", 12", "C:\\Program Files\\app.exe")]
    [InlineData("@C:\\app.dll,-12", "C:\\app.dll")]
    [InlineData("C:\\app.exe,tools.dll,0", "C:\\app.exe,tools.dll")]
    [InlineData("%1", null)]
    [InlineData("C:\\icons.dll,2", "C:\\icons.dll")]
    [InlineData("\"C:\\app.dll\",bad", null)]
    public void IconReferences_SeparateOnlyNumericResourceIndexes(string text, string? expected) =>
        CleanerRegistryScanner.ExtractIconPath(text).Should().Be(expected);

    [Fact]
    public void OtherUserElevation_DoesNotExpandUnknownUserVariablesUsingAdministratorEnvironment()
    {
        var user = new Mock<IInteractiveUserService>();
        user.SetupGet(x => x.IsOtsElevation).Returns(true);
        user.Setup(x => x.GetInteractiveUserFolderPath(Environment.SpecialFolder.UserProfile)).Returns(@"D:\Users\Interactive");
        var scanner = new CleanerRegistryScanner(new TestCleanerRegistrySessions(user.Object, Mock.Of<ICleanerRegistryReader>(), Mock.Of<ICleanerRegistryEditor>()), Mock.Of<ICleanerBackupVault>());
        scanner.ExpandReferenceEnvironment(@"%USERPROFILE%\app.exe", RegistryView.Registry64).Should().Be(@"D:\Users\Interactive\app.exe");
        scanner.ExpandReferenceEnvironment(@"%PRIVATE_USER_VARIABLE%\app.exe", RegistryView.Registry64).Should().Be(@"%PRIVATE_USER_VARIABLE%\app.exe");
    }
}
