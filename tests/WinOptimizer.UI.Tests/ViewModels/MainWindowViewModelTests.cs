using FluentAssertions;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.ViewModels;
using WinOptimizer.UI.Features.Settings.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class MainWindowViewModelTests : IDisposable
{
    private readonly Mock<IConfigurationService> _mockConfigurationService = new();
    private readonly Mock<ILocalizationService> _mockLocalizationService = new();
    private readonly Mock<IVersionService> _mockVersionService = new();
    private readonly Mock<ILogService> _mockLogService = new();
    private readonly Mock<IInteractiveUserService> _mockInteractiveUserService = new();

    // Child ViewModel dependencies
    private readonly Mock<ITaskProgressService> _mockTaskProgressService = new();
    private readonly Mock<IDispatcherService> _mockDispatcherService = new();
    private readonly Mock<IDialogService> _mockDialogService = new();
    private readonly Mock<IApplicationModeService> _mockApplicationModeService = new();
    private readonly Mock<IConfigExportService> _mockConfigExportService = new();
    private readonly Mock<IConfigApplicationExecutionService> _mockConfigExecutionService = new();
    private readonly Mock<IUserPreferencesService> _mockUserPreferencesService = new();

    private readonly TaskProgressViewModel _taskProgressViewModel;
    private readonly UpdateCheckViewModel _updateCheckViewModel;
    private readonly SidebarPreferencesViewModel _sidebarPreferences;

    public MainWindowViewModelTests()
    {
        _mockDialogService.Setup(d => d.ShowTestModeExitDialogAsync())
            .ReturnsAsync(WinOptimizer.Core.Features.Common.Enums.TestModeExitAction.Discard);
        // Set up dispatcher to execute actions synchronously
        _mockDispatcherService
            .Setup(d => d.RunOnUIThread(It.IsAny<Action>()))
            .Callback<Action>(a => a());
        _mockDispatcherService
            .Setup(d => d.RunOnUIThreadAsync(It.IsAny<Func<Task>>()))
            .Returns<Func<Task>>(f => f());

        // Default localization returns null so fallbacks are used
        _mockLocalizationService
            .Setup(l => l.GetString(It.IsAny<string>()))
            .Returns((string key) => null!);

        // Default version info
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        // Default interactive user service (not OTS)
        _mockInteractiveUserService
            .Setup(i => i.IsOtsElevation)
            .Returns(false);

        // Create child ViewModels
        _taskProgressViewModel = new TaskProgressViewModel(
            _mockTaskProgressService.Object,
            _mockDispatcherService.Object,
            _mockDialogService.Object,
            _mockLocalizationService.Object,
            _mockLogService.Object);

        _updateCheckViewModel = new UpdateCheckViewModel(
            _mockVersionService.Object,
            _mockLocalizationService.Object,
            _mockLogService.Object,
            Mock.Of<IBackgroundUpdateCheckService>(),
            _mockDispatcherService.Object);

        _sidebarPreferences = new(_mockUserPreferencesService.Object, _mockLocalizationService.Object);
    }

    private MainWindowViewModel CreateSut()
    {
        return new MainWindowViewModel(
            _mockConfigurationService.Object,
            _mockLocalizationService.Object,
            _mockVersionService.Object,
            _mockLogService.Object,
            _mockInteractiveUserService.Object,
            _taskProgressViewModel,
            _updateCheckViewModel,
            _mockDialogService.Object,
            _mockApplicationModeService.Object, _mockDispatcherService.Object,
            _mockConfigExportService.Object, _mockConfigExecutionService.Object,
            _sidebarPreferences);
    }

    public void Dispose()
    {
        _taskProgressViewModel.Dispose();
        _updateCheckViewModel.Dispose();
        _sidebarPreferences.Dispose();
    }

    [Fact]
    public void Initialize_SetsVersionInfo()
    {
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Returns(new VersionInfo { Version = "v25.05.01" });

        var sut = CreateSut();
        sut.Initialize();

        sut.VersionInfo.Should().Be("win-optimizer v25.05.01");
    }

    [Fact]
    public void Initialize_VersionServiceThrows_FallsBackToDefault()
    {
        _mockVersionService
            .Setup(v => v.GetCurrentVersion())
            .Throws(new Exception("Version unavailable"));

        var sut = CreateSut();
        sut.Initialize();

        sut.VersionInfo.Should().Be("win-optimizer");
    }

    [Fact]
    public void Initialize_WhenOtsElevation_ShowsInfoBar()
    {
        _mockInteractiveUserService
            .Setup(i => i.IsOtsElevation)
            .Returns(true);
        _mockInteractiveUserService
            .Setup(i => i.InteractiveUserName)
            .Returns("Standard");

        var sut = CreateSut();
        sut.Initialize();

        sut.IsOtsInfoBarOpen.Should().BeTrue();
        sut.OtsInfoBarTitle.Should().NotBeNullOrEmpty();
        sut.OtsInfoBarMessage.Should().Contain("Standard");
    }

    [Fact]
    public void Initialize_WhenNotOtsElevation_DoesNotShowInfoBar()
    {
        var sut = CreateSut();
        sut.Initialize();

        sut.IsOtsInfoBarOpen.Should().BeFalse();
    }

    [Fact]
    public void DismissOtsInfoBar_SetsIsOtsInfoBarOpenToFalse()
    {
        _mockInteractiveUserService
            .Setup(i => i.IsOtsElevation)
            .Returns(true);
        _mockInteractiveUserService
            .Setup(i => i.InteractiveUserName)
            .Returns("User");

        var sut = CreateSut();
        sut.Initialize();
        sut.IsOtsInfoBarOpen.Should().BeTrue();

        sut.DismissOtsInfoBar();

        sut.IsOtsInfoBarOpen.Should().BeFalse();
    }

    [Fact]
    public void ConstructorSharesTheSidebarPreferenceWithoutLoadingIt()
    {
        using var sut = CreateSut();
        sut.SidebarPreferences.Should().BeSameAs(_sidebarPreferences);
        _mockUserPreferencesService.VerifyNoOtherCalls();
    }

    [Fact]
    public void NavigationLabels_UseFallbackWhenLocalizationReturnsNull()
    {
        var sut = CreateSut();

        sut.NavSoftwareAppsText.Should().Be("Software & Apps");
        sut.NavOptimizeText.Should().Be("Optimize");
        sut.NavCleanerText.Should().Be("Cleaner");
        sut.NavCustomizeText.Should().Be("Customize");
        sut.NavAdvancedToolsText.Should().Be("Advanced Tools");
        sut.NavSettingsText.Should().Be("Settings");
    }

    [Fact]
    public async Task DonateCommand_OpensTheNormalSponsorsDialog()
    {
        using var sut = CreateSut();

        await sut.DonateCommand.ExecuteAsync(null);

        _mockDialogService.Verify(dialog => dialog.ShowSponsorsDialogAsync(
            WinOptimizer.Core.Features.Common.Enums.SponsorsDialogMode.Normal), Times.Once);
    }

    [Fact]
    public void LanguageChanged_NotifiesLocalizedStringProperties()
    {
        var sut = CreateSut();
        sut.Initialize();

        var changedProperties = new List<string>();
        sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);

        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);

        changedProperties.Should().Contain(nameof(sut.NavigationBackTooltip));
        changedProperties.Should().Contain(nameof(sut.NavigationForwardTooltip));
        changedProperties.Should().Contain(nameof(sut.DonateTooltip));
        changedProperties.Should().Contain(nameof(sut.BugReportTooltip));
        changedProperties.Should().Contain(nameof(sut.DocsTooltip));
    }

    [Fact]
    public void Dispose_UnsubscribesFromEvents()
    {
        var sut = CreateSut();
        sut.Initialize();

        sut.Dispose();

        var changedProperties = new List<string>();
        sut.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName!);
        _mockLocalizationService.Raise(l => l.LanguageChanged += null, this, EventArgs.Empty);
        _mockApplicationModeService.Raise(m => m.ModeChanged += null, this, EventArgs.Empty);

        changedProperties.Should().BeEmpty();
    }

    [Fact]
    public void TrialToggle_EntersAuthoringAndLeavesWithoutApplyingConfiguration()
    {
        var mode = WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Normal;
        _mockApplicationModeService.SetupGet(x => x.CurrentMode).Returns(() => mode);
        _mockApplicationModeService.Setup(x => x.EnterBuilderMode(WinOptimizer.Core.Features.Common.Enums.BuilderTarget.Config))
            .Callback(() => mode = WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder);
        _mockApplicationModeService.Setup(x => x.EnterNormalMode())
            .Callback(() => mode = WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Normal);
        using var sut = CreateSut();
        sut.IsTrialMode = true;
        sut.IsTrialMode.Should().BeTrue();
        sut.IsTrialMode = false;
        sut.IsTrialMode.Should().BeFalse();
        _mockConfigurationService.VerifyNoOtherCalls();
        _mockDialogService.Verify(d => d.ShowTestModeExitDialogAsync(), Times.Once);
        _mockConfigExportService.VerifyNoOtherCalls();
        _mockConfigExecutionService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrialExit_ExportLeavesOnlyAfterSuccessfulSave(bool saved)
    {
        _mockApplicationModeService.SetupGet(m => m.CurrentMode).Returns(WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder);
        _mockDialogService.Setup(d => d.ShowTestModeExitDialogAsync())
            .ReturnsAsync(WinOptimizer.Core.Features.Common.Enums.TestModeExitAction.Export);
        _mockConfigExportService.Setup(e => e.ExportBuilderConfigAsync()).ReturnsAsync(saved);
        using var sut = CreateSut();
        await sut.ExitTestModeCommand.ExecuteAsync(null);
        _mockConfigExportService.Verify(e => e.ExportBuilderConfigAsync(), Times.Once);
        _mockApplicationModeService.Verify(m => m.EnterNormalMode(), saved ? Times.Once : Times.Never);
        _mockConfigExecutionService.VerifyNoOtherCalls();
        sut.CanChangeTrialMode.Should().BeTrue();
    }

    [Fact]
    public async Task TrialExit_ApplyCapturesDraftBeforeDiscard_AndUsesExistingExecutionPipeline()
    {
        var mode = WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder;
        _mockApplicationModeService.SetupGet(m => m.CurrentMode).Returns(() => mode);
        _mockApplicationModeService.Setup(m => m.EnterNormalMode())
            .Callback(() => mode = WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Normal);
        _mockDialogService.Setup(d => d.ShowTestModeExitDialogAsync())
            .ReturnsAsync(WinOptimizer.Core.Features.Common.Enums.TestModeExitAction.Apply);
        var draft = new UnifiedConfigurationFile();
        draft.Optimize.Features = new Dictionary<string, ConfigSection>
        {
            ["Privacy"] = new() { Items = new[] { new ConfigurationItem { Id = "edited", IsSelected = true } } }
        };
        _mockConfigExportService.Setup(e => e.CreateConfigurationFromBuilderEditsAsync()).Returns(() =>
        {
            mode.Should().Be(WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder);
            return Task.FromResult(draft);
        });
        _mockConfigExecutionService.Setup(e => e.ExecuteConfigImportAsync(draft, It.IsAny<ImportOptions>()))
            .Callback(() => mode.Should().Be(WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Normal))
            .Returns(Task.CompletedTask);
        using var sut = CreateSut();
        await sut.ExitTestModeCommand.ExecuteAsync(null);
        _mockConfigExecutionService.Verify(e => e.ExecuteConfigImportAsync(draft, It.Is<ImportOptions>(options =>
            !options.ProcessWindowsAppsRemoval && !options.ProcessExternalAppsRemoval && !options.IsWindowsDefaults)), Times.Once);
        sut.IsTrialMode.Should().BeFalse();
    }

    [Fact]
    public async Task TrialExit_WaitsForChoice_AndRetainsDraftWhenPreparationFails()
    {
        _mockApplicationModeService.SetupGet(m => m.CurrentMode).Returns(WinOptimizer.Core.Features.Common.Enums.WinOptimizerMode.Builder);
        var choice = new TaskCompletionSource<WinOptimizer.Core.Features.Common.Enums.TestModeExitAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockDialogService.Setup(d => d.ShowTestModeExitDialogAsync()).Returns(choice.Task);
        _mockConfigExportService.Setup(e => e.CreateConfigurationFromBuilderEditsAsync()).ThrowsAsync(new IOException("Draft unavailable"));
        using var sut = CreateSut();
        var exit = sut.ExitTestModeCommand.ExecuteAsync(null);
        sut.IsTrialMode.Should().BeTrue();
        sut.CanChangeTrialMode.Should().BeFalse();
        choice.SetResult(WinOptimizer.Core.Features.Common.Enums.TestModeExitAction.Apply);
        await exit;
        sut.IsTrialMode.Should().BeTrue();
        sut.CanChangeTrialMode.Should().BeTrue();
        _mockApplicationModeService.Verify(m => m.EnterNormalMode(), Times.Never);
        _mockDialogService.Verify(d => d.ShowErrorAsync("Draft unavailable", It.IsAny<string>(), "OK"), Times.Once);
    }

    [Fact]
    public void TrialToggle_CannotChangeModeWhileTaskIsRunning()
    {
        using var sut = CreateSut();
        _taskProgressViewModel.IsTaskRunning = true;
        sut.CanChangeTrialMode.Should().BeFalse();
        sut.IsTrialMode = true;
        _mockApplicationModeService.Verify(x => x.EnterBuilderMode(It.IsAny<WinOptimizer.Core.Features.Common.Enums.BuilderTarget>()), Times.Never);
    }

    [Fact]
    public void TrialToggle_CannotStartDuringRealSettingApplication()
    {
        using var sut = CreateSut();
        _mockApplicationModeService.SetupGet(x => x.IsSystemOperationRunning).Returns(true);
        sut.IsTrialMode = true;
        sut.CanChangeTrialMode.Should().BeFalse();
        _mockApplicationModeService.Verify(x => x.EnterBuilderMode(It.IsAny<WinOptimizer.Core.Features.Common.Enums.BuilderTarget>()), Times.Never);
    }
}
