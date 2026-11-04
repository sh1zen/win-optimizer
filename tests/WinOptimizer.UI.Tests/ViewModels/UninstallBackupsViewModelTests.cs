using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Exercises recovery against the real shared task lifecycle with a fake resource restorer.</summary>
public sealed class UninstallBackupsViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryNeverCancelsAnOperationThatStartsBeforeConfirmation(bool afterRequest)
    {
        var tasks = new TaskProgressService(Mock.Of<ILogService>());
        var service = new Mock<IUninstallResidueService>();
        var text = new Mock<ILocalizationService>();
        text.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key);
        using var vm = new UninstallBackupsViewModel(service.Object, tasks, text.Object, new ExtensionTestDispatcher())
        { SelectedItem = new(new(Guid.NewGuid(), DateTime.UtcNow, "Example", 1)) };
        if (afterRequest) vm.RequestRestoreCommand.Execute(null);
        var owner = tasks.StartTask("Extraction");
        try
        {
            Assert.False(vm.RequestRestoreCommand.CanExecute(null));
            vm.RequestRestoreCommand.Execute(null);
            await vm.ConfirmCommand.ExecuteAsync(null);
            Assert.True(tasks.IsTaskRunning); Assert.Same(owner, tasks.CurrentTaskCancellationSource);
            Assert.False(owner.IsCancellationRequested);
            Assert.Equal("Cleaner_Wait", vm.Status);
            service.VerifyNoOtherCalls();
        }
        finally { tasks.CompleteTask(); }
        Assert.Equal(afterRequest, vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecoveryFinishesOnlyItsOwnTaskAndPreservesAReplacement()
    {
        var tasks = new TaskProgressService(Mock.Of<ILogService>());
        var service = new Mock<IUninstallResidueService>();
        var pending = new TaskCompletionSource<UninstallRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Setup(source => source.RestoreAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(pending.Task);
        var text = new Mock<ILocalizationService>();
        text.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key);
        using var vm = new UninstallBackupsViewModel(service.Object, tasks, text.Object, new ExtensionTestDispatcher())
        { SelectedItem = new(new(Guid.NewGuid(), DateTime.UtcNow, "Example", 1)) };
        vm.RequestRestoreCommand.Execute(null);
        var operation = vm.ConfirmCommand.ExecuteAsync(null);
        var replacement = tasks.StartTask("New task");
        pending.SetResult(new(1, [])); await operation;
        Assert.True(tasks.IsTaskRunning); Assert.Same(replacement, tasks.CurrentTaskCancellationSource);
        Assert.False(replacement.IsCancellationRequested);
        tasks.CompleteTask();
        Assert.True(vm.RequestRestoreCommand.CanExecute(null));
    }
}
