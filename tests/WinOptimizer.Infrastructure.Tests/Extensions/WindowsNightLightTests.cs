using WinOptimizer.Infrastructure.Features.Extensions;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Extensions;

public sealed class WindowsNightLightTests
{
    [Theory]
    [InlineData("434201000A0201002A06ECA0F4BE062A2B0E1343420100D00A02C614B09FD1E6F89FAFEE0100000000", false, null)]
    [InlineData("434201000A0201002A06ECA0F4BE062A2B0E0743420100100000000000", true, null)]
    [InlineData("434201000A0201002A06ECA0F4BE062A2B0E0843420100C21E0000000000", false, false)]
    public void ReadsRecognizedCloudStoreState(string hex, bool enabled, bool? supported)
    {
        Assert.Equal(new WindowsNightLight.State(enabled, supported, 1742540908), WindowsNightLight.Decode(Convert.FromHexString(hex)));
    }

    [Fact]
    public void WritesManualStateNewerThanCloudAndPreservesSupportFlag()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(30);
        var data = WindowsNightLight.Encode(true, new(false, true, 40), 50, now);

        Assert.Equal(new WindowsNightLight.State(true, true, 52), WindowsNightLight.Decode(data));
        // The changed state records a manual transition and the actual FILETIME separately from the envelope version.
        Assert.Contains("1000D00A02C614", Convert.ToHexString(data));
        using var ticks = new MemoryStream();
        using var writer = new BinaryWriter(ticks);
        writer.Write7BitEncodedInt64(now.ToFileTime());
        Assert.Contains("C614" + Convert.ToHexString(ticks.ToArray()), Convert.ToHexString(data));
        Assert.Equal(new WindowsNightLight.State(false, null, 55),
            WindowsNightLight.Decode(WindowsNightLight.Encode(false, new(true, null, 53), 50, now)));
    }

    [Theory]
    [InlineData("434201000A0201002A06ECA0F4BE062A2B0E0843420100C21E00000000")]
    [InlineData("434201000A0201002A06ECA0F4BE062A2B0E0843420100C21F0100000000")]
    public void RejectsTruncatedOrUnknownState(string hex)
    {
        Assert.Throws<InvalidDataException>(() => WindowsNightLight.Decode(Convert.FromHexString(hex)));
    }
}
