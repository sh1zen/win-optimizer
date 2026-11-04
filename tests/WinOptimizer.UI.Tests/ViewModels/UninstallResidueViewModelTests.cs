using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Checks tree identities, explicit confirmation and busy-review lifetime using the public review model.</summary>
public sealed class UninstallResidueViewModelTests
{
    private readonly Mock<IUninstallResidueSession> _session = new();
    private readonly Mock<IUninstallResidueService> _service = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<ILocalizationService> _text = new();
    public UninstallResidueViewModelTests()
    {
        _text.Setup(text => text.GetString(It.IsAny<string>())).Returns((string key) => key);
        _text.Setup(text => text.GetString(It.IsAny<string>(), It.IsAny<object[]>())).Returns((string key, object[] args) => key);
        _tasks.Setup(tasks => tasks.StartTask(It.IsAny<string>(), true)).Returns(() => new CancellationTokenSource());
    }
    private static UninstallResidue Finding(UninstallArtifactKind kind, string path, string? value = null, bool canDelete = true) =>
        new(Guid.NewGuid(), "app", "Recuva", "Account", kind, path, value, "Data", 8, DateTime.UtcNow, false, canDelete, "Reason");
    private async Task<UninstallResidueViewModel> CreateAsync(params UninstallResidue[] findings)
    {
        var verification = new AppUninstallResult([new("app", "Recuva", UninstallVerificationState.Removed, "Verified")], []);
        _session.Setup(session => session.ScanAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UninstallResidueScan(findings, verification.Applications, [], 2));
        var vm = new UninstallResidueViewModel(_session.Object, verification, _service.Object, _tasks.Object, _text.Object);
        await vm.ScanCommand.ExecuteAsync(null);
        return vm;
    }
    private static IEnumerable<UninstallResidueNode> Walk(IEnumerable<UninstallResidueNode> roots) => roots.SelectMany(root => new[] { root }.Concat(Walk(root.Children)));

    [Fact]
    public async Task StepsKeepSelectionsSeparate_AndCleanupPreservesTheOtherStepsChoices()
    {
        var file = Finding(UninstallArtifactKind.File, @"C:\Recuva\settings.ini");
        var registry = Finding(UninstallArtifactKind.RegistryValue, @"HKEY_USERS\account\Software\Recuva", "Options");
        var protectedRegistry = Finding(UninstallArtifactKind.RegistryKey, @"HKEY_USERS\account\Software\Shared", canDelete: false);
        using var vm = await CreateAsync(file, registry, protectedRegistry);
        Assert.False(vm.CanDelete);
        Walk(vm.FileRoots).Single(node => node.Finding?.Id == file.Id).IsSelected = true;
        vm.NextCommand.Execute(null);
        Walk(vm.RegistryRoots).Single(node => node.Finding?.Id == registry.Id).IsSelected = true;
        vm.PreviousCommand.Execute(null);
        vm.RequestDeleteCommand.Execute(null);
        _session.Setup(session => session.CleanAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(file.Id)),
            It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UninstallCleanupResult([file.Id], [], Guid.NewGuid()));

