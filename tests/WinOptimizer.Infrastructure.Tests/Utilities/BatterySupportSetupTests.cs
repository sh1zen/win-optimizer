using System.ComponentModel;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

public sealed class BatterySupportSetupTests
{
    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "Laptop", "www.asus.com", BatterySupportSetupKind.DriverAndUtility)]
    [InlineData(" Acer ", "Predator", "www.acer.com", BatterySupportSetupKind.DriverAndUtility)]
    [InlineData("Dell Inc.", "Laptop", "www.dell.com", BatterySupportSetupKind.OemUtility)]
    [InlineData("HP", "Laptop", "support.hp.com", BatterySupportSetupKind.Firmware)]
    [InlineData("Hewlett-Packard", "Laptop", "support.hp.com", BatterySupportSetupKind.Firmware)]
    [InlineData("LENOVO", "Laptop", "pcsupport.lenovo.com", BatterySupportSetupKind.DriverAndUtility)]
    [InlineData("Micro-Star International Co., Ltd.", "Laptop", "www.msi.com", BatterySupportSetupKind.OemUtility)]
    [InlineData("MSI", "Laptop", "www.msi.com", BatterySupportSetupKind.OemUtility)]
    [InlineData("SAMSUNG ELECTRONICS CO., LTD.", "Galaxy Book", "www.samsung.com", BatterySupportSetupKind.OemUtility)]
    [InlineData("LG Electronics", "gram", "www.lg.com", BatterySupportSetupKind.OemUtility)]
    [InlineData("Microsoft Corporation", "Surface Laptop", "support.microsoft.com", BatterySupportSetupKind.OemUtility)]
    public void OfficialSetupRoutesDoNotClaimHardwareCapability(string manufacturer, string model, string host, BatterySupportSetupKind kind)
    {
        var setup = Assert.IsType<BatterySupportSetup>(BatterySupportCatalog.Find(manufacturer, model));
        Assert.Equal("https", setup.InstallUri.Scheme);
        Assert.Equal(host, setup.InstallUri.Host);
        Assert.Equal("https", setup.HelpUri.Scheme);
        Assert.Equal(kind, setup.Kind);
        Assert.NotEmpty(setup.PackageName);
    }

    [Theory]
    [InlineData("Unknown manufacturer", "Laptop")]
    [InlineData("Microsoft Corporation", "Virtual Machine")]
    public void UnverifiedMachinesHaveNoSuggestedPackage(string manufacturer, string model) =>
        Assert.Null(BatterySupportCatalog.Find(manufacturer, model));

    [Theory]
    [InlineData(2, BatteryControlIssue.InterfaceUnavailable)]
    [InlineData(3, BatteryControlIssue.InterfaceUnavailable)]
    [InlineData(5, BatteryControlIssue.AccessDenied)]
    [InlineData(6, BatteryControlIssue.DetectionFailed)]
    public async Task AsusMissingDriverIsDistinctFromPermissionAndDeviceErrors(int code, BatteryControlIssue expected)
    {
        var device = new ChargeDeviceFake { ReadError = new Win32Exception(code) };
        var state = await Service("ASUSTeK", "Laptop", new AsusBatteryFirmware(device)).ReadAsync();
        Assert.Equal(expected, state.Issue);
        Assert.False(state.CanApply);
        Assert.NotNull(state.Setup);
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task ExistingDriverWithNoChargeEndpointIsFirmwareUnsupported()
    {
        var device = new ChargeDeviceFake();
        var state = await Service("ASUS", "Laptop", new AsusBatteryFirmware(device)).ReadAsync();
        Assert.Equal(BatteryControlIssue.FirmwareUnsupported, state.Issue);
    }

    [Fact]
    public async Task RecheckDetectsSupportAfterADriverBecomesAvailableWithoutApplyingALimit()
    {
        var device = new ChargeDeviceFake { ReadError = new Win32Exception(2) };
        var service = Service("ASUS", "Laptop", new AsusBatteryFirmware(device));
        Assert.Equal(BatteryControlIssue.InterfaceUnavailable, (await service.ReadAsync()).Issue);
        device.ReadError = null;
        device.Limit = 80;
        var restored = await service.ReadAsync();
        Assert.True(restored.CanApply);
        Assert.Equal(BatteryControlIssue.None, restored.Issue);
        Assert.Equal(80, restored.ActiveLimit);
        Assert.Equal(0, device.Writes);
    }

    [Theory]
    [InlineData("LENOVO")]
    [InlineData("MSI")]
    [InlineData("Samsung")]
    [InlineData("LG Electronics")]
    public async Task OemAppGuidanceDoesNotInventDirectControl(string manufacturer)
    {
        var service = Service(manufacturer, "Laptop");
        var state = await service.ReadAsync();
        Assert.Equal(BatteryControlIssue.NoProvider, state.Issue);
        Assert.NotNull(state.Setup);
        Assert.False(state.CanApply);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.SetLimitAsync(80));
    }

    [Fact]
    public async Task ProviderFailurePreservesItsDiagnosticInsteadOfClaimingAMissingDriver()
    {
        var provider = new ProviderFake { ReadError = new UtilityOperationException("Invalid firmware reply.") };
        var state = await Service("Acer", "Laptop", provider).ReadAsync();
        Assert.Equal(BatteryControlIssue.DetectionFailed, state.Issue);
        Assert.Equal("Invalid firmware reply.", state.Diagnostic);
    }

    [Theory]
    [InlineData(BatteryControlIssue.FirmwareLocked)]
    [InlineData(BatteryControlIssue.FirmwareUnsupported)]
    [InlineData(BatteryControlIssue.InterfaceUnavailable)]
    public async Task ProviderIssueSurvivesServiceDiscovery(BatteryControlIssue issue)
    {
        var provider = new ProviderFake { State = new("Dell BIOS WMI", [], null, issue) };
        Assert.Equal(issue, (await Service("Dell", "Laptop", provider).ReadAsync()).Issue);
    }

    /// <summary>Supplies deterministic inventory while exercising the production discovery and mode guard.</summary>
    private static BatteryChargeService Service(string manufacturer, string model, params IBatteryFirmwareProvider[] providers)
    {
        var mode = new Mock<IApplicationModeService>();
        mode.Setup(value => value.BeginSystemOperation()).Returns(Mock.Of<IDisposable>());
        return new(new InventoryFake(manufacturer, model), providers, mode.Object);
    }

    /// <summary>Models driver availability without opening a device or issuing IOCTLs.</summary>
    private sealed class ChargeDeviceFake : IAsusChargeDevice
    {
        public Exception? ReadError { get; set; }
        public int? Limit { get; set; }
        public int Writes { get; private set; }
        public int? ReadLimit() => ReadError == null ? Limit : throw ReadError;
        public void SetLimit(int percentage) { Writes++; Limit = percentage; }
    }

    /// <summary>Supplies provider outcomes to verify the service's public classification.</summary>
    private sealed class ProviderFake : IBatteryFirmwareProvider
    {
        public Exception? ReadError { get; init; }
        public FirmwareChargeState State { get; init; } = new("OEM", [], null);
        public bool Matches(string manufacturer) => true;
        public FirmwareChargeState Read() => ReadError == null ? State : throw ReadError;
        public void SetLimit(int percentage) => throw new InvalidOperationException("Unexpected firmware write.");
    }

    /// <summary>Only implements local computer and battery inventory; unexpected hardware calls fail.</summary>
    private sealed class InventoryFake(string manufacturer, string model) : IBatteryWmi
    {
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope, string query)
        {
            if (scope == @"root\wmi" && new[] { "BatteryStaticData", "BatteryFullChargedCapacity", "BatteryStatus", "BatteryCycleCount", "BatteryTemperature" }
                .Any(name => query == $"SELECT * FROM {name} WHERE Active=TRUE")) return [];
            Assert.Equal(@"root\cimv2", scope);
            return query switch
            {
                "SELECT Manufacturer, Model FROM Win32_ComputerSystem" => [new Dictionary<string, object?> { ["Manufacturer"] = manufacturer, ["Model"] = model }],
                "SELECT EstimatedChargeRemaining FROM Win32_Battery" => [new Dictionary<string, object?> { ["EstimatedChargeRemaining"] = 70 }],
                _ => throw new InvalidOperationException("Unexpected inventory query: " + query)
            };
        }
        public IReadOnlyDictionary<string, object?> Invoke(string scope, string className, string method,
            IReadOnlyDictionary<string, object> arguments, string? resultProperty, string? instanceName = null) =>
            throw new InvalidOperationException("Unexpected hardware method: " + method);
    }
}
