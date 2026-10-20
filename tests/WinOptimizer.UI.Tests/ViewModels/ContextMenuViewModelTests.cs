using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Customize.Interfaces;
using WinOptimizer.UI.Features.Customize.ViewModels;
using WinOptimizer.UI.Features.Customize.Interfaces;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks grouping and verified toggle presentation using fake Windows boundaries.</summary>
public sealed class ContextMenuViewModelTests
{
    private readonly Mock<IContextMenuService> _service = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<IProgramIconSource> _icons = new();
    private readonly ContextMenuEntry _single = new("a", "Alpha", "Open with Alpha", "User\\*\\shell\\Alpha", true);
    private ContextMenuViewModel Create()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key switch
        {
            "ContextMenu_ResultSummary" => "{0} of {1} programs · {2} entries",
            "ContextMenu_EntrySummary" => "{0} entries · {1} enabled",
            "ContextMenu_ProgramChangeFailed" => "Could not change all entries for {0}:\n{1}",
            _ => key,
        });
        return new(_service.Object, localization.Object, _mode.Object, new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>(), _icons.Object);
    }

    [Fact]
    public async Task GroupsSinglesAndMultipleEntriesWithoutLosingGroupShapeWhenSearching()
    {
        var second = _single with { Id = "b", ProgramName = "Beta", Name = "Compress file", Location = "User\\*\\shell\\Beta" };
        var third = second with { Id = "c", Name = "Compress folder", Location = "User\\Directory\\shell\\Beta", IsEnabled = false };
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single, second, third]);
        using var vm = Create();
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Programs.Count);
        Assert.True(vm.Programs[0].IsSingle); Assert.False(vm.Programs[0].IsMultiple);
        Assert.Same(_single, vm.Programs[0].First.Entry);
        Assert.True(vm.Programs[1].IsMultiple);
        Assert.Equal(new[] { second, third }, vm.Programs[1].Entries.Select(entry => entry.Entry));
        vm.Query = "Compress file";
        Assert.Equal(2, Assert.Single(vm.Programs).Entries.Count);
        vm.Query = "missing";
        Assert.Empty(vm.Programs); Assert.True(vm.ShowEmpty);
        vm.Query = "Alpha";
        Assert.True(Assert.Single(vm.Programs).IsSingle);
        _service.Verify(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ReflectsOnlyVerifiedStateAndExposesFailuresWithoutChangingTheToggle()
    {
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single]);
        var completion = new TaskCompletionSource<ContextMenuEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(service => service.SetEnabledAsync(_single, false)).Returns(completion.Task);
        using var vm = Create();
        await vm.RefreshAsync();
        var row = vm.Programs[0].First;
        var pending = vm.SetEnabledAsync(row, false);
        Assert.True(row.IsEnabled); Assert.True(vm.IsBusy); Assert.False(vm.CanWrite); Assert.False(row.CanWrite);
        Assert.False(vm.WaitForWriteAsync().IsCompleted);
        completion.SetResult(_single with { IsEnabled = false });
        await pending;
        Assert.False(row.IsEnabled); Assert.False(vm.IsError); Assert.True(row.CanWrite);
        Assert.True(vm.WaitForWriteAsync().IsCompleted);
        _service.Setup(service => service.SetEnabledAsync(row.Entry, true)).ThrowsAsync(new UnauthorizedAccessException("Access denied"));
        await vm.SetEnabledAsync(row, true);
        Assert.False(row.IsEnabled); Assert.True(vm.IsError);
        Assert.Equal("Access denied", vm.Status);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ReadsInTestAndReviewModesWithWritesDisabled(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single]);
        using var vm = Create();
        await vm.RefreshAsync();
        Assert.Single(vm.Programs); Assert.False(vm.CanWrite); Assert.True(vm.ShowModeNotice);
        Assert.False(vm.Programs[0].First.CanWrite);
        await vm.SetEnabledAsync(vm.Programs[0].First, false);
        _service.Verify(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RefreshReplacesInventoryAndPreservesSearch()
    {
        _service.SetupSequence(service => service.ReadAsync()).ReturnsAsync([_single])
            .ReturnsAsync([_single with { IsEnabled = false }]);
        using var vm = Create();
        vm.Query = "Alpha";
        await vm.RefreshAsync(); await vm.RefreshAsync();
        Assert.Equal("Alpha", vm.Query);
        Assert.False(Assert.Single(vm.Programs).First.IsEnabled);
    }

    [Fact]
    public async Task SearchRetainsProgramIdentityExpansionAndIconWorkAndRevealsMatchingEntries()
    {
        var first = _single with { ProgramPath = @"C:\Fixture\alpha.exe" };
        var second = first with { Id = "b", Name = "Compress files" };
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([first, second]);
        _icons.Setup(icons => icons.LoadAsync(first.ProgramPath!)).ReturnsAsync((Microsoft.UI.Xaml.Media.ImageSource?)null);
        using var vm = Create();
        Assert.False(vm.ShowEmpty);
        await vm.RefreshAsync();
        var program = Assert.Single(vm.Programs);
        Assert.True(program.ShowFallbackIcon); Assert.False(program.IsExpanded);
        Assert.Equal("1 of 1 programs · 2 entries", vm.ResultSummary);
        vm.Query = "Compress";
        Assert.Same(program, Assert.Single(vm.Programs)); Assert.True(program.IsExpanded);
        vm.Query = "not found";
        Assert.True(vm.ShowEmpty);
        vm.Query = "";
        Assert.Same(program, Assert.Single(vm.Programs)); Assert.True(program.IsExpanded);
        _icons.Verify(icons => icons.LoadAsync(first.ProgramPath!), Times.Once);
    }

    [Fact]
    public async Task EnabledSummaryTracksVerifiedTogglesAndCaptionsKeepRegistryDetailsAvailable()
    {
        var first = _single with { TargetType = @"directory\background", IsMachineWide = true };
        var second = first with { Id = "b", IsEnabled = false };
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([first, second]);
        _service.Setup(service => service.SetEnabledAsync(second, true)).ReturnsAsync(second with { IsEnabled = true });
        using var vm = Create();
        await vm.RefreshAsync();
        var program = Assert.Single(vm.Programs);
        Assert.Equal("2 entries · 1 enabled", program.Summary);
        Assert.Contains("ContextMenu_TargetFolderBackground", program.First.ContextLabel);
        Assert.Contains("ContextMenu_ScopeMachine", program.First.ContextLabel);
        Assert.Contains(first.Location, program.First.Details);
        await vm.SetEnabledAsync(program.Entries[1], true);
        Assert.Equal("2 entries · 2 enabled", program.Summary);
    }

    [Fact]
    public async Task ProgramToggleChangesEveryEntryDespiteSearchAndSkipsMatchingStates()
    {
        var second = _single with { Id = "b", Name = "Open folder" };
        var disabled = _single with { Id = "c", Name = "Open archive", IsEnabled = false };
        var unrelated = _single with { Id = "other", ProgramName = "Beta", Name = "Unrelated action" };
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single, second, disabled, unrelated]);
        _service.Setup(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), It.IsAny<bool>()))
            .ReturnsAsync((ContextMenuEntry entry, bool enabled) => entry with { IsEnabled = enabled });
        using var vm = Create(); await vm.RefreshAsync();
        vm.Query = "Open with Alpha";
        var program = Assert.Single(vm.Programs);
        Assert.True(program.HasEnabledEntries); Assert.True(program.CanWrite);
        await vm.SetProgramEnabledAsync(program, false);
        Assert.False(program.HasEnabledEntries); Assert.Equal(0, program.EnabledCount); Assert.True(program.IsExpanded);
        _service.Verify(service => service.SetEnabledAsync(disabled, false), Times.Never);
        _service.Verify(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), false), Times.Exactly(2));
        await vm.SetProgramEnabledAsync(program, true);
        Assert.True(program.HasEnabledEntries); Assert.Equal(3, program.EnabledCount);
        _service.Verify(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), true), Times.Exactly(3));
        _service.Verify(service => service.SetEnabledAsync(unrelated, It.IsAny<bool>()), Times.Never);
        Assert.False(vm.IsError);
    }

    [Fact]
    public async Task ProgramToggleContinuesAfterFailuresAndRetainsVerifiedPartialState()
    {
        var restricted = _single with { Id = "b", Name = "Restricted entry", Location = "Machine\\*\\shell\\Alpha" };
        var last = _single with { Id = "c", Name = "Last entry" };
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single, restricted, last]);
        _service.Setup(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), false))
            .ReturnsAsync((ContextMenuEntry entry, bool _) => entry with { IsEnabled = false });
        _service.Setup(service => service.SetEnabledAsync(restricted, false)).ThrowsAsync(new IOException("Access denied"));
        using var vm = Create(); await vm.RefreshAsync();
        var program = Assert.Single(vm.Programs);
        await vm.SetProgramEnabledAsync(program, false);
        Assert.Equal(new[] { false, true, false }, program.Entries.Select(entry => entry.IsEnabled));
        Assert.Equal(1, program.EnabledCount); Assert.True(program.HasEnabledEntries);
        Assert.True(vm.IsError); Assert.Contains("Restricted entry", vm.Status); Assert.Contains("Access denied", vm.Status);
        Assert.Contains(restricted.Location, vm.Status);
        _service.Verify(service => service.SetEnabledAsync(last, false), Times.Once);
    }

    [Fact]
    public async Task ProgramToggleDisablesControlsAndDrainsAllWritesBeforeShutdown()
    {
        var second = _single with { Id = "b" };
        var pending = new TaskCompletionSource<ContextMenuEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single, second]);
        _service.Setup(service => service.SetEnabledAsync(second, false)).ReturnsAsync(second with { IsEnabled = false });
        _service.Setup(service => service.SetEnabledAsync(_single, false)).Returns(pending.Task);
        using var vm = Create(); await vm.RefreshAsync();
        var program = Assert.Single(vm.Programs);
        var change = vm.SetProgramEnabledAsync(program, false);
        Assert.True(vm.IsBusy); Assert.False(program.CanWrite); Assert.False(vm.WaitForWriteAsync().IsCompleted);
        Assert.All(program.Entries, entry => { Assert.True(entry.IsEnabled); Assert.False(entry.CanWrite); });
        pending.SetResult(_single with { IsEnabled = false });
        await change; await vm.WaitForWriteAsync();
        Assert.False(program.HasEnabledEntries); Assert.True(program.CanWrite); Assert.True(vm.IsIdle);
        _service.Verify(service => service.SetEnabledAsync(second, false), Times.Once);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ProgramToggleRemainsReadOnlyOutsideNormalMode(WinOptimizerMode mode)
    {
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single, _single with { Id = "b" }]);
        using var vm = Create(); await vm.RefreshAsync();
        var program = Assert.Single(vm.Programs);
        Assert.False(program.CanWrite);
        await vm.SetProgramEnabledAsync(program, false);
        Assert.True(program.HasEnabledEntries); Assert.Equal(2, program.EnabledCount);
        _service.Verify(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task IndividualTogglesUpdateTheProgramSwitchFromMixedToFullyDisabled()
    {
        var second = _single with { Id = "b" };
        _service.Setup(service => service.ReadAsync()).ReturnsAsync([_single, second]);
        _service.Setup(service => service.SetEnabledAsync(It.IsAny<ContextMenuEntry>(), false))
            .ReturnsAsync((ContextMenuEntry entry, bool _) => entry with { IsEnabled = false });
        using var vm = Create(); await vm.RefreshAsync();
        var program = Assert.Single(vm.Programs);
        var notifications = 0;
        program.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(program.HasEnabledEntries)) notifications++; };
        await vm.SetEnabledAsync(program.Entries[0], false); Assert.True(program.HasEnabledEntries);
        await vm.SetEnabledAsync(program.Entries[1], false); Assert.False(program.HasEnabledEntries);
        Assert.Equal(2, notifications);
    }
}
