using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Utilities.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Verifies that the PATH presentation edits the production raw draft and save contract losslessly.</summary>
public sealed class EnvironmentEditorViewModelTests
{
    private readonly Mock<IEnvironmentEditorService> _service = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly EnvironmentVariableEntry _original = new("Path", @";%USERPROFILE%\bin;;C:\Tools;C:\Tools;", true);

    private EnvironmentEditorViewModel Create()
    {
        _service.Setup(service => service.ReadAsync(EnvironmentScope.User)).ReturnsAsync([_original]);
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => key);
        var vm = new EnvironmentEditorViewModel(_service.Object, new Mock<IDialogService>().Object,
            localization.Object, _mode.Object, new ExtensionTestDispatcher(), new Mock<ITaskProgressService>().Object);
        return vm;
    }

    [Fact]
    public async Task PathRowsAndRawTextRoundTripWithoutChangingReferencesDuplicatesOrEmptyEntries()
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = _original;
        Assert.True(vm.ShowPathEntries); Assert.False(vm.ShowRawValue);
        Assert.Equal(_original.Value.Split(';'), vm.PathEntries.Select(entry => entry.Value));
        Assert.False(vm.HasChanges); Assert.False(vm.SaveCommand.CanExecute(null));
        vm.EditPathAsText = true;
        Assert.True(vm.ShowRawValue); Assert.False(vm.ShowPathEntries);
        vm.EditPathAsText = false;
        Assert.Equal(_original.Value, vm.VariableValue);
        Assert.Equal(_original.Value.Split(';'), vm.PathEntries.Select(entry => entry.Value));
        _service.Verify(service => service.SaveAsync(It.IsAny<EnvironmentScope>(), It.IsAny<EnvironmentVariableEntry>(),
            It.IsAny<EnvironmentVariableEntry>()), Times.Never);
    }

    [Fact]
    public async Task InlineEditsAddRemoveAndReorderProduceTheExactSavedRawValue()
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = _original;
        var current = _original;
        _service.Setup(service => service.SaveAsync(EnvironmentScope.User, _original, It.IsAny<EnvironmentVariableEntry>()))
            .Callback<EnvironmentScope, EnvironmentVariableEntry?, EnvironmentVariableEntry>((_, _, entry) => current = entry)
            .ReturnsAsync(new EnvironmentEditResult(true));
        _service.Setup(service => service.ReadAsync(EnvironmentScope.User))
            .Returns(() => Task.FromResult<IReadOnlyList<EnvironmentVariableEntry>>([current]));

        vm.PathEntries[1].Value = @"%APPDATA%\bin";
        vm.AddPathEntryCommand.Execute(@"D:\More Tools");
        var added = vm.SelectedPathEntry;
        Assert.NotNull(added);
        vm.MovePathEntryUpCommand.Execute(null);
        Assert.Same(added, vm.SelectedPathEntry);
        Assert.Same(added, vm.PathEntries[^2]);
        vm.SelectedPathEntry = vm.PathEntries[3]; // Remove only one of two identical entries.
        vm.RemovePathEntryCommand.Execute(null);
        Assert.Equal(@";%APPDATA%\bin;;C:\Tools;D:\More Tools;", vm.VariableValue);
        Assert.True(vm.HasChanges); Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);
        _service.Verify(service => service.SaveAsync(EnvironmentScope.User, _original,
            It.Is<EnvironmentVariableEntry>(entry => entry.Value == @";%APPDATA%\bin;;C:\Tools;D:\More Tools;" && entry.Expandable)), Times.Once);
        Assert.False(vm.HasChanges); Assert.False(vm.SaveCommand.CanExecute(null)); Assert.False(vm.IsError);
    }

    [Fact]
    public async Task RawEditsAndNewPathNamesBuildRowsAndDetachSupersededRows()
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = _original;
        var detached = vm.PathEntries[1];
        vm.EditPathAsText = true;
        vm.VariableValue = @"C:\One;;%TOOLS%;";
        vm.EditPathAsText = false;
        Assert.Equal(new[] { @"C:\One", "", "%TOOLS%", "" }, vm.PathEntries.Select(entry => entry.Value));
        detached.Value = "old row";
        Assert.Equal(@"C:\One;;%TOOLS%;", vm.VariableValue);
        vm.NewCommand.Execute(null);
        vm.VariableValue = @"C:\First;C:\Second";
        vm.VariableName = "pAtH";
        Assert.True(vm.ShowPathEntries); Assert.Equal(2, vm.PathEntries.Count);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetRestoresRawValueAndTypeWhileOrdinaryVariablesRemainFreeText()
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = _original;
        vm.PathEntries[0].Value = @"C:\Changed"; vm.Expandable = false;
        vm.ResetDraftCommand.Execute(null);
        Assert.Equal(_original.Value, vm.VariableValue); Assert.True(vm.Expandable); Assert.False(vm.HasChanges);
        vm.SelectedVariable = new EnvironmentVariableEntry("OTHER", "a;b\r\nc", false);
        Assert.False(vm.IsPath); Assert.True(vm.ShowRawValue); Assert.Empty(vm.PathEntries);
        Assert.Equal("a;b\r\nc", vm.VariableValue);
        vm.VariableValue = "new"; vm.Expandable = true; vm.ResetDraftCommand.Execute(null);
        Assert.Equal("a;b\r\nc", vm.VariableValue); Assert.False(vm.Expandable);
    }

    [Fact]
    public async Task EmptyPathSupportsAddingEditingAndRemovingTheLastRow()
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = new EnvironmentVariableEntry("PATH", "", false);
        Assert.True(vm.ShowPathEmpty); Assert.False(vm.RemovePathEntryCommand.CanExecute(null));
        vm.AddPathEntryCommand.Execute(null);
        Assert.Single(vm.PathEntries); Assert.Same(vm.PathEntries[0], vm.SelectedPathEntry);
        Assert.False(vm.MovePathEntryUpCommand.CanExecute(null)); Assert.False(vm.MovePathEntryDownCommand.CanExecute(null));
        vm.PathEntries[0].Value = @"C:\Only";
        Assert.Equal(@"C:\Only", vm.VariableValue);
        vm.RemovePathEntryCommand.Execute(null);
        Assert.Empty(vm.PathEntries); Assert.Null(vm.SelectedPathEntry); Assert.Equal("", vm.VariableValue);
        Assert.True(vm.ShowPathEmpty); Assert.False(vm.HasChanges);
    }

    [Fact]
    public async Task MoveAvailabilityTracksSelectionBoundariesAndRawMode()
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = _original;
        Assert.False(vm.MovePathEntryUpCommand.CanExecute(null));
        vm.SelectedPathEntry = vm.PathEntries[0];
        Assert.False(vm.MovePathEntryUpCommand.CanExecute(null)); Assert.True(vm.MovePathEntryDownCommand.CanExecute(null));
        vm.MovePathEntryDownCommand.Execute(null);
        Assert.True(vm.MovePathEntryUpCommand.CanExecute(null));
        vm.SelectedPathEntry = vm.PathEntries[^1];
        Assert.False(vm.MovePathEntryDownCommand.CanExecute(null));
        vm.EditPathAsText = true;
        Assert.False(vm.AddPathEntryCommand.CanExecute(null)); Assert.False(vm.RemovePathEntryCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task PathMutationCommandsAreUnavailableOutsideNormalMode(WinOptimizerMode mode)
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = _original; vm.SelectedPathEntry = vm.PathEntries[1];
        _mode.SetupGet(service => service.CurrentMode).Returns(mode);
        _mode.Raise(service => service.ModeChanged += null, EventArgs.Empty);
        Assert.False(vm.CanWrite); Assert.False(vm.AddPathEntryCommand.CanExecute(null));
        Assert.False(vm.RemovePathEntryCommand.CanExecute(null)); Assert.False(vm.MovePathEntryUpCommand.CanExecute(null));
        Assert.False(vm.MovePathEntryDownCommand.CanExecute(null)); Assert.False(vm.ResetDraftCommand.CanExecute(null));
    }

    [Fact]
    public async Task FilteringRetainsTheDraftAndOriginalSaveTarget()
    {
        using var vm = Create();
        await vm.LoadAsync(); vm.SelectedVariable = _original;
        vm.PathEntries[1].Value = "%TOOLS%";
        vm.Query = "unmatched";
        Assert.Empty(vm.Variables); Assert.Same(_original, vm.SelectedVariable);
        Assert.True(vm.HasChanges); Assert.False(vm.CanChangeName);
        vm.Query = "path";
        Assert.Single(vm.Variables); Assert.Equal("%TOOLS%", vm.PathEntries[1].Value);
    }
}
