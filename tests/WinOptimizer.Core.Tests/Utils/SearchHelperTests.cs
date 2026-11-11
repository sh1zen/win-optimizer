using FluentAssertions;
using WinOptimizer.Core.Features.Common.Utils;
using Xunit;

namespace WinOptimizer.Core.Tests.Utils;

public class SearchHelperTests
{

    [Fact]
    public void MatchesSearchTerm_MultiWordSearch_AllTermsMustMatch()
    {
        // Both "power" and "plan" must appear in at least one field each
        SearchHelper.MatchesSearchTerm("power plan", "Power Settings", "Active Plan")
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesSearchTerm_MultiWordSearch_OneTermMissing_ReturnsFalse()
    {
        // "xyz" does not appear in any field
        SearchHelper.MatchesSearchTerm("power xyz", "Power Settings", "General")
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesSearchTerm_NullFieldsInArray_HandledGracefully()
    {
        SearchHelper.MatchesSearchTerm("test", null!, "test value", null!)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesSearchTerm_AllNullFields_ReturnsFalse()
    {
        SearchHelper.MatchesSearchTerm("test", null!, null!)
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesSearchTerm_EmptyFieldsArray_ReturnsFalse()
    {
        SearchHelper.MatchesSearchTerm("test")
            .Should().BeFalse();
    }
}