        await vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(vm.FileRoots);
        vm.NextCommand.Execute(null);
        Assert.True(vm.CanDelete);
        _session.Setup(session => session.CleanAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(registry.Id)),
            It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UninstallCleanupResult([registry.Id], [], Guid.NewGuid()));
        vm.RequestDeleteCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(protectedRegistry.Id, Assert.Single(Walk(vm.RegistryRoots), node => node.Finding != null).Finding!.Id);
        Assert.True(vm.CanRestore);
        _session.Verify(session => session.CleanAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PartialChildSelectionExcludesParent_AndProtectedResourcesRemainUnselected()
    {
        var parent = Finding(UninstallArtifactKind.Directory, @"C:\ProgramData\Recuva");
        var first = Finding(UninstallArtifactKind.File, parent.Path + @"\one");
        var second = Finding(UninstallArtifactKind.File, parent.Path + @"\two");
        var protectedFile = Finding(UninstallArtifactKind.File, parent.Path + @"\active", canDelete: false);
        using var vm = await CreateAsync(parent, first, second, protectedFile);
        Assert.All(Walk(vm.FileRoots), node => Assert.False(node.IsSelected));
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), vm.FileRoots.SelectMany(root => root.SelectedIds()).Order());
        var child = Walk(vm.FileRoots).Single(node => node.Finding?.Id == second.Id);
        child.IsSelected = false;
        Assert.Null(vm.FileRoots[0].IsSelected);
        Assert.Equal(first.Id, Assert.Single(vm.FileRoots[0].SelectedIds()));
    }

    [Fact]
    public async Task SameNamedKeyAndValueAndDefaultValuesKeepDistinctIdentities()
    {
        const string key = @"HKEY_USERS\account\Software\Recuva";
        var values = new[] { Finding(UninstallArtifactKind.RegistryKey, key + @"\child"),
            Finding(UninstallArtifactKind.RegistryValue, key, "child"), Finding(UninstallArtifactKind.RegistryValue, key, ""),
            Finding(UninstallArtifactKind.RegistryValue, key, "(Default)") };
        using var vm = await CreateAsync(values);
        vm.NextCommand.Execute(null);
        Assert.Equal(4, Walk(vm.RegistryRoots).Count(node => node.Finding != null));
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(values.Select(value => value.Id).Order(), vm.RegistryRoots.SelectMany(root => root.SelectedIds()).Order());
    }

    [Fact]
    public async Task SelectionRequiresConfirmation_AndOnlySelectedIdsReachService()
    {
        var first = Finding(UninstallArtifactKind.File, @"C:\Recuva\one");
        var second = Finding(UninstallArtifactKind.File, @"C:\Recuva\two");
        using var vm = await CreateAsync(first, second);
        Walk(vm.FileRoots).Single(node => node.Finding?.Id == first.Id).IsSelected = true;
        vm.RequestDeleteCommand.Execute(null);
        Assert.True(vm.IsConfirming);
        _session.Verify(session => session.CleanAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        var backup = Guid.NewGuid();
        _session.Setup(session => session.CleanAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(first.Id)),
            It.IsAny<IProgress<UninstallResidueProgress>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new UninstallCleanupResult([first.Id], [], backup));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanRestore);
        Assert.DoesNotContain(Walk(vm.FileRoots), node => node.Finding?.Id == first.Id);
        Assert.Contains(Walk(vm.FileRoots), node => node.Finding?.Id == second.Id);
        vm.RequestRestoreCommand.Execute(null);
        _service.Verify(service => service.RestoreAsync(backup, It.IsAny<CancellationToken>()), Times.Never);
        vm.CancelCommand.Execute(null);
        Assert.False(vm.IsConfirming);
    }

    [Fact]
    public async Task RescanBlocksCloseUntilAwaited_AndReplacesOldSelection()
    {
        var pending = new TaskCompletionSource<UninstallResidueScan>();
        using var vm = await CreateAsync(Finding(UninstallArtifactKind.File, @"C:\Recuva\old"));
        _session.Setup(session => session.ScanAsync(null, It.IsAny<CancellationToken>())).Returns(pending.Task);
        vm.SelectAllCommand.Execute(null);
        var running = vm.ScanCommand.ExecuteAsync(null);
        Assert.False(vm.CanClose);
        Assert.False(vm.CanDelete);
        var updated = Finding(UninstallArtifactKind.File, @"C:\Recuva\new");
        pending.SetResult(new([updated], [], [], 0));
        await running;
        Assert.True(vm.CanClose);
        Assert.False(vm.CanDelete);
        Assert.Equal(updated.Id, Assert.Single(Walk(vm.FileRoots), node => node.Finding != null).Finding!.Id);
        _tasks.Verify(tasks => tasks.CompleteTask(), Times.Exactly(2));
    }

    [Fact]
    public async Task ScanIsExplicit_AndFailureRequiresAnotherScanBeforeReview()
    {
        using var vm = new UninstallResidueViewModel(_session.Object, new([], []), _service.Object, _tasks.Object, _text.Object);
        Assert.True(vm.IsScanStep);
        Assert.Empty(vm.FileRoots);
        Assert.Empty(vm.RegistryRoots);
        Assert.False(vm.CanSelect);
        Assert.False(vm.CanNext);
        _session.VerifyNoOtherCalls();
        _session.Setup(session => session.ScanAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Scan failed"));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.IsScanStep);
        Assert.False(vm.HasResults);
        Assert.False(vm.CanDelete);
        Assert.Equal("Scan failed", vm.Status);
    }
}
