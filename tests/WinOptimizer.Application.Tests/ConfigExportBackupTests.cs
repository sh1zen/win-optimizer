using System.Text.Json;
using Xunit;
using Moq;
using WinOptimizer.Application.Configuration;
using WinOptimizer.Core.Features.AdvancedTools.Interfaces;
using WinOptimizer.Core.Features.Common.Constants;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;

namespace WinOptimizer.Application.Tests;

/// <summary>Exercises the production backup exporter with isolated identity, inventory and filesystem boundaries.</summary>
public sealed class ConfigExportBackupTests
{
    private const string AccountSid = "S-1-5-21-111-222-333-1001";
    private readonly Mock<ICompatibleSettingsRegistry> _registry = new();
    private readonly Mock<IFileSystemService> _files = new();
    private readonly Mock<ILogService> _log = new();
    private readonly ConfigExportService _export;

    public ConfigExportBackupTests()
    {
        _registry.SetupGet(registry => registry.IsInitialized).Returns(true);
        _registry.Setup(registry => registry.GetAllFilteredSettings()).Returns(new Dictionary<string, IEnumerable<SettingDefinition>>());
        var preloader = new Mock<IGlobalSettingsPreloader>();
        preloader.SetupGet(preloader => preloader.IsPreloaded).Returns(true);
        var apps = new Mock<IConfigurationAppSource>();
        apps.Setup(apps => apps.GetWindowsAppsAsync(true)).ReturnsAsync(new ConfigSection());
        apps.Setup(apps => apps.GetExternalAppsAsync()).ReturnsAsync(new ConfigSection());
        var account = new Mock<IInteractiveUserService>();
        account.SetupGet(account => account.InteractiveUserSid).Returns(AccountSid);
        _files.Setup(files => files.CombinePath(It.IsAny<string[]>())).Returns((string[] parts) => Path.Combine(parts));
        _export = new ConfigExportService(_log.Object, Mock.Of<IDialogService>(), Mock.Of<ILocalizationService>(),
            _registry.Object, preloader.Object, Mock.Of<ISystemSettingsDiscoveryService>(), account.Object, apps.Object,
            _files.Object, Mock.Of<IConfigurationFilePicker>(), Mock.Of<IApplicationModeService>(),
            Mock.Of<IAutounattendXmlGeneratorService>(), Mock.Of<IPowerPlanComboBoxService>());
    }

    [Fact]
    public async Task BackupCompletesOnlyAfterWritingInTheInteractiveAccountDirectory()
    {
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? path = null;
        string? contents = null;
        _files.Setup(files => files.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((target, json, _) => { path = target; contents = json; })
            .Returns(write.Task);
        var pending = _export.CreateUserBackupConfigAsync();
        try
        {
            Assert.False(pending.IsCompleted);
            var directory = Path.Combine(AppDataPaths.Root, "Users", AccountSid, "Backups", "Configurations");
            _files.Verify(files => files.CreateDirectory(directory), Times.Once);
            Assert.NotNull(path);
            Assert.Equal(directory, Path.GetDirectoryName(path));
            Assert.Matches(@"^UserBackup_[0-9]{8}_[0-9]{6}\.win-optimizer$", Path.GetFileName(path));
            Assert.NotNull(contents);
            using var json = JsonDocument.Parse(contents);
            Assert.Equal(ConfigFileConstants.CurrentVersion, json.RootElement.GetProperty("Version").GetString());
        }
        finally { write.TrySetResult(); }
        await pending;
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("directory")]
    [InlineData("write")]
    public async Task BackupLogsAndPropagatesTheOriginalFailure(string stage)
    {
        Exception failure = stage == "directory" ? new UnauthorizedAccessException("Backup directory denied.") : new IOException("Backup failed.");
        if (stage == "discovery") _registry.Setup(registry => registry.GetAllFilteredSettings()).Throws(failure);
        else if (stage == "directory") _files.Setup(files => files.CreateDirectory(It.IsAny<string>())).Throws(failure);
        else _files.Setup(files => files.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        Assert.Same(failure, await Record.ExceptionAsync(_export.CreateUserBackupConfigAsync));
        _log.Verify(log => log.Log(LogLevel.Error, It.Is<string>(message => message.Contains(failure.Message))), Times.Once);
    }
}
