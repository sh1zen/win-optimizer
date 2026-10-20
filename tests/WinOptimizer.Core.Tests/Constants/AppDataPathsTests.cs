using Moq;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Interfaces;
using Xunit;

namespace WinOptimizer.Core.Tests.Constants;

/// <summary>Checks shared storage ownership and the boundaries of account-dependent paths.</summary>
public class AppDataPathsTests
{
    [Fact]
    public void SharedDataUsesOneProgramDataRoot()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "win-optimizer"), AppDataPaths.Root);
        foreach (var directory in new[] { AppDataPaths.Logs, AppDataPaths.History, AppDataPaths.Backups,
            AppDataPaths.CleanerBackups, AppDataPaths.EventLogBackups, AppDataPaths.PowerPlanBackups,
            AppDataPaths.IconCache, AppDataPaths.WinGetCache, AppDataPaths.Tools, AppDataPaths.Scripts,
            AppDataPaths.Downloads, AppDataPaths.Apps, AppDataPaths.Temp, AppDataPaths.State })
            Assert.StartsWith(AppDataPaths.Root + Path.DirectorySeparatorChar, directory);
    }

    [Fact]
    public void PersonalDataUsesTheInteractiveAccountUnderAlternateAccountElevation()
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        var user = new Mock<IInteractiveUserService>();
        user.SetupGet(service => service.InteractiveUserSid).Returns(sid);
        var root = Path.Combine(AppDataPaths.Root, "Users", sid);
        Assert.Equal(Path.Combine(root, "Config", "UserPreferences.json"), AppDataPaths.Preferences(user.Object));
        Assert.Equal(Path.Combine(root, "Backups", "Configurations"), AppDataPaths.ConfigurationBackups(user.Object));
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("S-1-5-21-111-222-333-1001\\outside")]
    public void AccountPathsRejectFilesystemInput(string sid) =>
        Assert.ThrowsAny<ArgumentException>(() => AppDataPaths.User(sid));

    [Theory]
    [InlineData("..")]
    [InlineData("..\\outside")]
    [InlineData("C:\\outside")]
    [InlineData("name:stream")]
    [InlineData("name.")]
    public void UntrustedNamesCannotEscapeTheirStorageDirectory(string name) =>
        Assert.Throws<ArgumentException>(() => AppDataPaths.Child(AppDataPaths.Apps, name));
}
