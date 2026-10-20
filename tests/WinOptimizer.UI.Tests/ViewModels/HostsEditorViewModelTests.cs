using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Verifies structured row edits use the existing draft, explicit save and revision-aware undo.</summary>
public sealed class HostsEditorViewModelTests
{
    private readonly Mock<IHostsEditorService> _service = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly HostsDocument _document = new("# header\r\n192.168.1.1 router.test alias.test # First floor\r\n# ::1 localhost\n", "original", "test/hosts");

    private HostsEditorViewModel Create()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(s => s.GetString(It.IsAny<string>())).Returns((string key) => key);
        _service.Setup(s => s.ReadAsync()).ReturnsAsync(_document);
        return new(_service.Object, _dialogs.Object, localization.Object, _mode.Object,
            new ExtensionTestDispatcher(), new Mock<ITaskProgressService>().Object);
    }

    [Fact]
    public async Task LoadAndViewSwitchesDoNotRewriteOrSaveTheDocument()
    {
        using var vm = Create(); await vm.LoadAsync();
        Assert.True(vm.ShowEntries); Assert.Equal(2, vm.Entries.Count);
        Assert.True(vm.Entries[0].IsEnabled); Assert.False(vm.Entries[1].IsEnabled);
        vm.EditAsText = true; vm.EditAsText = false; await vm.LoadAsync();
        Assert.Equal(_document.Content, vm.Content); Assert.False(vm.HasChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));
        _service.Verify(s => s.ReadAsync(), Times.Once);
        _service.Verify(s => s.SaveAsync(It.IsAny<HostsDocument>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ToggleRetainsRowObjectsAndSurroundingTextAndUpdatesOnlyTheDraft()
    {
        using var vm = Create(); await vm.LoadAsync();
        var row = vm.Entries[0]; var other = vm.Entries[1];
        row.IsEnabled = false;
        Assert.Equal(_document.Content.Replace("192.168.1.1", "# 192.168.1.1"), vm.Content);
        Assert.Same(row, vm.Entries[0]); Assert.Same(other, vm.Entries[1]);
        Assert.True(vm.HasChanges); Assert.True(vm.SaveCommand.CanExecute(null));
        row.IsEnabled = true;
        Assert.Equal(_document.Content, vm.Content); Assert.False(vm.HasChanges);
        _service.Verify(s => s.SaveAsync(It.IsAny<HostsDocument>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddEditAndRemovePreserveHeadersAndUseTheNormalSaveAndUndoContract()
    {
        using var vm = Create(); await vm.LoadAsync();
        var revision = 0;
        _service.Setup(s => s.SaveAsync(It.IsAny<HostsDocument>(), It.IsAny<string>()))
            .ReturnsAsync((HostsDocument original, string content) => new HostsSaveResult(original with
            { Content = content, Revision = "saved-" + ++revision }, "backup/hosts.bak"));
        vm.ApplyEntry(null, "0.0.0.0", "ads.test tracking.test", "Block ads", true);
        Assert.Equal(3, vm.Entries.Count); Assert.StartsWith(_document.Content, vm.Content);
        vm.ApplyEntry(vm.Entries[0], "192.168.1.2", "router.test", "Second floor", false);
        vm.RemoveEntryCommand.Execute(vm.Entries[1]);
        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal("# header\r\n# 192.168.1.2\trouter.test # Second floor\r\n0.0.0.0\tads.test tracking.test # Block ads\r\n", vm.Content);
        var edited = vm.Content;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.HasChanges); Assert.True(vm.UndoCommand.CanExecute(null));
        _service.Verify(s => s.SaveAsync(_document, edited), Times.Once);
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(_document.Content, vm.Content); Assert.Equal(2, vm.Entries.Count);
        Assert.True(vm.Entries[0].IsEnabled); Assert.False(vm.Entries[1].IsEnabled);
        _service.Verify(s => s.SaveAsync(It.Is<HostsDocument>(d => d.Revision == "saved-1"), _document.Content), Times.Once);
    }

    [Fact]
    public async Task RawEditsRebuildMappingsAndExposeInvalidActiveLinesWithoutLosingThem()
    {
        using var vm = Create(); await vm.LoadAsync();
        vm.EditAsText = true; vm.Content = "# retained\ninvalid mapping\n::1 local.test\n";
        Assert.Single(vm.Entries); Assert.Equal("::1", vm.Entries[0].Address); Assert.True(vm.HasInvalidLines);
        vm.EditAsText = false;
        vm.Entries[0].IsEnabled = false;
        Assert.Equal("# retained\ninvalid mapping\n# ::1 local.test\n", vm.Content);
        vm.Content = "# comments only\n";
        Assert.True(vm.IsEmpty); Assert.False(vm.HasInvalidLines);
    }

    [Fact]
    public async Task TextBoxLineEndingNormalizationDoesNotRewriteAnUnchangedDraft()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.EditAsText = true;
        vm.UpdateContentFromEditor(_document.Content.Replace("\r\n", "\r").Replace('\n', '\r'));
        Assert.Equal(_document.Content, vm.Content); Assert.False(vm.HasChanges);
        vm.UpdateContentFromEditor("# user edit\r127.0.0.1 local.test\r");
        Assert.True(vm.HasChanges); Assert.Equal("# user edit\r127.0.0.1 local.test\r", vm.Content);
    }

    [Fact]
    public async Task InvalidDialogInputCannotMutateTheDraftEvenWhenDisabled()
    {
        using var vm = Create(); await vm.LoadAsync();
        Assert.Throws<ArgumentException>(() => vm.ApplyEntry(null, "bad", "host.test", "", false));
        Assert.Equal(_document.Content, vm.Content); Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task ReloadRequiresConfirmationBeforeDiscardingStructuredChanges()
    {
        using var vm = Create(); await vm.LoadAsync(); vm.Entries[0].IsEnabled = false;
        var edited = vm.Content;
        _dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(edited, vm.Content); _service.Verify(s => s.ReadAsync(), Times.Once);
        _dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(_document.Content, vm.Content); Assert.False(vm.HasChanges); Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ReadOnlyModesDisableStructuredMutations(WinOptimizerMode mode)
    {
        using var vm = Create(); await vm.LoadAsync();
        _mode.SetupGet(m => m.CurrentMode).Returns(mode); _mode.Raise(m => m.ModeChanged += null, EventArgs.Empty);
        Assert.False(vm.CanEditEntries); Assert.All(vm.Entries, row => Assert.False(row.CanEdit));
        Assert.False(vm.RemoveEntryCommand.CanExecute(vm.Entries[0]));
        Assert.Throws<InvalidOperationException>(() => vm.ApplyEntry(null, "127.0.0.1", "test.local", "", true));
        vm.Entries[0].IsEnabled = false;
        Assert.True(vm.Entries[0].IsEnabled); Assert.Equal(_document.Content, vm.Content);
    }
}
