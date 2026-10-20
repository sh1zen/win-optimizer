using Microsoft.Win32;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Customize.Interfaces;
using WinOptimizer.Infrastructure.Features.Customize.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises production discovery and edits against a private disposable registry fixture.</summary>
public sealed class WindowsContextMenuStoreTests : IDisposable
{
    private readonly string _path = @"Software\WinOptimizer.Tests\ContextMenus\" + Guid.NewGuid().ToString("N");
    private readonly WindowsContextMenuStore _store;
    public WindowsContextMenuStoreTests()
    {
        using var root = Registry.CurrentUser.CreateSubKey(_path);
        _store = new(() => [new("Fixture", Registry.CurrentUser.OpenSubKey(_path)!)]);
    }

    private void Value(string path, string name, string value, RegistryValueKind kind = RegistryValueKind.String)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_path + "\\" + path);
        key.SetValue(name, value, kind);
    }

    [Fact]
    public void StaticTogglePreservesCommandAndOtherRegistrationsAcrossReload()
    {
        Value(@"*\shell\Alpha", "", "Open in Alpha");
        Value(@"*\shell\Alpha\command", "", "\"C:\\Fixture\\alpha.exe\" \"%1\"");
        Value(@"Directory\Background\shell\Beta\command", "", @"C:\Fixture\beta.exe %V");
        var entries = _store.Read();
        Assert.Equal(2, entries.Count);
        var alpha = Assert.Single(entries, item => item.ProgramName == "alpha");
        Assert.Equal(@"C:\Fixture\alpha.exe", alpha.ProgramPath);
        Assert.Equal("*", alpha.TargetType);
        var disabled = _store.SetEnabled(alpha, false);
        Assert.False(disabled.IsEnabled);
        Assert.True(Assert.Single(_store.Read(), item => item.ProgramName == "beta").IsEnabled);
        Assert.False(Assert.Single(_store.Read(), item => item.ProgramName == "alpha").IsEnabled);
        var enabled = _store.SetEnabled(disabled, true);
        Assert.True(enabled.IsEnabled);
        using var command = Registry.CurrentUser.OpenSubKey(_path + @"\*\shell\Alpha\command");
        Assert.Equal("\"C:\\Fixture\\alpha.exe\" \"%1\"", command!.GetValue(""));
    }

    [Fact]
    public void HandlerTogglePreservesClsidServerAndUnrelatedValues()
    {
        const string clsid = "{C64A470F-8B2D-4997-9002-F826AB744E29}";
        Value(@"Directory\shellex\ContextMenuHandlers\Alpha", "", clsid);
        Value(@"CLSID\" + clsid, "", "Alpha menu");
        Value(@"CLSID\" + clsid + @"\InprocServer32", "", @"C:\Fixture\alpha.dll");
        Value(@"Directory\shellex\ContextMenuHandlers\Alpha", "Other", "retain");
        var original = Assert.Single(_store.Read());
        Assert.Equal("Alpha menu", original.Name);
        Assert.Equal(@"C:\Fixture\alpha.dll", original.ProgramPath);
        Assert.Equal("Directory", original.TargetType);
        var disabled = _store.SetEnabled(original, false);
        Assert.False(disabled.IsEnabled);
        Assert.Equal(disabled, Assert.Single(_store.Read()));
        Assert.True(_store.SetEnabled(disabled, true).IsEnabled);
        using var key = Registry.CurrentUser.OpenSubKey(_path + @"\Directory\shellex\ContextMenuHandlers\Alpha");
        Assert.Equal(clsid, key!.GetValue(""));
        Assert.Equal("retain", key.GetValue("Other"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalChangesAreRejectedBeforeWriting(bool commandChanged)
    {
        Value(@"*\shell\Alpha\command", "", @"C:\Fixture\alpha.exe %1");
        var entry = Assert.Single(_store.Read());
        if (commandChanged) Value(@"*\shell\Alpha\command", "", @"C:\Fixture\changed.exe %1");
        else Value(@"*\shell\Alpha", "LegacyDisable", "external");
        Assert.Throws<InvalidOperationException>(() => _store.SetEnabled(entry, false));
    }

    [Fact]
    public void UserHandlerResolvesMachineClassAndTogglesOnlyItsOwnRegistration()
    {
        const string clsid = "{C64A470F-8B2D-4997-9002-F826AB744E29}";
        Value(@"User\*\shellex\ContextMenuHandlers\Alpha", "", clsid);
        Value(@"Machine\CLSID\" + clsid + @"\InprocServer32", "", @"C:\Fixture\alpha.dll");
        Value(@"Machine\Directory\shellex\ContextMenuHandlers\Alpha", "", clsid);
        var store = new WindowsContextMenuStore(() =>
        [
            new("User", Registry.CurrentUser.OpenSubKey(_path + @"\User")!),
            new("Machine (64-bit)", Registry.CurrentUser.OpenSubKey(_path + @"\Machine")!)
        ]);
        var entries = store.Read();
        Assert.Equal(2, entries.Count);
        store.SetEnabled(Assert.Single(entries, entry => entry.Location.StartsWith("User")), false);
        var refreshed = store.Read();
        Assert.False(Assert.Single(refreshed, entry => entry.Location.StartsWith("User")).IsEnabled);
        Assert.True(Assert.Single(refreshed, entry => entry.Location.StartsWith("Machine")).IsEnabled);
    }

    [Fact]
    public void FindsAssociationAndShiftVerbsWhileOmittingProgrammaticAndUnidentifiedCommands()
    {
        Value(@"SystemFileAssociations\.fixture\shell\Alpha\command", "", @"C:\Fixture\alpha.exe %1");
        Value(@"SystemFileAssociations\.fixture\shell\Alpha", "Extended", "");
        Value(@"*\shell\Hidden\command", "", @"C:\Fixture\hidden.exe %1");
        Value(@"*\shell\Hidden", "ProgrammaticAccessOnly", "");
        Value(@"*\shell\Unknown\command", "", "unknown-command %1");
        Assert.Equal("alpha", Assert.Single(_store.Read()).ProgramName);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Alpha\\alpha.exe\" \"%1\"", @"C:\Program Files\Alpha\alpha.exe")]
    [InlineData(@"C:\Program Files\Alpha\alpha.exe %1", @"C:\Program Files\Alpha\alpha.exe")]
    [InlineData(@"C:\Fixture\alpha.dll,0", @"C:\Fixture\alpha.dll")]
    [InlineData("alpha.exe %1", "")]
    [InlineData("\"unterminated", "")]
    public void ParsesTargetsWithoutExecutingThem(string command, string expected) =>
        Assert.Equal(expected, WindowsContextMenuStore.ExecutablePath(command));

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_path, false);
}

/// <summary>Checks inventory authority and application-mode guards with no Windows access.</summary>
public sealed class ContextMenuServiceTests
{
    private readonly Mock<IContextMenuStore> _store = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly ContextMenuEntry _entry = new("entry", "Alpha", "Open", "Fixture", true);

    public ContextMenuServiceTests()
    {
        _mode.Setup(mode => mode.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        _store.Setup(store => store.Read()).Returns([_entry]);
    }

    [Fact]
    public async Task AllowsKnownEntryAndTracksVerifiedStateForSubsequentEdits()
    {
        var disabled = _entry with { IsEnabled = false };
        _store.Setup(store => store.SetEnabled(_entry, false)).Returns(disabled);
        _store.Setup(store => store.SetEnabled(disabled, true)).Returns(_entry);
        var service = new ContextMenuService(_store.Object, _mode.Object);
        await service.ReadAsync();
        Assert.Equal(disabled, await service.SetEnabledAsync(_entry, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(_entry, true));
        Assert.Equal(_entry, await service.SetEnabledAsync(disabled, true));
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ReadsInAllModesButNeverWritesOutsideNormalMode(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        var service = new ContextMenuService(_store.Object, _mode.Object);
        Assert.Single(await service.ReadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(_entry, false));
        _store.Verify(store => store.SetEnabled(It.IsAny<ContextMenuEntry>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RejectsUnknownEntriesAndPropagatesRegistryFailuresWithoutChangingSnapshot()
    {
        var service = new ContextMenuService(_store.Object, _mode.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(_entry, false));
        await service.ReadAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEnabledAsync(_entry with { Id = "forged" }, false));
        _store.Setup(store => store.SetEnabled(_entry, false)).Throws(new UnauthorizedAccessException("Registry access denied."));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetEnabledAsync(_entry, false));
    }
}
