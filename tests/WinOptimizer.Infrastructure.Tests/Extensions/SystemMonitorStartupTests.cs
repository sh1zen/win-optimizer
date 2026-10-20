using System.Xml.Linq;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.Extensions;
using WinOptimizer.Infrastructure.Features.Common.Services;
using WinOptimizer.Infrastructure.Features.Extensions;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Extensions;

public sealed class SystemMonitorStartupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RegistrationUsesTheCurrentAccountAndBothChoices(bool enabled, bool startup)
    {
        var tasks = new Mock<IScheduledTaskService>();
        tasks.Setup(t => t.ConfigureUserLogonApplicationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(OperationResult.Succeeded());
        var service = new SystemMonitorStartupService(tasks.Object, Mock.Of<IInteractiveUserService>());
        Assert.True((await service.ApplyAsync(new() { Enabled = enabled, StartWithWindows = startup })).Success);
        tasks.Verify(t => t.ConfigureUserLogonApplicationAsync(It.Is<string>(name => name.StartsWith("SysMonitor-S-1-")),
            Environment.ProcessPath!, SystemMonitorLaunch.Argument, It.Is<string>(sid => sid.StartsWith("S-1-")), enabled && startup), Times.Once);
    }

    [Fact]
    public async Task DifferentAdministratorCredentialsCannotScheduleForAStandardDesktopAccount()
    {
        var tasks = new Mock<IScheduledTaskService>(MockBehavior.Strict);
        var interactive = Mock.Of<IInteractiveUserService>(u => u.IsOtsElevation == true);
        var result = await new SystemMonitorStartupService(tasks.Object, interactive).ApplyAsync(new() { Enabled = true, StartWithWindows = true });
        Assert.False(result.Success);
        Assert.Contains("administrator account", result.ErrorMessage);
        tasks.VerifyNoOtherCalls();
    }

    [Fact]
    public void TaskXmlEscapesPathsAndConfiguresInteractivePersistentBatteryFriendlyLaunch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sysmonitor-&-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "widget & monitor.exe");
            File.WriteAllText(executable, "");
            var document = XDocument.Parse(ScheduledTaskService.BuildUserLogonApplicationXml(executable, SystemMonitorLaunch.Argument, "S-1-5-21-1-2-3-1001"));
            string Value(string name) => Assert.Single(document.Descendants(), e => e.Name.LocalName == name).Value;
            Assert.Equal(executable, Value("Command"));
            Assert.Equal(SystemMonitorLaunch.Argument, Value("Arguments"));
            Assert.Equal(directory, Value("WorkingDirectory"));
            Assert.Equal("InteractiveToken", Value("LogonType"));
            Assert.Equal("HighestAvailable", Value("RunLevel"));
            Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
            Assert.Equal("false", Value("StopIfGoingOnBatteries"));
            Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
            Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
            Assert.All(document.Descendants().Where(e => e.Name.LocalName == "UserId"), e => Assert.Equal("S-1-5-21-1-2-3-1001", e.Value));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RelativeExecutableIsRejectedBeforeAccessingTaskScheduler() =>
        Assert.Throws<ArgumentException>(() => ScheduledTaskService.BuildUserLogonApplicationXml("widget.exe", "", "S-1-5-21-1-2-3-1001"));
}
