using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Management;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.Management.ViewModels;
using WinOptimizer.UI.Tests.Helpers;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

/// <summary>Uses fake process lifetimes, ports, files and ETW snapshots without inspecting the machine.</summary>
public sealed class ProcessMonitorViewModelTests
{
    private static readonly ProcessIdentity Parent = new(10, 100);
    private static readonly ProcessIdentity Child = new(20, 200);
    private readonly Mock<IProcessMonitorService> _service = new();
    private readonly Mock<ILocalizationService> _localization = new();
    private readonly Mock<IApplicationModeService> _mode = new();
    private ProcessMetric[] _metrics = [Metric(Parent, "Parent", 0), Metric(Child, "Worker", 10)];
    private bool _capturing;
    private int _fileReads;
    private static ProcessMetric Metric(ProcessIdentity id, string name, int parent, double? cpu = 5, long? memory = 1024 * 1024) =>
        new(new(id, parent, name, "C:\\Example\\" + name + ".exe", 100, memory, 2048 * 1024, 100, 200, 4, 20, ""), cpu, 10, 20);

    public ProcessMonitorViewModelTests()
    {
        _localization.Setup(source => source.GetString(It.IsAny<string>())).Returns((string key) => key switch
        { "ProcessMonitor_Summary" => "{0}/{1} {2}", "ProcessMonitor_NetworkSummary" => "{0} {1} {2} {3} {4}", _ => key });
        _service.Setup(source => source.SampleAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new ProcessSnapshot(DateTimeOffset.Now, _metrics));
        _service.Setup(source => source.InspectAsync(It.IsAny<ProcessIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessIdentity id, CancellationToken _) => new ProcessInspection(id, DateTimeOffset.Now, "example --background", "Example user", "Example process", "Example Ltd", [], []));
        _service.Setup(source => source.ReadPortsAsync(It.IsAny<ProcessIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessIdentity id, CancellationToken _) => new ProcessPortSnapshot(id, DateTimeOffset.Now,
                [new("TCP4", "192.0.2.10", 55000, "198.51.100.20", 443, "Established")], []));
        _service.Setup(source => source.ReadOpenFilesAsync(It.IsAny<ProcessIdentity>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessIdentity id, bool _, CancellationToken _) => { _fileReads++; return new ProcessFileSnapshot(id, DateTimeOffset.Now, [new("10", "File", "C:\\Example\\Document.txt")], ""); });
        _service.Setup(source => source.StartNetworkAsync(It.IsAny<ProcessIdentity>(), It.IsAny<CancellationToken>())).Callback(() => _capturing = true).Returns(Task.CompletedTask);
        _service.Setup(source => source.ReadNetworkAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Network(_capturing ? ProcessNetworkState.Running : ProcessNetworkState.Stopped));
        _service.Setup(source => source.StopNetworkAsync()).Callback(() => _capturing = false).ReturnsAsync(() => Network(ProcessNetworkState.Stopped));
    }

    private static ProcessNetworkSnapshot Network(ProcessNetworkState state) => new(Parent, DateTimeOffset.Now.AddSeconds(-2), DateTimeOffset.Now, state,
        [new("TCP", "198.51.100.20", 4096, 8192, 10, DateTimeOffset.Now)], 0, 0, false, "");
    private ProcessMonitorViewModel Create() => new(_service.Object, _localization.Object, _mode.Object, new ExtensionTestDispatcher(), Mock.Of<ITaskProgressService>());

