using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Protects the management overview and its localized tool labels.</summary>
public sealed class ManagementViewModelTests
{
    [Fact]
    public void ManagementLabelsRefreshWithLanguageAndUnsubscribeOnDisposal()
    {
        var localization = new Mock<ILocalizationService>();
        var language = "en";
        localization.Setup(service => service.GetString(It.IsAny<string>())).Returns((string key) => language + ":" + key);
        using var vm = new ManagementViewModel(localization.Object, new ExtensionTestDispatcher());
        Assert.Equal("en:Nav_Management", vm.Heading);
        Assert.Equal("en:Management_Description", vm.Description);
        Assert.Equal("en:Utilities_Environment", vm.EnvironmentDisplayName);
        Assert.Equal("en:Utilities_EnvironmentDescription", vm.EnvironmentDescription);
        Assert.Equal("en:Utilities_Hosts", vm.HostsDisplayName);
        Assert.Equal("en:Utilities_HostsDescription", vm.HostsDescription);
        Assert.Equal("en:Utilities_Battery", vm.BatteryDisplayName);
        Assert.Equal("en:Utilities_BatteryDescription", vm.BatteryDescription);
        Assert.Equal("en:Utilities_Connection", vm.ConnectionDisplayName);
        Assert.Equal("en:Utilities_ConnectionDescription", vm.ConnectionDescription);
        Assert.Equal("en:WindowsStatus_Title", vm.WindowsStatusDisplayName);
        Assert.Equal("en:WindowsStatus_TitleDescription", vm.WindowsStatusDescription);
        Assert.Equal("en:Autoruns_Title", vm.AutorunsDisplayName);
        Assert.Equal("en:Autoruns_TitleDescription", vm.AutorunsDescription);
        Assert.Equal("en:ProcessMonitor_Title", vm.ProcessMonitorDisplayName);
        Assert.Equal("en:ProcessMonitor_TitleDescription", vm.ProcessMonitorDescription);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        language = "it";
        localization.Raise(service => service.LanguageChanged += null, EventArgs.Empty);
        Assert.Equal("it:Nav_Management", vm.Heading);
        Assert.Equal("it:Management_Description", vm.Description);
        Assert.Equal("it:Utilities_Environment", vm.EnvironmentDisplayName);
        Assert.Equal("it:Utilities_EnvironmentDescription", vm.EnvironmentDescription);
        Assert.Equal("it:Utilities_Hosts", vm.HostsDisplayName);
        Assert.Equal("it:Utilities_HostsDescription", vm.HostsDescription);
        Assert.Equal("it:Utilities_Battery", vm.BatteryDisplayName);
        Assert.Equal("it:Utilities_BatteryDescription", vm.BatteryDescription);
        Assert.Equal("it:Utilities_Connection", vm.ConnectionDisplayName);
        Assert.Equal("it:Utilities_ConnectionDescription", vm.ConnectionDescription);
        Assert.Equal("it:WindowsStatus_Title", vm.WindowsStatusDisplayName);
        Assert.Equal("it:WindowsStatus_TitleDescription", vm.WindowsStatusDescription);
        Assert.Equal("it:Autoruns_Title", vm.AutorunsDisplayName);
        Assert.Equal("it:Autoruns_TitleDescription", vm.AutorunsDescription);
        Assert.Equal("it:ProcessMonitor_Title", vm.ProcessMonitorDisplayName);
        Assert.Equal("it:ProcessMonitor_TitleDescription", vm.ProcessMonitorDescription);
        Assert.Equal(new[] { string.Empty }, notifications);
        vm.Dispose();
        localization.Raise(service => service.LanguageChanged += null, EventArgs.Empty);
        Assert.Single(notifications);
    }
}
