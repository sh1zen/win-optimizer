using Moq;
using WinOptimizer.Core.Features.Common.Enums;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class AcerBatteryFirmwareTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(85)]
    public async Task DiscoversFixedModesAndConfirmsBothChangesThroughTheNormalService(int unsupportedPercentage)
    {
        var wmi = new AcerWmiFake();
        var service = Service(wmi);
        var before = await service.ReadAsync();
        Assert.Equal(new[] { 80, 100 }, before.SupportedLimits);
        Assert.Equal(100, before.ActiveLimit);
        Assert.Equal("Acer Battery Health Mode", before.Provider);
        Assert.Equal("community.acer.com", before.HelpUri.Host);
        Assert.Empty(wmi.Writes);
        Assert.Equal(80, (await service.SetLimitAsync(80)).ActiveLimit);
        Assert.Equal(100, (await service.SetLimitAsync(100)).ActiveLimit);
        Assert.Equal(new byte[] { 1, 0 }, wmi.Writes);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(unsupportedPercentage));
        Assert.Equal(2, wmi.Writes.Count);
    }

    [Fact]
    public async Task UnconfirmedWriteIsNeverReportedAsSuccess()
    {
        var wmi = new AcerWmiFake { AcceptWrites = false };
        var service = Service(wmi);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80));
        Assert.Equal(100, (await service.ReadAsync()).ActiveLimit);
        Assert.Single(wmi.Writes);
    }

    [Theory]
    [InlineData(false, true, (byte)3)]
    [InlineData(true, false, (byte)3)]
    [InlineData(true, true, (byte)0)]
    [InlineData(true, true, (byte)2)]
    public async Task MissingInactiveOrUnsupportedFirmwareOffersNoLimits(bool endpoint, bool active, byte functions)
    {
        var wmi = new AcerWmiFake { HasEndpoint = endpoint, Active = active, Functions = functions };
        var service = Service(wmi);
        var state = await service.ReadAsync();
        Assert.False(state.CanApply);
        Assert.Null(state.ActiveLimit);
        Assert.Equal(!endpoint || !active ? BatteryControlIssue.InterfaceUnavailable : BatteryControlIssue.FirmwareUnsupported, state.Issue);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80));
        Assert.Empty(wmi.Writes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public async Task MissingOrMalformedNamedStatusDisablesControl(int length)
    {
        var wmi = new AcerWmiFake { Status = length < 0 ? null : new byte[length] };
        var state = await Service(wmi).ReadAsync();
        Assert.False(state.CanApply);
        Assert.Equal(BatteryControlIssue.DetectionFailed, state.Issue);
        Assert.Contains("invalid", state.Diagnostic);
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task MissingCapabilityIsAnExplicitDiagnostic()
    {
        var state = await Service(new AcerWmiFake { Functions = null }).ReadAsync();
        Assert.False(state.CanApply);
        Assert.Contains("capabilities", state.Diagnostic);
    }

    [Fact]
    public async Task ReadFailureReportsDiagnosticsAndPreventsWrites()
    {
        var wmi = new AcerWmiFake { ReadError = new UnauthorizedAccessException("Administrator access is required.") };
        var service = Service(wmi);
        Assert.Contains("Administrator", (await service.ReadAsync()).Diagnostic);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80));
        Assert.Empty(wmi.Writes);
    }

    [Fact]
    public async Task WriteFailureRetainsThePartialChangeWarning()
    {
        var wmi = new AcerWmiFake { WriteError = new UtilityOperationException("Firmware rejected the request.") };
        var error = await Assert.ThrowsAsync<UtilityOperationException>(() => Service(wmi).SetLimitAsync(80));
        Assert.Contains("some firmware changes may already be applied", error.Message);
        Assert.Same(wmi.WriteError, error.InnerException);
    }

    [Theory]
    [InlineData(WinOptimizerMode.Builder)]
    [InlineData(WinOptimizerMode.ConfigReview)]
    public async Task RestrictedModesNeverInvokeFirmware(WinOptimizerMode mode)
    {
        var wmi = new AcerWmiFake();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(wmi, mode).SetLimitAsync(80));
        Assert.Equal(0, wmi.Calls);
    }

    [Fact]
    public async Task MissingBatterySkipsOemDiscovery()
    {
        var wmi = new AcerWmiFake { HasBattery = false };
        Assert.False((await Service(wmi).ReadAsync()).CanApply);
        Assert.Equal(0, wmi.Calls);
    }

    [Fact]
    public async Task PrefersActivePredatorEndpointAndTargetsItsExactInstance()
    {
        var wmi = new AcerWmiFake { OtherEndpoint = @"ACPI\Other\0_0" };
        Assert.Equal(80, (await Service(wmi).SetLimitAsync(80)).ActiveLimit);
        Assert.Single(wmi.Writes);
    }

    [Fact]
    public async Task SingleActiveNonPredatorEndpointIsSupported()
    {
        var wmi = new AcerWmiFake { Instance = @"ACPI\Other\0_0" };
        Assert.Equal(80, (await Service(wmi).SetLimitAsync(80)).ActiveLimit);
    }

    [Fact]
    public async Task AmbiguousEndpointsAreNotInvoked()
    {
        var wmi = new AcerWmiFake { Instance = @"ACPI\One\0_0", OtherEndpoint = @"ACPI\Two\0_0" };
        var service = Service(wmi);
        var state = await service.ReadAsync();
        Assert.False(state.CanApply);
        Assert.Contains("Multiple", state.Diagnostic);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80));
        Assert.Equal(0, wmi.Calls);
    }

    [Theory]
    [InlineData("Acer", true)]
    [InlineData("ACER INC.", true)]
    [InlineData("Dell Inc.", false)]
    public void MatchesOnlyAcer(string manufacturer, bool expected) =>
        Assert.Equal(expected, new AcerBatteryFirmware(new AcerWmiFake()).Matches(manufacturer));

    /// <summary>Exercises production discovery, mode guards, writing and readback against a fake WMI boundary.</summary>
    private static BatteryChargeService Service(AcerWmiFake wmi, WinOptimizerMode mode = WinOptimizerMode.Normal)
    {
        var applicationMode = new Mock<IApplicationModeService>();
        applicationMode.SetupGet(value => value.CurrentMode).Returns(mode);
        applicationMode.Setup(value => value.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        return new(wmi, [new DellBatteryFirmware(wmi), new HpBatteryFirmware(wmi), new AcerBatteryFirmware(wmi)], applicationMode.Object);
    }

    /// <summary>Models Acer's named method outputs and checks instance selection, argument types and masks.</summary>
    private sealed class AcerWmiFake : IBatteryWmi
    {
        public bool HasBattery { get; init; } = true;
        public bool HasEndpoint { get; init; } = true;
        public bool Active { get; init; } = true;
        public bool AcceptWrites { get; init; } = true;
        public string Instance { get; init; } = @"ACPI\PNP0C14\APGe_0";
        public string? OtherEndpoint { get; init; }
        public byte? Functions { get; init; } = 3;
        public byte[]? Status { get; init; } = new byte[5];
        public Exception? ReadError { get; init; }
        public Exception? WriteError { get; init; }
        public int Calls { get; private set; }
        public List<byte> Writes { get; } = [];

        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string query)
        {
            if (scope == @"root\wmi" && new[] { "BatteryStaticData", "BatteryFullChargedCapacity", "BatteryStatus", "BatteryCycleCount", "BatteryTemperature" }
                .Any(name => query == $"SELECT * FROM {name} WHERE Active=TRUE")) return [];
            if (query.Contains("Win32_ComputerSystem"))
                return [new Dictionary<string, object?> { ["Manufacturer"] = "Acer", ["Model"] = "Test laptop" }];
            if (query.Contains("Win32_Battery"))
                return HasBattery ? [new Dictionary<string, object?> { ["EstimatedChargeRemaining"] = 72 }] : [];
            Assert.Equal(AcerBatteryFirmware.Scope, scope);
            Assert.Equal("SELECT * FROM BatteryControl WHERE Active=TRUE", query);
            if (!HasEndpoint) return [];
            var endpoints = new List<IReadOnlyDictionary<string, object?>>
                { new Dictionary<string, object?> { ["InstanceName"] = Instance, ["Active"] = Active } };
            if (OtherEndpoint != null)
                endpoints.Insert(0, new Dictionary<string, object?> { ["InstanceName"] = OtherEndpoint, ["Active"] = true });
            return endpoints;
        }

        public IReadOnlyDictionary<string, object?> Invoke(string scope, string className, string method,
            IReadOnlyDictionary<string, object> arguments, string? resultProperty, string? instanceName = null)
        {
            Calls++;
            Assert.Equal(AcerBatteryFirmware.Scope, scope);
            Assert.Equal("BatteryControl", className);
            Assert.Equal(Instance, instanceName);
            Assert.Null(resultProperty);
            Assert.Equal((byte)1, Assert.IsType<byte>(arguments["uBatteryNo"]));
            if (method == "GetBatteryHealthControlStatus")
            {
                Assert.Equal(3, arguments.Count);
                Assert.Equal((byte)1, Assert.IsType<byte>(arguments["uFunctionQuery"]));
                Assert.Equal(new byte[2], Assert.IsType<byte[]>(arguments["uReserved"]));
                if (ReadError != null) throw ReadError;
                return new Dictionary<string, object?> { ["uFunctionList"] = Functions, ["uFunctionStatus"] = Status,
                    ["uReturn"] = new byte[] { 0xff, 0xff } };
            }
            Assert.Equal("SetBatteryHealthControl", method);
            Assert.Equal(4, arguments.Count);
            Assert.Equal((byte)1, Assert.IsType<byte>(arguments["uFunctionMask"]));
            Assert.Equal(new byte[5], Assert.IsType<byte[]>(arguments["uReservedIn"]));
            var enabled = Assert.IsType<byte>(arguments["uFunctionStatus"]);
            if (WriteError != null) throw WriteError;
            Writes.Add(enabled);
            if (AcceptWrites && Status is { Length: 5 }) Status[0] = enabled;
            return new Dictionary<string, object?> { ["uReturn"] = (ushort)0xffff };
        }
    }
}
