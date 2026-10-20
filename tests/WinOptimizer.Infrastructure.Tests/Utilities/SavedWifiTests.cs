using System.Xml;
using Moq;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Utilities;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Exercises profile semantics with synthetic keys; never reads the machine's WLAN inventory.</summary>
public sealed class SavedWifiTests
{
    private static string Profile(string authentication, string encryption, string sharedKey, string oneX = "false") => $$"""
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1"><name>Example</name>
          <MSM><security><authEncryption><authentication>{{authentication}}</authentication><encryption>{{encryption}}</encryption><useOneX>{{oneX}}</useOneX></authEncryption>
          {{sharedKey}}</security></MSM>
        </WLANProfile>
        """;

    [Fact]
    public void PlaintextKeysPreserveSpecialCharactersAndNeverEnterProfileDiagnostics()
    {
        var result = WindowsSavedWifi.ParseProfile(Profile("WPA2PSK", "AES",
            "<sharedKey><protected>false</protected><keyMaterial> fake &amp; &lt;key&gt; </keyMaterial></sharedKey>"), "Café & Work", "Adapter 1");
        Assert.Equal(" fake & <key> ", result.Password);
        Assert.Equal(WifiPasswordState.Available, result.PasswordState);
        Assert.Equal("Café & Work", result.Name);
        Assert.Equal("Adapter 1", result.Adapter);
        Assert.Equal("WPA2PSK", result.Authentication);
        Assert.DoesNotContain(result.Password!, result.ToString());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("")]
    public void ProtectedOrUnspecifiedKeysAreNeverReturnedAsPasswords(string protectedFlag)
    {
        var result = WindowsSavedWifi.ParseProfile(Profile("WPA2PSK", "AES",
            $"<sharedKey><protected>{protectedFlag}</protected><keyMaterial>encrypted-blob</keyMaterial></sharedKey>"), "Example", "Wi-Fi");
        Assert.Null(result.Password);
        Assert.Equal(WifiPasswordState.Unavailable, result.PasswordState);
    }

    [Theory]
    [InlineData("open", "none", "false", WifiPasswordState.OpenNetwork)]
    [InlineData("WPA2", "AES", "true", WifiPasswordState.NotStored)]
    [InlineData("WPA3ENT", "AES", "1", WifiPasswordState.NotStored)]
    [InlineData("WPA2PSK", "AES", "false", WifiPasswordState.Unavailable)]
    [InlineData("open", "WEP", "false", WifiPasswordState.Unavailable)]
    public void MissingKeysDistinguishOpenEnterpriseAndUnavailableProfiles(string auth, string cipher, string oneX, WifiPasswordState expected)
    {
        var result = WindowsSavedWifi.ParseProfile(Profile(auth, cipher, "", oneX), "Example", "Wi-Fi");
        Assert.Null(result.Password); Assert.Equal(expected, result.PasswordState);
    }

    [Theory]
    [InlineData("not XML")]
    [InlineData("<wrongRoot/>")]
    [InlineData("<!DOCTYPE WLANProfile [<!ENTITY key SYSTEM 'file:///nonexistent'>]><WLANProfile>&key;</WLANProfile>")]
    public void InvalidOrExternalEntityXmlIsRejected(string xml) =>
        Assert.Throws<XmlException>(() => WindowsSavedWifi.ParseProfile(xml, "Example", "Wi-Fi"));

    [Fact]
    public async Task ServicePreservesPartialResultsWithoutStartingWorkDuringConstruction()
    {
        var source = new WifiSource();
        var user = new Mock<IInteractiveUserService>();
        var service = new SavedWifiService(source, user.Object);
        Assert.Equal(0, source.Reads); user.VerifyNoOtherCalls();
        var snapshot = new SavedWifiSnapshot([new("Example", "Wi-Fi", "", "", null, WifiPasswordState.Unavailable)], "Adapter 2: unavailable");
        source.Snapshot = snapshot;
        Assert.Same(snapshot, await service.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AlternateAccountElevationNeverReadsTheAdministratorsProfiles()
    {
        var source = new WifiSource();
        var user = new Mock<IInteractiveUserService>();
        user.SetupGet(value => value.IsOtsElevation).Returns(true);
        var service = new SavedWifiService(source, user.Object);
        await Assert.ThrowsAsync<UtilityOperationException>(() => service.ReadAsync(CancellationToken.None));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task CancelledPageDoesNotStartNativeDiscovery()
    {
        var source = new WifiSource();
        var service = new SavedWifiService(source, Mock.Of<IInteractiveUserService>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(new CancellationToken(true)));
        Assert.Equal(0, source.Reads);
    }

    private sealed class WifiSource : ISavedWifiSource
    {
        public int Reads { get; private set; }
        public SavedWifiSnapshot Snapshot { get; set; } = new([], "");
        public SavedWifiSnapshot Read(CancellationToken cancellationToken) { Reads++; return Snapshot; }
    }
}
