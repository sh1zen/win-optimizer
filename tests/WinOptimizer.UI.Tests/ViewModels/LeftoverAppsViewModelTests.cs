using System.Xml.Linq;
using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Exercises the real leftover view model and third-tab routing on the WinUI dispatcher.</summary>
public sealed class LeftoverAppsViewModelTests : IDisposable
{
    private readonly Mock<ILeftoverService> _service = new();
    private readonly Mock<ILocalizationService> _text = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private readonly Mock<ITaskProgressService> _tasks = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly LeftoverAppsViewModel _vm;
    private LeftoverItem _first = new(Guid.NewGuid(), "obsolete.msi", LeftoverKind.Msi, @"C:\Windows\Installer\obsolete.msi", 42, true, "Unreferenced fixture");
    private LeftoverItem _protected = new(Guid.NewGuid(), "protected.msp", LeftoverKind.Msp, @"C:\Windows\Installer\protected.msp", 80, false, "Ownership unknown");

    public LeftoverAppsViewModelTests()
    {
        _text.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key);
        _text.Setup(source => source.GetString(It.IsAny<string>(), It.IsAny<object[]>())).Returns((string key, object[] args) => key);
        _text.Setup(source => source.GetString("Accessibility_Selected")).Returns("Selected");
        _dispatcher.Setup(source => source.RunOnUIThreadAsync(It.IsAny<Func<Task>>())).Returns((Func<Task> action) => action());
        _dispatcher.Setup(source => source.RunOnUIThread(It.IsAny<Action>())).Callback((Action action) => action());
        _tasks.Setup(source => source.StartTask(It.IsAny<string>(), It.IsAny<bool>())).Returns(_cancel);
        _service.Setup(source => source.ScanAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new LeftoverScan([_first, _protected], ["Fixture coverage notice"]));
        _vm = new(_service.Object, _text.Object, _dialogs.Object, _mode.Object, _tasks.Object, _dispatcher.Object, Mock.Of<ILogService>());
    }

    [Fact]
    public async Task ScanPublishesOnlySelectableLeftoversAndRetainsCoverageNotices()
    {
        Assert.Empty(_vm.Items);
        _service.VerifyNoOtherCalls();
        await _vm.ScanAsync();
        Assert.Single(_vm.Items); Assert.Single(_vm.ItemsView.Cast<LeftoverItemViewModel>()); Assert.True(_vm.HasScanned); Assert.True(_vm.HasNotices);
        Assert.DoesNotContain(_vm.Items, row => !row.CanRemove);
        _vm.SearchText = "protected";
        _vm.ToggleSelectAll(); Assert.False(_vm.HasSelectedItems);
        _vm.SearchText = "obsolete";
        _vm.ToggleSelectAll(); Assert.True(_vm.IsAllSelected); Assert.True(_vm.HasSelectedItems);
        Assert.Equal(_first.Id, Assert.Single(_vm.Items, item => item.IsSelected).Item.Id);
    }

    [Fact]
    public async Task SelectionSummaryTracksIndividualAndFilteredBulkSelections()
    {
        var folder = _first with { Id = Guid.NewGuid(), Name = "folder", Location = @"C:\Program Files\Folder", Kind = LeftoverKind.Folder, Bytes = 1536 };
        var registration = _first with { Id = Guid.NewGuid(), Name = "registration", Location = @"HKEY_LOCAL_MACHINE\Software\Registration", Kind = LeftoverKind.Registration, Bytes = 0 };
        _service.Setup(source => source.ScanAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeftoverScan([_first, folder, registration, _protected], []));
        await _vm.ScanAsync();
        Assert.Equal("Selected: 0 · 0 B", _vm.SelectionText);
        var changes = new List<string?>();
        _vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        _vm.Items[1].IsSelected = true;
        Assert.Equal(1, _vm.SelectedCount); Assert.Equal(1536, _vm.SelectedBytes);
        Assert.Equal($"Selected: 1 · {1.5:N1} KiB", _vm.SelectionText);
        foreach (var property in new[] { nameof(_vm.SelectedCount), nameof(_vm.SelectedBytes), nameof(_vm.SelectedSize), nameof(_vm.SelectionText) })
            Assert.Contains(property, changes);

        _vm.SearchText = "obsolete"; _vm.ToggleSelectAll();
        Assert.True(_vm.IsAllSelected);
        Assert.Equal(2, _vm.SelectedCount); Assert.Equal(1578, _vm.SelectedBytes);
        _vm.ToggleSelectAll();
        Assert.Equal(1, _vm.SelectedCount); Assert.Equal(1536, _vm.SelectedBytes);
        _vm.SearchText = ""; _vm.ToggleSelectAll();
        Assert.Equal(3, _vm.SelectedCount); Assert.Equal(1578, _vm.SelectedBytes);
        _vm.ToggleSelectAll();
        Assert.Equal("Selected: 0 · 0 B", _vm.SelectionText);

        changes.Clear();
        _text.Setup(source => source.GetString("Accessibility_Selected")).Returns("Selezionato");
        _vm.NotifyLanguageChanged();
        Assert.Equal("Selezionato: 0 · 0 B", _vm.SelectionText);
        Assert.Contains(nameof(_vm.SelectionText), changes);
    }

    [Theory]
    [InlineData(0L, 0d, "B")]
    [InlineData(42L, 42d, "B")]
    [InlineData(1024L, 1d, "KiB")]
    [InlineData(1572864L, 1.5d, "MiB")]
    [InlineData(5368709120L, 5d, "GiB")]
    public async Task SelectionSizeUsesReadableUnitsAndSupportsTotalsAboveFourGiB(long bytes, double amount, string unit)
    {
        _first = _first with { Bytes = bytes };
        await _vm.ScanAsync(); _vm.Items[0].IsSelected = true;
        Assert.Equal(1, _vm.SelectedCount); Assert.Equal(bytes, _vm.SelectedBytes);
        Assert.Equal(unit == "B" ? $"{amount:N0} B" : $"{amount:N1} {unit}", _vm.SelectedSize);
    }

    [Fact]
    public async Task RefreshClearsTheSelectionSummaryAndDetachesOldRows()
    {
        await _vm.ScanAsync();
        var oldRow = _vm.Items[0]; oldRow.IsSelected = true;
        await _vm.ScanAsync();
        Assert.Equal(0, _vm.SelectedCount); Assert.Equal(0, _vm.SelectedBytes);
        Assert.Equal("Selected: 0 · 0 B", _vm.SelectionText);
        var changes = new List<string?>();
        _vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        oldRow.IsSelected = false;
        Assert.DoesNotContain(nameof(_vm.SelectionText), changes);
    }

    [Fact]
    public async Task CleanupRequiresConfirmationAndRefreshesItsExpiredSelection()
    {
        await _vm.ScanAsync(); _vm.Items[0].IsSelected = true;
        _dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = false });
        await _vm.RemoveSelectedAsync();
        _service.Verify(source => source.RemoveAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        _dialogs.Setup(source => source.ShowConfirmationAsync(It.IsAny<ConfirmationRequest>())).ReturnsAsync(new ConfirmationResponse { Confirmed = true });
        _service.Setup(source => source.RemoveAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LeftoverCleanupResult(1, [], Guid.NewGuid()));
        await _vm.RemoveSelectedAsync();
        _service.Verify(source => source.RemoveAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(_first.Id)), It.IsAny<CancellationToken>()), Times.Once);
        _service.Verify(source => source.ScanAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.False(_vm.HasSelectedItems); Assert.False(_vm.IsBusy);
        Assert.Equal("Selected: 0 · 0 B", _vm.SelectionText);
    }

    [Fact]
    public async Task FailedOrCancelledScansClearPriorResultsInsteadOfOfferingStaleSelections()
    {
        await _vm.ScanAsync(); _vm.Items[0].IsSelected = true;
        _service.Setup(source => source.ScanAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Fixture inventory failed"));
        await _vm.ScanAsync();
        Assert.Empty(_vm.Items); Assert.False(_vm.HasSelectedItems); Assert.False(_vm.HasScanned);
        Assert.Equal("Selected: 0 · 0 B", _vm.SelectionText);
        Assert.Equal("Fixture inventory failed", _vm.StatusText);
        _service.Setup(source => source.ScanAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await _vm.ScanAsync();
        Assert.False(_vm.IsBusy); Assert.Equal("UninstallResidue_Cancelled", _vm.StatusText);
        Assert.Equal("Selected: 0 · 0 B", _vm.SelectionText);
    }

    [Fact]
    public async Task BuilderAndReviewModesCannotDeleteLeftovers()
    {
        await _vm.ScanAsync(); _vm.Items[0].IsSelected = true;
        foreach (var mode in new[] { WinOptimizerMode.Builder, WinOptimizerMode.ConfigReview })
        {
            _mode.SetupGet(source => source.CurrentMode).Returns(mode);
            await _vm.RemoveSelectedAsync();
        }
        _dialogs.VerifyNoOtherCalls();
        _service.Verify(source => source.RemoveAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ThirdTabRoutesSearchAndRefreshAndExitsWhenBuilderModeStarts()
    {
        var windows = new WindowsAppsViewModel(Mock.Of<IWindowsAppsService>(), Mock.Of<IAppUninstallWorkflow>(), Mock.Of<ILogService>(),
            _dialogs.Object, _text.Object, _dispatcher.Object, Mock.Of<IThemeService>(), Mock.Of<IAppIconResolver>()) { IsInitialized = true };
        var external = new ExternalAppsViewModel(Mock.Of<IExternalAppsService>(), Mock.Of<IAppUninstallWorkflow>(), Mock.Of<ILogService>(),
            _dialogs.Object, _text.Object, _dispatcher.Object, Mock.Of<IThemeService>(), Mock.Of<IAppIconResolver>()) { IsInitialized = true };
        using var parent = new SoftwareAppsViewModel(windows, external, _text.Object, Mock.Of<ILogService>(), Mock.Of<IUserPreferencesService>(),
            Mock.Of<IConfigReviewModeService>(), Mock.Of<IConfigReviewBadgeService>(), _mode.Object, Mock.Of<IUninstallResidueReview>(), _vm);
        await parent.SelectLeftoverTabAsync();
        Assert.True(parent.IsLeftoverTabSelected); Assert.False(parent.IsWindowsAppsTabSelected); Assert.False(parent.IsExternalAppsTabSelected);
        Assert.False(parent.IsInventoryTabSelected); Assert.Equal("UninstallResidue_Delete", parent.RemoveButtonText);
        parent.SearchText = "obsolete"; Assert.Equal("obsolete", _vm.SearchText);
        _vm.Items[0].IsSelected = true; Assert.True(parent.CanRemoveItems);
        await parent.RefreshInstallationStatusCommand.ExecuteAsync(null);
        _service.Verify(source => source.ScanAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _mode.SetupGet(source => source.CurrentMode).Returns(WinOptimizerMode.Builder);
        _mode.Raise(source => source.ModeChanged += null, EventArgs.Empty);
        Assert.False(parent.IsLeftoverTabSelected); Assert.True(parent.IsWindowsAppsTabSelected); Assert.False(parent.IsLeftoverTabEnabled);
        await parent.SelectLeftoverTabAsync(); Assert.True(parent.IsWindowsAppsTabSelected);
        windows.Dispose(); external.Dispose();
    }

    [Fact]
    public void PageDeclaresTheLeftoverTabAndInspectionControl()
    {
        using var stream = typeof(LeftoverAppsViewModelTests).Assembly.GetManifestResourceStream("SoftwareAppsPage.xaml");
        var page = XDocument.Load(stream!);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tab = Assert.Single(page.Descendants(), element => (string?)element.Attribute(x + "Name") == "LeftoverTab");
        Assert.Contains("SelectLeftoverTabCommand", (string)tab.Attribute("Command")!);
        Assert.Contains(page.Descendants(), element => element.Name.LocalName == "LeftoverAppsControl");
    }

    public void Dispose() { _vm.Dispose(); _cancel.Dispose(); }
}
