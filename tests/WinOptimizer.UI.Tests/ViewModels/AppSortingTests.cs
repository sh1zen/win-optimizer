using System.Reflection;
using System.Xml.Linq;
using CommunityToolkit.WinUI.Collections;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.Core.Features.SoftwareApps.Interfaces;
using WinOptimizer.Core.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.Common.Interfaces;
using WinOptimizer.UI.Features.SoftwareApps.Models;
using WinOptimizer.UI.Features.SoftwareApps.ViewModels;
using Xunit;

namespace WinOptimizer.UI.Tests.ViewModels;

public class AppSortingTests
{
    private readonly Mock<ILocalizationService> _text = new();
    private readonly Mock<IDispatcherService> _dispatcher = new();
    private readonly Mock<IUserPreferencesService> _preferences = new();
    private readonly Mock<IWindowsAppsService> _windows = new();
    private readonly Mock<IExternalAppsService> _external = new();

    public AppSortingTests()
    {
        _text.Setup(text => text.GetString(It.IsAny<string>())).Returns((string key) => key);
        _dispatcher.Setup(dispatcher => dispatcher.RunOnUIThread(It.IsAny<Action>())).Callback<Action>(action => action());
        _dispatcher.Setup(dispatcher => dispatcher.RunOnUIThreadAsync(It.IsAny<Func<Task>>())).Returns<Func<Task>>(action => action());
        _preferences.Setup(p => p.GetPreference(It.IsAny<string>(), It.IsAny<string>())).Returns((string key, string fallback) => fallback);
        _preferences.Setup(p => p.SetPreferenceAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(OperationResult.Succeeded());
    }

    private SoftwareAppsViewModel Create()
    {
        var windows = new WindowsAppsViewModel(_windows.Object, Mock.Of<IAppUninstallWorkflow>(), Mock.Of<ILogService>(),
            Mock.Of<IDialogService>(), _text.Object, _dispatcher.Object, Mock.Of<IThemeService>(), Mock.Of<IAppIconResolver>());
        var external = new ExternalAppsViewModel(_external.Object, Mock.Of<IAppUninstallWorkflow>(), Mock.Of<ILogService>(),
            Mock.Of<IDialogService>(), _text.Object, _dispatcher.Object, Mock.Of<IThemeService>(), Mock.Of<IAppIconResolver>());
        return new SoftwareAppsViewModel(windows, external, _text.Object, Mock.Of<ILogService>(), _preferences.Object,
            Mock.Of<IConfigReviewModeService>(), Mock.Of<IConfigReviewBadgeService>(), Mock.Of<IApplicationModeService>(),
            Mock.Of<IUninstallResidueReview>(), new LeftoverAppsViewModel(Mock.Of<ILeftoverService>(), _text.Object,
                Mock.Of<IDialogService>(), Mock.Of<IApplicationModeService>(), Mock.Of<ITaskProgressService>(), _dispatcher.Object, Mock.Of<ILogService>()));
    }

    private AppItemViewModel Item(string id, string name, string group, int? metric)
    {
        var date = metric.HasValue ? DateTimeOffset.UnixEpoch.AddDays(metric.Value) : (DateTimeOffset?)null;
        return new AppItemViewModel(new ItemDefinition
        {
            Id = id, Name = name, Description = "", GroupName = group, AppxPackageName = ["Vendor." + id], IsInstalled = true,
            Metadata = new AppMetadata(date, metric, date),
        }, _text.Object, _dispatcher.Object, Mock.Of<IThemeService>());
    }

    [Theory]
    [InlineData(AppSortMode.GroupAsc, "b,a,c,d")]
    [InlineData(AppSortMode.NameAsc, "a,b,c,d")]
    [InlineData(AppSortMode.InstallationDate, "c,b,d,a")]
    [InlineData(AppSortMode.Size, "c,b,d,a")]
    [InlineData(AppSortMode.LastUsageTime, "c,b,d,a")]
    public void EveryModeOrdersTheGridAndGroupedViewsConsistently(AppSortMode mode, string expected)
    {
        using var vm = Create();
        foreach (var child in new[] { vm.WindowsAppsViewModel.Items, vm.ExternalAppsViewModel.Items })
        {
            child.Add(Item("d", "Zulu", "B", 2));
            child.Add(Item("c", "Gamma", "B", 10));
            child.Add(Item("b", "Beta", "A", 2));
            child.Add(Item("a", "alpha", "B", null));
        }
        vm.ApplySortMode(mode);
        // Rebuild groups through the normal filter path, including the default sort.
        vm.ExternalAppsViewModel.SearchText = " ";
        var order = expected.Split(',');
        Assert.Equal(order, vm.WindowsAppsViewModel.ItemsView.Cast<AppItemViewModel>().Select(item => item.Id));
        Assert.Equal(order, vm.ExternalAppsViewModel.ItemsView.Cast<AppItemViewModel>().Select(item => item.Id));
        Assert.Equal(order, vm.WindowsAppsViewModel.WindowsAppsFiltered.Select(item => item.Id));
        foreach (var category in vm.ExternalAppsViewModel.Categories)
            Assert.Equal(order.Where(id => category.Apps.Any(item => item.Id == id)), category.Apps.Select(item => item.Id));
        foreach (var view in Enum.GetValues<SoftwareAppsViewMode>())
        {
            vm.ViewMode = view;
            Assert.Equal(mode, vm.SortMode);
            Assert.Equal(order, vm.WindowsAppsViewModel.ItemsView.Cast<AppItemViewModel>().Select(item => item.Id));
        }
    }

    [Fact]
    public void ReselectingTheMenuChoiceOverridesAColumnSort()
    {
        using var vm = Create();
        vm.WindowsAppsViewModel.Items.Add(Item("b", "Beta", "A", 2));
        vm.WindowsAppsViewModel.Items.Add(Item("a", "Alpha", "A", 1));
        vm.ApplySortMode(AppSortMode.NameAsc);
        vm.WindowsAppsViewModel.ItemsView.SortDescriptions.Clear();
        vm.WindowsAppsViewModel.ItemsView.SortDescriptions.Add(new SortDescription("Name", SortDirection.Descending));
        Assert.Equal("b", ((AppItemViewModel)vm.WindowsAppsViewModel.ItemsView[0]).Id);
        vm.ApplySortMode(AppSortMode.NameAsc);
        Assert.Equal("a", ((AppItemViewModel)vm.WindowsAppsViewModel.ItemsView[0]).Id);
        _preferences.Verify(preferences => preferences.SetPreferenceAsync("SoftwareAppsSortMode", "NameAsc"), Times.Once);
    }

    [Theory]
    [InlineData("GroupAsc", AppSortMode.GroupAsc)]
    [InlineData("NameAsc", AppSortMode.NameAsc)]
    [InlineData("InstallationDate", AppSortMode.InstallationDate)]
    [InlineData("Size", AppSortMode.Size)]
    [InlineData("LastUsageTime", AppSortMode.LastUsageTime)]
    [InlineData("NameAscInstalledFirst", AppSortMode.GroupAsc)]
    [InlineData("NameDesc", AppSortMode.GroupAsc)]
    [InlineData("999", AppSortMode.GroupAsc)]
    public async Task SavedSortRestoresToBothTabs(string saved, AppSortMode expected)
    {
        _preferences.Setup(p => p.GetPreference("SoftwareAppsSortMode", It.IsAny<string>())).Returns(saved);
        using var vm = Create();
        vm.WindowsAppsViewModel.IsInitialized = true;
        vm.ExternalAppsViewModel.IsInitialized = true;
        await vm.InitializeAsync();
        Assert.Equal(expected, vm.SortMode);
        Assert.Equal(expected, vm.WindowsAppsViewModel.SortMode);
        Assert.Equal(expected, vm.ExternalAppsViewModel.SortMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataRefreshReordersEvenWhenInstalledStatusDoesNotChange(bool external)
    {
        using var vm = Create();
        var first = Item("a", "Alpha", "A", 1);
        var second = Item("b", "Beta", "A", 2);
        var items = external ? vm.ExternalAppsViewModel.Items : vm.WindowsAppsViewModel.Items;
        items.Add(first);
        items.Add(second);
        vm.ApplySortMode(AppSortMode.Size);
        _external.Setup(service => service.GetAppsAsync()).ReturnsAsync(new[] { first.Definition, second.Definition });
        _windows.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .Callback(() => first.Definition.Metadata = new AppMetadata(null, 3, null))
            .ReturnsAsync(new Dictionary<string, bool> { ["a"] = true, ["b"] = true });
        _external.Setup(service => service.CheckBatchInstalledAsync(It.IsAny<IEnumerable<ItemDefinition>>()))
            .Callback(() => first.Definition.Metadata = new AppMetadata(null, 3, null))
            .ReturnsAsync(new Dictionary<string, bool> { ["a"] = true, ["b"] = true });
        if (external) await vm.ExternalAppsViewModel.CheckInstallationStatusAsync();
        else await vm.WindowsAppsViewModel.CheckInstallationStatusAsync();
        var table = external ? vm.ExternalAppsViewModel.ItemsView : vm.WindowsAppsViewModel.ItemsView;
        Assert.Equal(new[] { "a", "b" }, table.Cast<AppItemViewModel>().Select(item => item.Id));
        Assert.Equal(new[] { "a", "b" }, external ? vm.ExternalAppsViewModel.Categories.Single().Apps.Select(item => item.Id)
            : vm.WindowsAppsViewModel.WindowsAppsFiltered.Select(item => item.Id));
    }

    [Fact]
    public void SortMenuOffersExactlyFiveOptionsAndIsEnabledInTheGrid()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoftwareAppsPage.xaml");
        var document = XDocument.Load(source!);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = document.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == "SortButton");
        Assert.Null(button.Attribute("IsEnabled"));
        Assert.Equal(new[] { "SortGroupAsc_Click", "SortNameAsc_Click", "SortInstallationDate_Click", "SortSize_Click", "SortLastUsageTime_Click" },
            button.Descendants().Where(element => element.Name.LocalName == "RadioMenuFlyoutItem").Select(element => (string?)element.Attribute("Click")));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1.0 KiB")]
    [InlineData(1048576L, "1.0 MiB")]
    [InlineData(1073741824L, "1.0 GiB")]
    [InlineData(1099511627776L, "1.0 TiB")]
    public void MetadataSizeDisplaysReadableUnitsAndDistinguishesUnknownFromZero(long? bytes, string expected)
    {
        _text.Setup(text => text.CurrentLanguage).Returns("en-US");
        using var item = Item("app", "App", "A", null);
        item.Definition.Metadata = new AppMetadata(null, bytes, null);
        Assert.Equal(expected, item.SizeText);
    }

    [Fact]
    public void MetadataDatesAndSizesUpdateWithTheSelectedLanguageAndDiscovery()
    {
        _text.Setup(text => text.CurrentLanguage).Returns("en-US");
        using var item = Item("app", "App", "A", null);
        Assert.Equal("—", item.InstallationDateText);
        Assert.Equal("—", item.LastUsageTimeText);
        var timestamp = new DateTimeOffset(2026, 10, 20, 13, 45, 0, TimeSpan.Zero);
        item.Definition.Metadata = new AppMetadata(timestamp, 1536, timestamp);
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        item.NotifyMetadataChanged();
        Assert.Contains(nameof(item.InstallationDateText), changed);
        Assert.Contains(nameof(item.LastUsageTimeText), changed);
        Assert.Contains(nameof(item.SizeText), changed);
        Assert.Equal(timestamp.ToLocalTime().ToString("d", System.Globalization.CultureInfo.GetCultureInfo("en-US")), item.InstallationDateText);
        Assert.Equal(timestamp.ToLocalTime().ToString("g", System.Globalization.CultureInfo.GetCultureInfo("en-US")), item.LastUsageTimeText);
        Assert.Equal("1.5 KiB", item.SizeText);
        changed.Clear();
        _text.Setup(text => text.CurrentLanguage).Returns("it-IT");
        _text.Raise(text => text.LanguageChanged += null, EventArgs.Empty);
        Assert.Contains(nameof(item.InstallationDateText), changed);
        Assert.Contains(nameof(item.LastUsageTimeText), changed);
        Assert.Contains(nameof(item.SizeText), changed);
        Assert.Equal("1,5 KiB", item.SizeText);
        Assert.Equal(timestamp.ToLocalTime().ToString("d", System.Globalization.CultureInfo.GetCultureInfo("it-IT")), item.InstallationDateText);
    }

    [Fact]
    public void BothAppTablesShowMetadataUsingTypedSortKeys()
    {
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoftwareAppsPage.xaml");
        var document = XDocument.Load(source!);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "WindowsAppsDataGrid", "ExternalAppsDataGrid" })
        {
            var table = document.Descendants().Single(element => (string?)element.Attribute(xaml + "Name") == name);
            var columns = table.Elements().Single(element => element.Name.LocalName == "DataGrid.Columns").Elements().ToArray();
            var expected = new[] { "Name", "Description", "LastUsageTime", "InstallationDate", "SizeBytes" };
            Assert.Equal(expected, columns.Select(column => (string?)column.Attribute("Tag")).OfType<string>());
            if (name == "WindowsAppsDataGrid")
            {
                Assert.Equal("SizeBytes", (string?)columns.Last().Attribute("Tag"));
                Assert.Equal("{StaticResource LastColumnHeaderStyle}", (string?)columns.Last().Attribute("HeaderStyle"));
            }
            foreach (var (tag, key, binding) in new[]
            {
                ("LastUsageTime", "AppLastUsageCellTemplate", "LastUsageTimeText"),
                ("InstallationDate", "AppInstallationDateCellTemplate", "InstallationDateText"),
                ("SizeBytes", "AppSizeCellTemplate", "SizeText"),
            })
            {
                var column = columns.Single(element => (string?)element.Attribute("Tag") == tag);
                Assert.Equal("{StaticResource " + key + "}", (string?)column.Attribute("CellTemplate"));
                var template = document.Descendants().Single(element => (string?)element.Attribute(xaml + "Key") == key);
                Assert.Equal("{Binding " + binding + "}", (string?)template.Elements().Single().Attribute("Text"));
            }
        }
    }
}
