using System.Runtime.InteropServices;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Infrastructure.Features.Common.Services;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Services;

/// <summary>Exercises startup reconciliation without connecting to Windows Task Scheduler.</summary>
public sealed class ScheduledTaskStartupTests
{
    [Theory]
    [InlineData(true, false, unchecked((int)0x80070002))]
    [InlineData(true, false, unchecked((int)0x80070003))]
    [InlineData(true, true, unchecked((int)0x80070002))]
    [InlineData(true, true, unchecked((int)0x80070003))]
    [InlineData(false, false, unchecked((int)0x80070002))]
    [InlineData(false, false, unchecked((int)0x80070003))]
    [InlineData(false, true, unchecked((int)0x80070002))]
    [InlineData(false, true, unchecked((int)0x80070003))]
    public async Task DisablingAbsentStartupRegistrationSucceeds(bool missingFolder, bool comException, int hresult)
    {
        var error = comException ? new COMException("Missing scheduler resource", hresult)
            : Marshal.GetExceptionForHR(hresult)!;
        var folder = new StartupFolder { DeleteError = missingFolder ? null : error };
        var session = new StartupSession(folder) { FolderError = missingFolder ? error : null };
        var log = new Mock<ILogService>(MockBehavior.Strict);
        var service = new ScheduledTaskService(log.Object, Mock.Of<IFileSystemService>(), () => session);

        var result = await service.ConfigureUserLogonApplicationAsync("SysMonitor-test", "", "", "", false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\\win-optimizer", session.RequestedFolder);
        Assert.Equal(missingFolder ? null : "SysMonitor-test", folder.DeletedTask);
        log.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DisablingExistingStartupRegistrationDeletesOnlyTheRequestedTask()
    {
        var folder = new StartupFolder();
        var session = new StartupSession(folder);
        var service = new ScheduledTaskService(Mock.Of<ILogService>(), Mock.Of<IFileSystemService>(), () => session);

        var result = await service.ConfigureUserLogonApplicationAsync("SysMonitor-test", "", "", "", false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("\\win-optimizer", session.RequestedFolder);
        Assert.Equal("SysMonitor-test", folder.DeletedTask);
    }

    [Theory]
    [InlineData(true, unchecked((int)0x80070005))]
    [InlineData(false, unchecked((int)0x80070005))]
    [InlineData(true, unchecked((int)0x8007001F))]
    [InlineData(false, unchecked((int)0x8007001F))]
    public async Task OtherSchedulerFailuresRemainVisible(bool folderFailure, int hresult)
    {
        var error = Marshal.GetExceptionForHR(hresult)!;
        var folder = new StartupFolder { DeleteError = folderFailure ? null : error };
        var session = new StartupSession(folder) { FolderError = folderFailure ? error : null };
        var log = new Mock<ILogService>();
        var service = new ScheduledTaskService(log.Object, Mock.Of<IFileSystemService>(), () => session);

        var result = await service.ConfigureUserLogonApplicationAsync("SysMonitor-test", "", "", "", false);

        Assert.False(result.Success);
        Assert.Contains("Unable to configure Windows startup", result.ErrorMessage);
        log.Verify(l => l.LogError(It.IsAny<string>(), error), Times.Once);
    }

    [Fact]
    public async Task MissingSchedulerDuringConnectionIsReportedAsFailure()
    {
        var error = new FileNotFoundException("Scheduler unavailable");
        var service = new ScheduledTaskService(Mock.Of<ILogService>(), Mock.Of<IFileSystemService>(), () => throw error);

        var result = await service.ConfigureUserLogonApplicationAsync("SysMonitor-test", "", "", "", false);

        Assert.False(result.Success);
        Assert.Contains(error.Message, result.ErrorMessage);
    }

    /// <summary>Represents the connected COM session; folder lookup is the only operation used when disabled.</summary>
    public sealed class StartupSession(StartupFolder folder)
    {
        public Exception? FolderError { get; init; }
        public string? RequestedFolder { get; private set; }

        public StartupFolder GetFolder(string path)
        {
            RequestedFolder = path;
            if (FolderError is not null) throw FolderError;
            return folder;
        }
    }

    /// <summary>Records the exact deletion target and can reproduce native or managed scheduler errors.</summary>
    public sealed class StartupFolder
    {
        public Exception? DeleteError { get; init; }
        public string? DeletedTask { get; private set; }

        public void DeleteTask(string taskName, int flags)
        {
            Assert.Equal(0, flags);
            DeletedTask = taskName;
            if (DeleteError is not null) throw DeleteError;
        }
    }
}
