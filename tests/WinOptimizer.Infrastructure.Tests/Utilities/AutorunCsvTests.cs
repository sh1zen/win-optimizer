using WinOptimizer.Core.Features.Utilities;
using WinOptimizer.Infrastructure.Features.Management;
using Xunit;

namespace WinOptimizer.Infrastructure.Tests.Utilities;

/// <summary>Checks the real Autorunsc boundary format using synthetic CSV, never local startup data.</summary>
public sealed class AutorunCsvTests
{
    internal const string Header = "\"Time\",\"Entry Location\",\"Entry\",\"Enabled\",\"Category\",\"Profile\",\"Description\",\"Signer\",\"Company\",\"Image Path\",\"Version\",\"Launch String\"\n";

    [Fact]
    public void QuotedCommasQuotesAndMultilineCommandsArePreserved()
    {
        var csv = Header + "\"2026-01-01\",\"HKCU\\Run\",\"Example, app\",\"enabled\",\"Logon\",\"User\",\"Description\",\"(Verified) Example\",\"Example\",\"C:\\Example.exe\",\"1\",\"\"\"C:\\Example.exe\"\" --start\n--other\"";
        var entry = Assert.Single(AutorunCsvReader.Read(csv));
        Assert.Equal("Example, app", entry.Name); Assert.Equal("\"C:\\Example.exe\" --start\n--other", entry.Command);
        Assert.True(entry.Enabled); Assert.False(entry.CanToggle); Assert.Equal("(Verified) Example", entry.Signer);
    }

    [Theory]
    [InlineData("enabled", true)] [InlineData("disabled", false)] [InlineData("unknown", null)] [InlineData("", null)]
    public void StateIsNeverInferredFromPresence(string state, bool? expected)
    {
        var entry = Assert.Single(AutorunCsvReader.Read(Header + $"\"\",\"location\",\"entry\",\"{state}\",\"Explorer\",\"System\",\"\",\"\",\"\",\"\",\"\",\"\""));
        Assert.Equal(expected, entry.Enabled);
    }

    [Theory]
    [InlineData("")] [InlineData("error: permission denied")] [InlineData("\"Entry\",\"Enabled\"\n\"x\",\"enabled\"")]
    public void MissingOrUnexpectedHeadersFail(string csv) => Assert.Throws<UtilityOperationException>(() => AutorunCsvReader.Read(csv));

    [Fact]
    public void AnIncompleteRecordFailsInsteadOfSilentlyDroppingTheEntry() =>
        Assert.Throws<UtilityOperationException>(() => AutorunCsvReader.Read(Header + "\"time\",\"location\",\"entry\""));

    [Fact]
    public void IdenticalEntryNamesInDifferentLocationsKeepDistinctIdentities()
    {
        var record = "\"\",\"location\",\"same name\",\"enabled\",\"Explorer\",\"System\",\"\",\"\",\"\",\"\",\"\",\"\"\n";
        var entries = AutorunCsvReader.Read("\uFEFF" + Header + record + record);
        Assert.Equal(2, entries.Count); Assert.NotEqual(entries[0].Id, entries[1].Id);
    }
}