    [Fact]
    public void ConstructionDoesNotSampleCaptureOrAcceptLicenses()
    {
        using var vm = Create(); Assert.False(vm.LicenseAccepted); Assert.Empty(vm.Rows); Assert.False(vm.CanStartNetwork); Assert.Empty(vm.SelectedState);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TreeExpansionAndSearchKeepAncestorsWithoutRescanning()
    {
        using var vm = Create(); await vm.SampleAsync(default); var parent = vm.Rows[0];
        Assert.Equal("Parent", parent.Name); Assert.Equal(12, vm.Rows[1].Indent.Left);
        vm.ToggleExpanded(parent); Assert.Single(vm.Rows);
        vm.SearchText = "worker"; Assert.Equal(2, vm.Rows.Count); Assert.Equal("Parent", vm.Rows[0].Name);
        _service.Verify(source => source.SampleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LiveUpdatesPreserveTheSelectedRowIdentity()
    {
        using var vm = Create(); await vm.SampleAsync(default); var row = vm.Rows[0]; await vm.SelectAsync(row);
        _metrics = [Metric(Parent, "Parent", 0, 25), Metric(Child, "Worker", 10)]; await vm.SampleAsync(default);
        Assert.Same(row, vm.SelectedRow); Assert.Same(row, vm.Rows[0]); Assert.Equal(25, row.Metric.CpuPercent); Assert.Single(vm.Ports);
    }

    [Fact]
    public async Task ReusedPidGetsANewRowAndCannotUseTheOldInspectorControls()
    {
        using var vm = Create(); await vm.SampleAsync(default); var old = vm.Rows[0]; await vm.SelectAsync(old);
        _metrics = [Metric(Parent with { StartedAtUtcTicks = 500 }, "Replacement", 0)]; await vm.SampleAsync(default);
        Assert.NotSame(old, vm.Rows[0]); Assert.False(vm.SelectedAlive); Assert.False(vm.CanStartNetwork);
        Assert.Equal("ProcessMonitor_Exited", vm.SelectedState);
    }

    [Fact]
    public async Task ProtectedLifetimesRemainVisibleButCannotBeInspected()
    {
        _metrics = [Metric(new(4, 0), "Protected", 0, null)];
        using var vm = Create(); await vm.SampleAsync(default); await vm.SelectAsync(vm.Rows[0]);
        Assert.Single(vm.Rows); Assert.False(vm.CanInspect); Assert.False(vm.Rows[0].HasCpu); Assert.Equal("—", vm.Rows[0].Cpu);
        _service.Verify(source => source.InspectAsync(It.IsAny<ProcessIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileQueriesRequireTheExplicitConsentAndUseTheSelectedLifetime()
    {
        using var vm = Create(); await vm.SampleAsync(default); await vm.SelectAsync(vm.Rows[0]);
        await vm.LoadFilesCommand.ExecuteAsync(null); Assert.Equal(0, _fileReads);
        vm.LicenseAccepted = true; await vm.LoadFilesCommand.ExecuteAsync(null);
        Assert.Equal("C:\\Example\\Document.txt", Assert.Single(vm.Files).Path);
        _service.Verify(source => source.ReadOpenFilesAsync(Parent, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAndStopAreExplicitAndRetainObservedIpTotals()
    {
        using var vm = Create(); await vm.SampleAsync(default); await vm.SelectAsync(vm.Rows[0]);
        Assert.False(_capturing); await vm.StartNetworkCommand.ExecuteAsync(null);
        Assert.True(_capturing); Assert.True(vm.CanStopNetwork); Assert.False(vm.CanStartNetwork); Assert.Single(vm.Hosts);
        await vm.StopNetworkCommand.ExecuteAsync(null); Assert.False(_capturing); Assert.Single(vm.Hosts); Assert.True(vm.CanStartNetwork);
    }

    [Fact]
    public async Task SelectingAnotherProcessStopsThePreviousCaptureAndClearsItsDetails()
    {
        using var vm = Create(); await vm.SampleAsync(default); await vm.SelectAsync(vm.Rows[0]);
        await vm.StartNetworkCommand.ExecuteAsync(null); vm.LicenseAccepted = true; await vm.LoadFilesCommand.ExecuteAsync(null);
        await vm.SelectAsync(vm.Rows[1]); Assert.False(_capturing); Assert.Empty(vm.Hosts); Assert.Empty(vm.Files); Assert.Equal(Child, vm.SelectedRow!.Identity);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)] [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task ReadOnlyInspectionRemainsAvailableInNonNormalModes(WinOptimizerMode mode)
    {
        _mode.SetupGet(source => source.CurrentMode).Returns(mode);
        using var vm = Create(); await vm.SampleAsync(default); await vm.SelectAsync(vm.Rows[0]); Assert.True(vm.CanInspect);
    }

    [Fact]
    public async Task NavigatingAwayStopsTheOwnedCapture()
    {
        using var vm = Create(); await vm.ActivateAsync(); await vm.SelectAsync(vm.Rows[0]); await vm.StartNetworkCommand.ExecuteAsync(null);
        _service.Invocations.Clear(); await vm.DeactivateAsync();
        Assert.False(_capturing); Assert.False(vm.CanStopNetwork); _service.Verify(source => source.StopNetworkAsync(), Times.Once);
    }

    [Fact]
    public async Task ACancelledPageNeverPublishesItsLateSample()
    {
        var pending = new TaskCompletionSource<ProcessSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Setup(source => source.SampleAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var vm = Create(); await vm.ActivateAsync(); var closing = vm.DeactivateAsync(); Assert.False(closing.IsCompleted);
        pending.SetResult(new(DateTimeOffset.Now, _metrics)); await closing; Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task OperationalErrorsAreReportedWithoutInventingTraffic()
    {
        _service.Setup(source => source.StartNetworkAsync(It.IsAny<ProcessIdentity>(), It.IsAny<CancellationToken>())).ThrowsAsync(new UtilityOperationException("Administrator required"));
        using var vm = Create(); await vm.SampleAsync(default); await vm.SelectAsync(vm.Rows[0]); await vm.StartNetworkCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Equal("Administrator required", vm.Status); Assert.Empty(vm.Hosts);
    }

    [Fact]
    public async Task SortModesUseMeasurementsAndSearchIncludesTheExecutablePath()
    {
        _metrics = [Metric(Parent, "Parent", 0, 2), Metric(Child, "Worker", 10, 20)];
        using var vm = Create(); await vm.SampleAsync(default); vm.SortMode = 1; Assert.Equal(Child, vm.Rows[0].Identity);
        vm.SearchText = "EXAMPLE\\Worker.exe"; Assert.Equal(Child, Assert.Single(vm.Rows).Identity);
    }

    [Fact]
    public async Task ColumnHeadersSortAllColumnsInBothDirectionsAndShareTheSelectorState()
    {
        _metrics = [Metric(Parent, "Zulu", 0, 2), Metric(Child, "Alpha", 10, 20, 10 * 1024 * 1024)];
        using var vm = Create(); await vm.SampleAsync(default);
        foreach (var (column, mode, first, descending) in new[]
        {
            ("Name", 3, Child, false), ("Pid", 4, Parent, false),
            ("Cpu", 1, Child, true), ("Memory", 2, Child, true)
        })
        {
            vm.SortByColumnCommand.Execute(column);
            Assert.Equal(mode, vm.SortMode); Assert.Equal(descending, vm.SortDescending);
            Assert.Equal(first, vm.Rows[0].Identity);
            Assert.All(vm.Rows, row => { Assert.False(row.HasChildren); Assert.Equal(0, row.Indent.Left); });
            vm.SortByColumnCommand.Execute(column);
            Assert.Equal(!descending, vm.SortDescending); Assert.NotEqual(first, vm.Rows[0].Identity);
        }
        Assert.EndsWith("↑", vm.MemoryHeader);
        vm.SortMode = 1; Assert.True(vm.SortDescending); Assert.EndsWith("↓", vm.CpuHeader);
        Assert.DoesNotContain("↑", vm.MemoryHeader); Assert.DoesNotContain("↓", vm.MemoryHeader);
        _service.Verify(source => source.SampleAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MeasurementSortsKeepUnknownsLastAndUseNumericValuesAndStableTies()
    {
        var unknown = new ProcessIdentity(30, 300);
        _metrics = [Metric(Child, "Same", 0, 10), Metric(unknown, "Unknown", 0, null, null), Metric(Parent, "Same", 0, 10)];
        using var vm = Create(); await vm.SampleAsync(default);
        foreach (var column in new[] { "Cpu", "Memory" })
        {
            vm.SortByColumnCommand.Execute(column);
            Assert.Equal(new[] { Parent, Child, unknown }, vm.Rows.Select(row => row.Identity));
            vm.SortByColumnCommand.Execute(column);
            Assert.Equal(new[] { Parent, Child, unknown }, vm.Rows.Select(row => row.Identity));
        }
        _metrics = [Metric(Parent, "Same", 0, 2), Metric(Child, "Same", 0, 10), _metrics[1]];
        await vm.SampleAsync(default); vm.SortByColumnCommand.Execute("Cpu");
        Assert.Equal(Child, vm.Rows[0].Identity); Assert.Equal(unknown, vm.Rows[^1].Identity);
    }

    [Fact]
    public async Task SortingSurvivesSamplesAndSearchWithoutLosingSelectionOrTreeExpansion()
    {
        using var vm = Create(); await vm.SampleAsync(default);
        var parent = vm.Rows[0]; vm.ToggleExpanded(parent); await vm.SelectAsync(parent);
        vm.SortByColumnCommand.Execute("Cpu"); vm.SortByColumnCommand.Execute("Cpu");
        _metrics = [Metric(Parent, "Parent", 0, 30), Metric(Child, "Worker", 10, 2)];
        await vm.SampleAsync(default);
        Assert.Same(parent, vm.SelectedRow); Assert.False(vm.SortDescending); Assert.Equal(Child, vm.Rows[0].Identity);
        vm.SearchText = "worker"; Assert.Equal(Child, Assert.Single(vm.Rows).Identity);
        vm.SortMode = 0; Assert.Equal(2, vm.Rows.Count); Assert.Equal(12, vm.Rows[1].Indent.Left);
        vm.SearchText = ""; Assert.Same(parent, Assert.Single(vm.Rows)); Assert.Same(parent, vm.SelectedRow);
        Assert.DoesNotContain("↑", vm.CpuHeader); Assert.DoesNotContain("↓", vm.CpuHeader);
    }

    [Fact]
    public void DeepTreesKeepNamesReadableAndUseNativeChevronGlyphs()
    {
        var row = new ProcessDisplayRow(Metric(Parent, "Parent", 0));
        row.SetTree(20, true, true); Assert.Equal(72, row.Indent.Left); Assert.Equal("\uE70D", row.Expander);
        row.SetTree(1, true, false); Assert.Equal(12, row.Indent.Left); Assert.Equal("\uE76C", row.Expander);
    }
}
