using Moq;
using WinOptimizer.Core.Features.SystemInformation.Models;
using WinOptimizer.Infrastructure.Features.SystemInformation;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.SystemInformation;

public sealed class SystemInformationServiceTests
{
    private readonly Mock<ISystemInformationQuery> _query = new();
    private readonly WindowsSystemInformationService _service;
    public SystemInformationServiceTests()
    {
        _query.Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IReadOnlyDictionary<string, object?>>());
        _service = new(_query.Object);
    }
    private void Rows(string type, params IReadOnlyDictionary<string, object?>[] rows) =>
        _query.Setup(x => x.ReadAsync(type, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(rows);
    private static SystemInformationValue Value(SystemInformationSnapshot snapshot, SystemInformationCategory category, string label) =>
        Assert.Single(snapshot.Sections.Single(section => section.Category == category).Items).Properties.Single(property => property.LabelKey == "SystemInfo_Field_" + label).Value;

    [Fact]
    public void Construction_DoesNotCollectWindowsData()
    {
        Assert.Null(_service.CachedSnapshot);
        _query.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompletedSnapshot_IsReusedByReference_WhileExplicitReadsRecollect()
    {
        var first = await _service.ReadAsync(default);
        Assert.Same(first, _service.CachedSnapshot);
        Assert.Same(first, _service.CachedSnapshot);
        _query.Verify(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Exactly(13));
        var refreshed = await _service.ReadAsync(default);
        Assert.NotSame(first, refreshed);
        Assert.Same(refreshed, _service.CachedSnapshot);
        _query.Verify(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Exactly(26));
    }

    [Fact]
    public async Task CancelledOrFailedRefresh_RetainsTheLastCompletedSessionSnapshot()
    {
        var previous = await _service.ReadAsync(default);
        using var owner = new CancellationTokenSource();
        _query.Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { owner.Cancel(); return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ReadAsync(owner.Token));
        Assert.Same(previous, _service.CachedSnapshot);
        _query.Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ReadAsync(default));
        Assert.Same(previous, _service.CachedSnapshot);
    }
    [Fact]
    public async Task Snapshot_QueriesEachCategoryOnce_WithExplicitUniqueProperties()
    {
        var requests = new List<(string Class, IReadOnlyList<string> Fields)>();
        _query.Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<string>, CancellationToken>((name, fields, _) => requests.Add((name, fields)))
            .ReturnsAsync(Array.Empty<IReadOnlyDictionary<string, object?>>());
        var result = await _service.ReadAsync(default);
        Assert.Equal(Enum.GetValues<SystemInformationCategory>(), result.Sections.Select(section => section.Category));
        Assert.Equal(13, requests.Count);
        Assert.Equal(13, requests.Select(request => request.Class).Distinct().Count());
        Assert.All(requests, request => { Assert.NotEmpty(request.Fields); Assert.Equal(request.Fields.Count, request.Fields.Distinct().Count()); });
        Assert.All(result.Sections, section => { Assert.Null(section.Error); Assert.Empty(section.Items); });
        Assert.Contains(requests, request => request.Class == "Win32_PnPSignedDriver" && request.Fields.Contains("DriverVersion") && request.Fields.Contains("DriverDate") && request.Fields.Contains("IsSigned"));
        Assert.DoesNotContain(requests, request => request.Fields.Contains("AdapterRAM")); // WMI's uint32 VRAM field truncates larger adapters.
    }
    [Fact]
    public async Task Quantities_Remain64Bit_AndWindowsKilobytesBecomeBytes()
    {
        Rows("Win32_ComputerSystem", new Dictionary<string, object?> { ["Name"] = " Example machine ", ["TotalPhysicalMemory"] = 34359738368UL });
        Rows("Win32_OperatingSystem", new Dictionary<string, object?> { ["FreePhysicalMemory"] = 8388608UL });
        var snapshot = await _service.ReadAsync(default);
        Assert.Equal(new SystemInformationNumber(34359738368UL, SystemInformationUnit.Bytes), Value(snapshot, SystemInformationCategory.Machine, "TotalMemory"));
        Assert.Equal(new SystemInformationNumber(8589934592UL, SystemInformationUnit.Bytes), Value(snapshot, SystemInformationCategory.Windows, "AvailableMemory"));
        Assert.Equal("Example machine", Assert.Single(snapshot.Sections[0].Items).Name);
    }
    [Fact]
    public async Task MissingValues_AreUnknown_AndDoNotBecomeFalseOrZero()
    {
        Rows("Win32_NetworkAdapter", new Dictionary<string, object?> { ["Name"] = "Example adapter", ["NetEnabled"] = false, ["Speed"] = 0UL });
        var snapshot = await _service.ReadAsync(default);
        Assert.Equal(new SystemInformationBoolean(false), Value(snapshot, SystemInformationCategory.Network, "Enabled"));
        Assert.Equal(new SystemInformationBoolean(null), Value(snapshot, SystemInformationCategory.Network, "PhysicalAdapter"));
        Assert.Equal(new SystemInformationNumber(0, SystemInformationUnit.BitsPerSecond), Value(snapshot, SystemInformationCategory.Network, "LinkSpeed"));
        Assert.Equal(new SystemInformationText(null), Value(snapshot, SystemInformationCategory.Network, "MacAddress"));
    }
    [Theory]
    [InlineData("20260102120000.000000+000", true)]
    [InlineData("*********************", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task WindowsDates_AreParsedOrExplicitlyUnknown(string? date, bool known)
    {
        Rows("Win32_PnPSignedDriver", new Dictionary<string, object?> { ["DeviceName"] = "Example driver", ["DriverDate"] = date });
        var snapshot = await _service.ReadAsync(default);
        var value = Assert.IsType<SystemInformationDate>(Value(snapshot, SystemInformationCategory.Drivers, "DriverDate"));
        Assert.Equal(known, value.Value.HasValue);
        Assert.Null(snapshot.Sections.Single(section => section.Category == SystemInformationCategory.Drivers).Error);
    }
    [Fact]
    public async Task UnavailableCategory_IsNotAClaimOfNoHardware_AndOtherQueriesContinue()
    {
        _query.Setup(x => x.ReadAsync("Win32_BIOS", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SystemInformationException("Win32_BIOS: access denied"));
        Rows("Win32_BaseBoard", new Dictionary<string, object?> { ["Product"] = "Example board" });
        var snapshot = await _service.ReadAsync(default);
        Assert.Equal("Win32_BIOS: access denied", snapshot.Sections.Single(section => section.Category == SystemInformationCategory.Firmware).Error);
        Assert.Single(snapshot.Sections.Single(section => section.Category == SystemInformationCategory.Motherboard).Items);
        Assert.Equal(13, snapshot.Sections.Count);
    }
    [Theory]
    [InlineData("not a number")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    public async Task InvalidProviderValues_AreReported_InsteadOfFabricatingNumericResults(string value)
    {
        Rows("Win32_ComputerSystem", new Dictionary<string, object?> { ["TotalPhysicalMemory"] = value });
        var snapshot = await _service.ReadAsync(default);
        Assert.NotNull(snapshot.Sections[0].Error);
        Assert.Empty(snapshot.Sections[0].Items);
        Assert.Null(snapshot.Sections[1].Error);
    }
    [Fact]
    public async Task Cancellation_DoesNotBecomeAPartialSuccessfulSnapshot()
    {
        using var cts = new CancellationTokenSource();
        _query.Setup(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => { cts.Cancel(); return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ReadAsync(cts.Token));
        _query.Verify(x => x.ReadAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Theory]
    [InlineData("Win32_Processor WHERE Name='test'", "Name")]
    [InlineData("Win32_Processor", "Name FROM Win32_BIOS")]
    public async Task WmiBoundary_RejectsNonIdentifierInputs_BeforeUsingWindows(string type, string field)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new WindowsSystemInformationQuery().ReadAsync(type, [field], default));
    }
}
