using FluentAssertions;
using WinOptimizer.Core.Features.Common.Utils;
using Xunit;

namespace WinOptimizer.Core.Tests.Utils;

public class SearchHelperTests
{
    [Fact]
    public void PreparedSearchRetainsLiteralTabsAndMatchesTermsAcrossFields()
    {
        var query = SearchHelper.Prepare("  POWER\tPLAN  Windows  ");
        Assert.True(query.Matches(["Power\tPlan", null, "Windows settings"]));
        Assert.False(query.Matches(["Power Plan", "Windows settings"]));
        Assert.False(query.Matches(["Power\tPlan", "Other settings"]));
        Assert.True(SearchHelper.Prepare(" \t ").Matches([]));
    }

    [Fact]
    public void MatchesSearchTerm_MultiWordSearch_AllTermsMustMatch()
    {
        // Both "power" and "plan" must appear in at least one field each
        SearchHelper.MatchesSearchTerm("power plan", "Power Settings", "Active Plan")
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesSearchTerm_NoFieldsWithText_ReturnsFalse()
    {
        SearchHelper.MatchesSearchTerm("test", null!, null!).Should().BeFalse();
        SearchHelper.MatchesSearchTerm("test").Should().BeFalse();
    }
}
